using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TauConfigManager.Core;

public sealed class TauDeviceService
{
    public event Action<string>? Log;

    public async Task<TauDeviceInfo> ReadDeviceInfoAsync(TauConnectionOptions connection, CancellationToken cancellationToken = default)
    {
        await using var telnet = new TelnetConnection(connection);
        LogLine($"连接 {connection.Host}:{connection.Port} ...");
        await telnet.ConnectAndLoginAsync(cancellationToken).ConfigureAwait(false);
        LogLine("Telnet 登录成功，读取设备信息。");

        var script = """
echo __INFO_BEGIN__
echo "SoftwareVersion=$(uci get easycwmp.@device[0].software_version 2>/dev/null)"
echo "HardwareVersion=$(uci get easycwmp.@device[0].hardware_version 2>/dev/null)"
echo "EngineeringVersion=$(uci get easycwmp.@device[0].engineering_sw_version 2>/dev/null)"
echo "SerialNumber=$(uci get easycwmp.@device[0].serial_number 2>/dev/null)"
if [ -f /etc/eth0-setting ]; then . /etc/eth0-setting; echo "ManagementIp=$IP"; echo "ManagementMac=$MAC"; fi
echo __RESERVE_BEGIN__
cat /ltehost/config/reserve_config.cfg 2>/dev/null
echo __RESERVE_END__
echo __FEATURES_BEGIN__
cat /usr/local/apache/htdocs/conf/features.xml 2>/dev/null
echo __FEATURES_END__
echo __INFO_END__
""";
        var result = await telnet.RunScriptAsync(script, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("读取设备信息失败：" + result.Output);
        }

        return ParseDeviceInfo(connection.Host, result.Output);
    }

    public async Task ExportAsync(TauConnectionOptions connection, string packagePath, ExportOptions options, CancellationToken cancellationToken = default)
    {
        var deviceInfo = await ReadDeviceInfoAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var telnet = new TelnetConnection(connection);
        await telnet.ConnectAndLoginAsync(cancellationToken).ConfigureAwait(false);

        LogLine("开始远程打包配置文件。");
        var rawPayload = await ExportPayloadAsync(telnet, options, cancellationToken).ConfigureAwait(false);
        LogLine($"远程配置包已接收，大小 {rawPayload.Length:N0} 字节。");

        var payload = TarGzArchive.Sanitize(rawPayload, options);
        var entries = TarGzArchive.Read(payload);
        var files = entries.Select(e => e.Path).ToList();
        var fileHashes = entries.ToDictionary(e => e.Path, e => TauConfigPackage.ComputeSha256(e.Data), StringComparer.Ordinal);
        var manifest = new TauConfigManifest
        {
            ConfigScopeVersion = TauConfigPaths.ScopeVersion,
            SourceIp = deviceInfo.ManagementIp.Length > 0 ? deviceInfo.ManagementIp : connection.Host,
            SoftwareVersion = deviceInfo.SoftwareVersion,
            HardwareVersion = deviceInfo.HardwareVersion,
            EngineeringVersion = deviceInfo.EngineeringVersion,
            HardwareProfile = deviceInfo.HardwareProfile,
            FeatureHash = deviceInfo.FeatureHash,
            IncludesDeviceIdentity = options.IncludeDeviceIdentity,
            IncludesManagementMac = options.IncludeManagementMac,
            IncludesHardwareDetection = options.IncludeHardwareDetection,
            IncludesLicenses = options.IncludeLicenses,
            Files = files,
            ManagedPaths = TauConfigPaths.GetExportPaths(options).ToList(),
            FileHashes = fileHashes,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(packagePath))!);
        await using var file = File.Create(packagePath);
        TauConfigPackage.Write(file, manifest, payload);
        LogLine($"配置包已保存：{packagePath}");
    }

    public async Task<IReadOnlyList<ConfigDiffEntry>> CompareAsync(TauConnectionOptions connection, string packagePath, ExportOptions options, CancellationToken cancellationToken = default)
    {
        await using var packageFile = File.OpenRead(packagePath);
        var package = TauConfigPackage.Read(packageFile);

        await using var telnet = new TelnetConnection(connection);
        await telnet.ConnectAndLoginAsync(cancellationToken).ConfigureAwait(false);
        var devicePayloadRaw = await ExportPayloadAsync(telnet, options, cancellationToken).ConfigureAwait(false);
        var devicePayload = TarGzArchive.Sanitize(devicePayloadRaw, options);
        return TarGzArchive.Compare(package.Payload, devicePayload);
    }

    public async Task<ImportResult> ImportAsync(
        TauConnectionOptions connection,
        string packagePath,
        IPAddress localAddress,
        bool force,
        CancellationToken cancellationToken = default)
    {
        return await ImportAsync(
            connection,
            packagePath,
            localAddress,
            new ImportOptions { Force = force },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportResult> ImportAsync(
        TauConnectionOptions connection,
        string packagePath,
        IPAddress localAddress,
        ImportOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var packageFile = File.OpenRead(packagePath);
        var package = TauConfigPackage.Read(packageFile);
        var target = await ReadDeviceInfoAsync(connection, cancellationToken).ConfigureAwait(false);
        var precheck = TauConfigPackage.Precheck(package.Manifest, target);
        if (!precheck.IsCompatible && !options.Force)
        {
            return new ImportResult
            {
                Success = false,
                Output = string.Join(Environment.NewLine, precheck.Errors),
            };
        }

        await using var server = new LocalHttpFileServer(localAddress, package.Payload);
        server.Start();
        var url = server.GetUrl(localAddress);
        LogLine($"Windows 临时 HTTP 服务已启动：{url}");

        await using var telnet = new TelnetConnection(connection);
        await telnet.ConnectAndLoginAsync(cancellationToken).ConfigureAwait(false);
        var managedPaths = package.Manifest.ManagedPaths.Count > 0
            ? package.Manifest.ManagedPaths
            : package.Manifest.Files;
        var script = BuildImportScript(
            url,
            package.Manifest.PayloadSha256,
            package.Manifest.Files,
            managedPaths,
            package.Manifest.FileHashes,
            package.Manifest,
            options);
        LogLine("开始导入，目标设备会先备份当前配置。");
        var result = await telnet.RunScriptAsync(script, TimeSpan.FromMinutes(6), cancellationToken).ConfigureAwait(false);
        var backupPath = ParseLineValue(result.Output, "__BACKUP__");
        var verifyFailedMarker = ParseLineValue(result.Output, "__VERIFY_FAILED__");
        var verificationFailures = ParseMarkerValues(result.Output, "__VERIFY_MISSING__", "__VERIFY_MISMATCH__");
        var scriptCompleted = !string.IsNullOrWhiteSpace(backupPath) && !string.IsNullOrWhiteSpace(verifyFailedMarker);
        if (result.ExitCode == 0 && !scriptCompleted)
        {
            verificationFailures.Add("导入脚本没有返回完成标记，可能未真正执行完成。");
        }

        return new ImportResult
        {
            Success = result.ExitCode == 0 && scriptCompleted && verificationFailures.Count == 0,
            BackupPath = backupPath,
            DeletedFiles = ParseIntLineValue(result.Output, "__DELETED_COUNT__"),
            FileVerificationPassed = scriptCompleted && (!options.VerifyFileHashes || verificationFailures.Count == 0),
            VerificationFailures = verificationFailures,
            Output = result.Output,
        };
    }

    public async Task<ImportResult> RollbackAsync(TauConnectionOptions connection, string backupTarGzPath, CancellationToken cancellationToken = default)
    {
        await using var telnet = new TelnetConnection(connection);
        await telnet.ConnectAndLoginAsync(cancellationToken).ConfigureAwait(false);
        var safePath = ShellQuote(backupTarGzPath);
        var script = $"""
set -e
if [ ! -f {safePath} ]; then
    echo "__ERROR__ backup not found"
    exit 2
fi
tar -xzf {safePath} -C /
sync
echo "__ROLLBACK__=ok"
""";
        var result = await telnet.RunScriptAsync(script, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        return new ImportResult
        {
            Success = result.ExitCode == 0 && result.Output.Contains("__ROLLBACK__=ok", StringComparison.Ordinal),
            BackupPath = backupTarGzPath,
            Output = result.Output,
        };
    }

    public static TauConfigManifest ReadManifest(string packagePath)
    {
        using var file = File.OpenRead(packagePath);
        return TauConfigPackage.Read(file).Manifest;
    }

    private async Task<byte[]> ExportPayloadAsync(TelnetConnection telnet, ExportOptions options, CancellationToken cancellationToken)
    {
        var result = await telnet.RunScriptAsync(BuildExportScript(options), TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("远程打包失败：" + result.Output);
        }

        return ExtractUuencodedPayload(result.Output);
    }

    private static string BuildExportScript(ExportOptions options)
    {
        var paths = string.Join("\n", TauConfigPaths.GetExportPaths(options));
        return $$"""
set -e
tmp="/tmp/tau_cfg_export_$$"
archive="${tmp}.tar.gz"
list="${tmp}.list"
existing="${tmp}.existing"
state_dir="/tmp/tau_config_tool_state"
mkdir -p "$state_dir"
if [ -x /ltehost/show_system_info.sh ]; then
    /ltehost/show_system_info.sh get > "$state_dir/snmp_cpu_check" 2>/dev/null || echo 0 > "$state_dir/snmp_cpu_check"
else
    echo 0 > "$state_dir/snmp_cpu_check"
fi
cat > "$list" <<'__TAU_PATHS__'
{{paths}}
__TAU_PATHS__
rm -f "$existing"
while read f; do
    [ -z "$f" ] && continue
    if [ -e "/$f" ]; then
        echo "$f" >> "$existing"
    fi
done < "$list"
if [ ! -s "$existing" ]; then
    echo "__ERROR__ no config files found"
    exit 3
fi
tar -czf "$archive" -C / -T "$existing"
if [ ! -s "$archive" ]; then
    echo "__ERROR__ archive is empty"
    rm -f "$archive" "$list" "$existing"
    exit 4
fi
echo "__SHA256__=$(sha256sum "$archive" | awk '{print $1}')"
echo "__PAYLOAD_BEGIN__"
uuencode -m "$archive" payload.tar.gz
echo "__PAYLOAD_END__"
rm -f "$archive" "$list" "$existing" "$state_dir/snmp_cpu_check"
rmdir "$state_dir" 2>/dev/null || true
""";
    }

    private static string BuildImportScript(
        string url,
        string expectedSha256,
        IReadOnlyList<string> files,
        IReadOnlyList<string> managedPaths,
        IReadOnlyDictionary<string, string> fileHashes,
        TauConfigManifest manifest,
        ImportOptions options)
    {
        var deleteMissing = options.DeleteMissingManagedFiles ? "1" : "0";
        var verifyHashes = options.VerifyFileHashes ? "1" : "0";
        var scheduleReboot = options.ScheduleReboot ? "1" : "0";
        var shouldPreserveIdentity = !manifest.IncludesDeviceIdentity;
        var shouldPreserveManagementMac = !manifest.IncludesManagementMac;
        var shouldPreserveManagementAddress = options.PreserveManagementAddress;
        var shouldPreserveHardwareDetection = !manifest.IncludesHardwareDetection;
        var preserveIdentity = shouldPreserveIdentity ? "1" : "0";
        var preserveManagementMac = shouldPreserveManagementMac ? "1" : "0";
        var preserveManagementAddress = shouldPreserveManagementAddress ? "1" : "0";
        var preserveHardwareDetection = shouldPreserveHardwareDetection ? "1" : "0";
        var preserveScript = BuildPreserveScript(
            shouldPreserveIdentity,
            shouldPreserveManagementMac,
            shouldPreserveHardwareDetection,
            shouldPreserveManagementAddress);
        var restorePreservedScript = BuildRestorePreservedScript(
            shouldPreserveIdentity || shouldPreserveManagementMac || shouldPreserveHardwareDetection || shouldPreserveManagementAddress);
        var postHooksScript = options.RunPostImportHooks
            ? BuildPostImportHooksScript()
            : "";
        var managedPathScript = BuildShellFileScript(
            "managed_paths",
            managedPaths.Select(NormalizeManifestPath));
        var expectedHashScript = BuildShellFileScript(
            "expected_hashes",
            fileHashes
                .Select(kvp => new
                {
                    Path = NormalizeManifestPath(kvp.Key),
                    Hash = kvp.Value,
                })
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .Select(item => $"{item.Hash} {item.Path}"));
        return $$"""
set -e
tmp="/tmp/tau_cfg_import_$$"
pkg="$tmp.tar.gz"
pkgfiles="$tmp.pkgfiles"
existing="$tmp.existing"
preserve_jobs="$tmp.preserve_jobs"
managed_paths="$tmp.managed_paths"
expected_hashes="$tmp.expected_hashes"
delete_candidates="$tmp.delete_candidates"
backup_dir="/mnt/mmcblk0p1/upload/tau_config_tool_backup/$(date +%Y%m%d_%H%M%S)"
mkdir -p "$backup_dir"
echo "__PHASE__=download"
wget -q -O "$pkg" "{{url}}"
echo "__PHASE__=checksum"
actual="$(sha256sum "$pkg" | awk '{print $1}')"
if [ "$actual" != "{{expectedSha256}}" ]; then
    echo "__ERROR__ checksum mismatch: $actual"
    rm -f "$pkg" "$pkgfiles" "$existing" "$preserve_jobs" "$managed_paths" "$expected_hashes" "$delete_candidates"
    exit 4
fi
echo "__PHASE__=list"
tar -tzf "$pkg" > "$pkgfiles"
{{managedPathScript}}
{{expectedHashScript}}

echo "__PHASE__=backup-list"
rm -f "$existing"
touch "$existing"
while IFS= read -r f; do
    if [ -z "$f" ]; then
        continue
    fi
    if [ -f "/$f" ]; then
        echo "$f" >> "$existing"
    fi
done < "$pkgfiles"

if [ "{{deleteMissing}}" = "1" ]; then
    while IFS= read -r p; do
        if [ -f "/$p" ]; then echo "$p" >> "$existing"; fi
        if [ -d "/$p" ]; then find "/$p" -type f | sed 's#^/##' >> "$existing"; fi
    done < "$managed_paths"
    awk '!seen[$0]++' "$existing" > "$existing.unique"
    mv "$existing.unique" "$existing"
fi

if [ -s "$existing" ]; then
    echo "__PHASE__=backup"
    tar -czf "$backup_dir/before_import.tar.gz" -C / -T "$existing"
else
    touch "$backup_dir/before_import.empty"
fi

{{preserveScript}}

deleted_count=0
echo "__PHASE__=delete"
if [ "{{deleteMissing}}" = "1" ]; then
    : > "$delete_candidates"
    if [ -s "$managed_paths" ]; then
        while IFS= read -r p; do
            if [ -z "$p" ]; then
                continue
            fi
            if [ -f "/$p" ]; then
                echo "$p" >> "$delete_candidates"
            fi
            if [ -d "/$p" ]; then
                find "/$p" -type f >> "$delete_candidates"
            fi
        done < "$managed_paths"
    fi
    if [ -s "$delete_candidates" ]; then
        while IFS= read -r candidate; do
            if [ -z "$candidate" ]; then
                continue
            fi
            rel="$(echo "$candidate" | sed 's#^/##')"
            if awk -v path="$rel" '$0 == path { found=1; exit } END { exit !found }' "$pkgfiles"; then
                continue
            fi
            if [ -f "/$rel" ]; then
                rm -f "/$rel"
                deleted_count=$((deleted_count + 1))
                echo "__DELETED__=$rel"
            fi
        done < "$delete_candidates"
    fi
fi

echo "__PHASE__=extract"
tar -xzf "$pkg" -C /

{{restorePreservedScript}}

echo "__PHASE__=post-hooks"
{{postHooksScript}}

sync

echo "__PHASE__=verify"
verify_failed=0
if [ "{{verifyHashes}}" = "1" ]; then
if [ -s "$expected_hashes" ]; then
    while IFS=' ' read -r expected_hash f; do
        if [ -z "$f" ]; then
            continue
        fi
        skip_verify=0
        if [ "{{preserveIdentity}}" = "1" ]; then
            if [ "$f" = "etc/config/easycwmp" ]; then
                skip_verify=1
            fi
        fi
        if [ "{{preserveManagementMac}}" = "1" ] || [ "{{preserveManagementAddress}}" = "1" ]; then
            if echo "$f" | grep '^etc/eth0.*-setting$' >/dev/null 2>&1; then
                skip_verify=1
            fi
        fi
        if [ "{{preserveHardwareDetection}}" = "1" ]; then
            if [ "$f" = "ltehost/config/reserve_config.cfg" ]; then
                skip_verify=1
            fi
        fi
        if [ "$skip_verify" = "1" ]; then
            continue
        fi
        if [ ! -f "/$f" ]; then
            echo "__VERIFY_MISSING__=$f"
            verify_failed=$((verify_failed + 1))
            continue
        fi
        actual_file_hash="$(sha256sum "/$f" | awk '{print $1}')"
        if [ "$actual_file_hash" != "$expected_hash" ]; then
            echo "__VERIFY_MISMATCH__=$f"
            verify_failed=$((verify_failed + 1))
            continue
        fi
    done < "$expected_hashes"
else
    if [ -s "$pkgfiles" ]; then
        while IFS= read -r f; do
            if [ -z "$f" ]; then
                continue
            fi
            if [ ! -f "/$f" ]; then
                echo "__VERIFY_MISSING__=$f"
                verify_failed=$((verify_failed + 1))
                continue
            fi
        done < "$pkgfiles"
    fi
fi
fi

echo "__PHASE__=done"
echo "__DELETED_COUNT__=$deleted_count"
echo "__VERIFY_FAILED__=$verify_failed"
echo "__BACKUP__=$backup_dir/before_import.tar.gz"
if [ "$verify_failed" -ne 0 ]; then
    rm -f "$pkg" "$pkgfiles" "$existing" "$preserve_jobs" "$managed_paths" "$expected_hashes" "$delete_candidates"
    rm -f /tmp/tau_config_tool_state/snmp_cpu_check
    rmdir /tmp/tau_config_tool_state 2>/dev/null || true
    exit 5
fi
if [ "{{scheduleReboot}}" = "1" ]; then
    echo "__REBOOT__=scheduled"
    (sleep 2; reboot) >/dev/null 2>&1 &
fi
rm -f "$pkg" "$pkgfiles" "$existing" "$preserve_jobs" "$managed_paths" "$expected_hashes" "$delete_candidates"
rm -f /tmp/tau_config_tool_state/snmp_cpu_check
rmdir /tmp/tau_config_tool_state 2>/dev/null || true
""";
    }

    private static string BuildPostImportHooksScript()
    {
        return """
echo "__POST_HOOKS__=direct-file-import"
set +e
if [ -f /ltehost/config/arping_bd_req_ip.cfg ]; then
    if [ -x /ltehost/arping_bd_req_ip_loop.sh ]; then
        /ltehost/arping_bd_req_ip_loop.sh >/dev/null 2>&1 &
        echo "__POST_HOOK__=lan-arp-detect"
    fi
fi
if [ -f /tmp/tau_config_tool_state/snmp_cpu_check ]; then
    snmp_cpu_check="$(cat /tmp/tau_config_tool_state/snmp_cpu_check 2>/dev/null)"
    if [ "$snmp_cpu_check" = "0" ]; then
        /ltehost/show_system_info.sh -q >/dev/null 2>&1
        echo "__POST_HOOK__=snmp-cpu-check:0"
    fi
    if [ "$snmp_cpu_check" != "0" ]; then
        /ltehost/show_system_info.sh 1 >/dev/null 2>&1
        echo "__POST_HOOK__=snmp-cpu-check:1"
    fi
fi
set -e
""";
    }

    private static string BuildPreserveScript(
        bool preserveIdentity,
        bool preserveManagementMac,
        bool preserveHardwareDetection,
        bool preserveManagementAddress)
    {
        if (!preserveIdentity && !preserveManagementMac && !preserveHardwareDetection && !preserveManagementAddress)
        {
            return """
echo "__PHASE__=preserve-skip"
: > "$preserve_jobs"
""";
        }

        var lines = new List<string>
        {
            "echo \"__PHASE__=preserve\"",
            "preserve_dir=\"$backup_dir/preserve\"",
            "mkdir -p \"$preserve_dir\"",
            ": > \"$preserve_jobs\"",
        };

        if (preserveIdentity)
        {
            lines.AddRange(
            [
                "if [ -f /etc/config/easycwmp ]; then",
                "    cp /etc/config/easycwmp \"$preserve_dir/easycwmp\"",
                "    echo \"$preserve_dir/easycwmp|/etc/config/easycwmp|serial_number\" >> \"$preserve_jobs\"",
                "    echo \"$preserve_dir/easycwmp|/etc/config/easycwmp|software_version\" >> \"$preserve_jobs\"",
                "    echo \"$preserve_dir/easycwmp|/etc/config/easycwmp|hardware_version\" >> \"$preserve_jobs\"",
                "    echo \"$preserve_dir/easycwmp|/etc/config/easycwmp|engineering_sw_version\" >> \"$preserve_jobs\"",
                "    echo \"$preserve_dir/easycwmp|/etc/config/easycwmp|imei\" >> \"$preserve_jobs\"",
                "fi",
            ]);
        }

        if (preserveManagementMac)
        {
            lines.AddRange(
            [
                "if [ -f /etc/eth0-setting ]; then",
                "    cp /etc/eth0-setting \"$preserve_dir/eth0-setting\"",
                "    echo \"$preserve_dir/eth0-setting|/etc/eth0-setting|MAC\" >> \"$preserve_jobs\"",
                "fi",
                "if [ -f /etc/eth0.1-setting ]; then",
                "    cp /etc/eth0.1-setting \"$preserve_dir/eth0.1-setting\"",
                "    echo \"$preserve_dir/eth0.1-setting|/etc/eth0.1-setting|MAC\" >> \"$preserve_jobs\"",
                "fi",
                "if [ -f /etc/eth0.2-setting ]; then",
                "    cp /etc/eth0.2-setting \"$preserve_dir/eth0.2-setting\"",
                "    echo \"$preserve_dir/eth0.2-setting|/etc/eth0.2-setting|MAC\" >> \"$preserve_jobs\"",
                "fi",
            ]);
        }

        if (preserveManagementAddress)
        {
            lines.AddRange(
            [
                "if [ -f /etc/eth0-setting ]; then",
                "    cp /etc/eth0-setting \"$preserve_dir/eth0-setting\"",
                "    echo \"$preserve_dir/eth0-setting|/etc/eth0-setting|IP\" >> \"$preserve_jobs\"",
                "    echo \"$preserve_dir/eth0-setting|/etc/eth0-setting|MASK\" >> \"$preserve_jobs\"",
                "    echo \"$preserve_dir/eth0-setting|/etc/eth0-setting|GATEWAY\" >> \"$preserve_jobs\"",
                "    echo \"$preserve_dir/eth0-setting|/etc/eth0-setting|Gateway\" >> \"$preserve_jobs\"",
                "fi",
            ]);
        }

        if (preserveHardwareDetection)
        {
            lines.AddRange(
            [
                "if [ -f /ltehost/config/reserve_config.cfg ]; then",
                "    cp /ltehost/config/reserve_config.cfg \"$preserve_dir/reserve_config.cfg\"",
                "    echo \"$preserve_dir/reserve_config.cfg|/ltehost/config/reserve_config.cfg|hw_version_detect_num\" >> \"$preserve_jobs\"",
                "    echo \"$preserve_dir/reserve_config.cfg|/ltehost/config/reserve_config.cfg|hw_version_detect_num_save\" >> \"$preserve_jobs\"",
                "    echo \"$preserve_dir/reserve_config.cfg|/ltehost/config/reserve_config.cfg|hw_version_detect_colour\" >> \"$preserve_jobs\"",
                "    echo \"$preserve_dir/reserve_config.cfg|/ltehost/config/reserve_config.cfg|hw_version_pcb\" >> \"$preserve_jobs\"",
                "fi",
            ]);
        }

        return string.Join("\n", lines);
    }

    private static string BuildRestorePreservedScript(bool hasPreserveJobs)
    {
        if (!hasPreserveJobs)
        {
            return """
echo "__PHASE__=restore-preserved-skip"
""";
        }

        return """
echo "__PHASE__=restore-preserved"
if [ -s "$preserve_jobs" ]; then
    while IFS='|' read -r old_file new_file key; do
        if [ ! -f "$old_file" ]; then
            continue
        fi
        if [ ! -f "$new_file" ]; then
            continue
        fi
        tmp_file="$new_file.tau_tool_tmp"
        cp "$new_file" "$tmp_file"
        repl="$(awk -v key="$key" '
            $0 ~ "^[[:space:]]*option[[:space:]]+" key "[[:space:]]" || $0 ~ "^[[:space:]]*" key "=" { print; exit }
        ' "$old_file")"
        if [ -z "$repl" ]; then
            continue
        fi
        section="$(awk -v key="$key" '
            /^[[:space:]]*config[[:space:]]/ { section=$0; gsub(/^[[:space:]]+|[[:space:]]+$/, "", section) }
            $0 ~ "^[[:space:]]*option[[:space:]]+" key "[[:space:]]" { print section; exit }
        ' "$old_file")"
        awk -v key="$key" -v repl="$repl" -v section="$section" '
            /^[[:space:]]*config[[:space:]]/ {
                if (active && !done) { print repl; done=1 }
                header=$0
                gsub(/^[[:space:]]+|[[:space:]]+$/, "", header)
                active=(header == section && !done)
            }
            $0 ~ "^[[:space:]]*option[[:space:]]+" key "[[:space:]]" || $0 ~ "^[[:space:]]*" key "=" {
                if (section == "" || active) {
                    if (!done) { print repl; done=1 }
                    next
                }
            }
            { print }
            END {
                if (!done && repl != "" && (section == "" || active)) { print repl }
                if (!done && section != "" && !active) { exit 6 }
            }
        ' "$tmp_file" > "$tmp_file.next"
        mv "$tmp_file.next" "$tmp_file"
        mv "$tmp_file" "$new_file"
    done < "$preserve_jobs"
fi
""";
    }

    private static string BuildShellFileScript(string shellVariableName, IEnumerable<string> lines)
    {
        if (shellVariableName.Length == 0 ||
            shellVariableName.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_'))
        {
            throw new ArgumentException("Invalid shell variable name.", nameof(shellVariableName));
        }

        var builder = new StringBuilder();
        builder.Append(": > \"$").Append(shellVariableName).AppendLine("\"");
        foreach (var line in lines
                     .Where(line => line.Length > 0)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            builder.Append("printf '%s\\n' ")
                .Append(ShellQuote(line))
                .Append(" >> \"$")
                .Append(shellVariableName)
                .AppendLine("\"");
        }

        return builder.ToString().TrimEnd();
    }

    private static string NormalizeManifestPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains('\r', StringComparison.Ordinal) ||
            normalized.Contains('\n', StringComparison.Ordinal) ||
            normalized.Contains("..", StringComparison.Ordinal) ||
            normalized.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException($"Invalid config path in package: {path}");
        }

        return normalized;
    }

    private static TauDeviceInfo ParseDeviceInfo(string host, string output)
    {
        var reserve = ExtractBlock(output, "__RESERVE_BEGIN__", "__RESERVE_END__");
        var featuresXml = ExtractBlock(output, "__FEATURES_BEGIN__", "__FEATURES_END__");
        var features = Regex.Matches(featuresXml, "<name>(.*?)</name>")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(v => v.Length > 0)
            .ToList();

        return new TauDeviceInfo
        {
            Host = host,
            SoftwareVersion = ParseLineValue(output, "SoftwareVersion"),
            HardwareVersion = ParseLineValue(output, "HardwareVersion"),
            EngineeringVersion = ParseLineValue(output, "EngineeringVersion"),
            SerialNumber = ParseLineValue(output, "SerialNumber"),
            ManagementIp = ParseLineValue(output, "ManagementIp"),
            ManagementMac = ParseLineValue(output, "ManagementMac"),
            HardwareProfile = ParseHardwareProfile(reserve),
            FeatureHash = HashText(string.Join('\n', features)),
            Features = features,
        };
    }

    private static string ParseHardwareProfile(string reserveConfig)
    {
        var current = "";
        foreach (var line in reserveConfig.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("hw_version_user_set=", StringComparison.Ordinal))
            {
                current = trimmed["hw_version_user_set=".Length..].Trim();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(current) || current == "-1")
        {
            return "";
        }

        foreach (var line in reserveConfig.Replace("\r\n", "\n").Split('\n'))
        {
            var parts = line.Trim().Split('=', 2);
            if (parts.Length == 2 &&
                !parts[0].Trim().Equals("hw_version_user_set", StringComparison.Ordinal) &&
                parts[1].Trim() == current)
            {
                return parts[0].Trim();
            }
        }

        return current;
    }

    private static byte[] ExtractUuencodedPayload(string output)
    {
        var start = output.IndexOf("begin-base64", StringComparison.Ordinal);
        if (start < 0)
        {
            start = output.IndexOf("begin ", StringComparison.Ordinal);
        }

        if (start < 0)
        {
            throw new InvalidDataException("远程输出中没有 uuencode payload。");
        }

        var lines = output[start..].Replace("\r\n", "\n").Split('\n');
        var base64 = new StringBuilder();
        var inPayload = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!inPayload)
            {
                if (trimmed.StartsWith("begin", StringComparison.Ordinal))
                {
                    inPayload = true;
                }
                continue;
            }

            if (trimmed == "====" || trimmed == "end")
            {
                break;
            }

            if (trimmed.Length > 0)
            {
                base64.Append(trimmed);
            }
        }

        return Convert.FromBase64String(base64.ToString());
    }

    private static string ExtractBlock(string text, string begin, string end)
    {
        var start = text.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }

        start += begin.Length;
        var stop = text.IndexOf(end, start, StringComparison.Ordinal);
        return stop < 0 ? text[start..] : text[start..stop];
    }

    private static string ParseLineValue(string output, string key)
    {
        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            var prefix = key + "=";
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return trimmed[prefix.Length..].Trim();
            }
        }

        return "";
    }

    private static int ParseIntLineValue(string output, string key)
    {
        var value = ParseLineValue(output, key);
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static List<string> ParseMarkerValues(string output, params string[] markers)
    {
        var values = new List<string>();
        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            foreach (var marker in markers)
            {
                var prefix = marker + "=";
                if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                {
                    values.Add(trimmed[prefix.Length..].Trim());
                    break;
                }
            }
        }

        return values;
    }

    private static string HashText(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string ShellQuote(string value)
    {
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private void LogLine(string message)
    {
        Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}
