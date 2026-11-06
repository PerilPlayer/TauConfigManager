using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Reflection;
using TauConfigManager.Core;

if (args.Length >= 2 && args[0] == "--test-preservation")
{
    await TestPreservationOnDeviceAsync(args[1]);
    return;
}

if (args.Length >= 2 && args[0] == "--smoke")
{
    await RunSmokeAsync(args[1]);
    return;
}

if (args.Length >= 2 && args[0] == "--dump-import-script")
{
    DumpImportScript(args[1]);
    return;
}

if (args.Length >= 3 && args[0] == "--compare")
{
    await ComparePackageAsync(args[1], args[2]);
    return;
}

if (args.Length >= 3 && args[0] == "--exec")
{
    await RunCommandAsync(args[1], string.Join(' ', args.Skip(2)));
    return;
}

var tests = new (string Name, Action Body)[]
{
    ("sanitizer removes device identity by default", SanitizerRemovesDeviceIdentity),
    ("sanitizer excludes license files by default", SanitizerExcludesLicenseFilesByDefault),
    ("package roundtrip verifies payload checksum", PackageRoundtripVerifiesChecksum),
    ("package roundtrip preserves managed scope metadata", PackageRoundtripPreservesManagedScopeMetadata),
    ("tar roundtrip preserves file mode", TarRoundtripPreservesFileMode),
    ("tar read keeps last duplicate entry", TarReadKeepsLastDuplicateEntry),
    ("tar read keeps empty regular files", TarReadKeepsEmptyRegularFiles),
    ("export paths include non-wireless supplements", ExportPathsIncludeNonWirelessSupplements),
    ("export paths include observed web routing vpn files", ExportPathsIncludeObservedWebRoutingVpnFiles),
    ("import script avoids device shell functions", ImportScriptAvoidsDeviceShellFunctions),
    ("export script uses tar file list", ExportScriptUsesTarFileList),
    ("telnet wrapper hides completion token from echoed command", TelnetWrapperHidesCompletionToken),
    ("telnet upload token is hidden from echoed command", TelnetUploadTokenIsHiddenFromEchoedCommand),
    ("telnet protocol strips negotiation bytes", TelnetProtocolStripsNegotiationBytes),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine(ex.Message);
    }
}

if (failures > 0)
{
    Environment.Exit(1);
}

static void SanitizerRemovesDeviceIdentity()
{
    var options = ExportOptions.SafeDefaults();
    var easycwmp = """
config device
    option serial_number 'old-device'
    option software_version 'V12.020004-08'
    option hardware_version 'P12.020104-04-QD'
    option product_class 'TAU'
""";
    var eth0 = """
IP=192.168.17.200
MASK=255.255.255.0
MAC=F8:FF:5F:9C:03:5C
""";

    var sanitizedEasycwmp = ConfigSanitizer.SanitizeText("etc/config/easycwmp", easycwmp, options);
    var sanitizedEth0 = ConfigSanitizer.SanitizeText("etc/eth0-setting", eth0, options);

    AssertFalse(sanitizedEasycwmp.Contains("old-device", StringComparison.Ordinal));
    AssertFalse(sanitizedEasycwmp.Contains("software_version", StringComparison.Ordinal));
    AssertFalse(sanitizedEasycwmp.Contains("hardware_version", StringComparison.Ordinal));
    AssertTrue(sanitizedEasycwmp.Contains("product_class", StringComparison.Ordinal));
    AssertFalse(sanitizedEth0.Contains("MAC=", StringComparison.Ordinal));
    AssertTrue(sanitizedEth0.Contains("IP=192.168.17.200", StringComparison.Ordinal));
}

static void SanitizerExcludesLicenseFilesByDefault()
{
    var safe = ExportOptions.SafeDefaults();
    var withLicense = new ExportOptions { IncludeLicenses = true };

    AssertFalse(ConfigSanitizer.ShouldIncludePath("usr/local/apache/htdocs/conf/sct_tau.lic", safe));
    AssertFalse(ConfigSanitizer.ShouldIncludePath("usr/local/apache/htdocs/conf/license/sct_tau.lic", safe));
    AssertTrue(ConfigSanitizer.ShouldIncludePath("usr/local/apache/htdocs/conf/sct_tau.lic", withLicense));
}

static void PackageRoundtripVerifiesChecksum()
{
    var manifest = new TauConfigManifest
    {
        SourceIp = "192.168.17.200",
        SoftwareVersion = "V12.020004-08",
        HardwareVersion = "P12.020104-04-QD",
        EngineeringVersion = "V12.020004-08_beta001",
        HardwareProfile = "V02C04",
        FeatureHash = "abc123",
    };
    var payload = Encoding.UTF8.GetBytes("payload-data");
    using var package = new MemoryStream();

    TauConfigPackage.Write(package, manifest, payload);
    package.Position = 0;

    var read = TauConfigPackage.Read(package);
    AssertEqual("192.168.17.200", read.Manifest.SourceIp);
    AssertEqual("V12.020004-08", read.Manifest.SoftwareVersion);
    AssertEqual(TauConfigPackage.ComputeSha256(payload), read.Manifest.PayloadSha256);
    AssertEqual("payload-data", Encoding.UTF8.GetString(read.Payload));
}

static void PackageRoundtripPreservesManagedScopeMetadata()
{
    var manifest = new TauConfigManifest
    {
        ConfigScopeVersion = TauConfigPaths.ScopeVersion,
        SourceIp = "192.168.13.21",
        SoftwareVersion = "V12.020004-19",
        HardwareProfile = "P12.020104-06-QD",
        Files = ["etc/config/route"],
        ManagedPaths = ["etc/config/route", "usr/local/apache/htdocs/conf/gre"],
        FileHashes = new Dictionary<string, string>
        {
            ["etc/config/route"] = "abc123",
        },
    };

    using var package = new MemoryStream();
    TauConfigPackage.Write(package, manifest, Encoding.UTF8.GetBytes("payload"));
    package.Position = 0;

    var read = TauConfigPackage.Read(package);
    AssertEqual(TauConfigPaths.ScopeVersion, read.Manifest.ConfigScopeVersion);
    AssertTrue(read.Manifest.ManagedPaths.Contains("usr/local/apache/htdocs/conf/gre", StringComparer.Ordinal));
    AssertEqual("abc123", read.Manifest.FileHashes["etc/config/route"]);
}

static void TarRoundtripPreservesFileMode()
{
    var payload = TarGzArchive.Write(
    [
        new TarGzEntry
        {
            Path = "ltehost/vlan.sh",
            Data = Encoding.ASCII.GetBytes("#!/bin/sh\n"),
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                   UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                   UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
        },
    ]);

    var entries = TarGzArchive.Read(payload);
    var mode = entries.Single().Mode;
    AssertTrue((mode & UnixFileMode.UserExecute) != 0);
    AssertTrue((mode & UnixFileMode.GroupExecute) != 0);
    AssertTrue((mode & UnixFileMode.OtherExecute) != 0);
}

static void TarReadKeepsLastDuplicateEntry()
{
    using var output = new MemoryStream();
    using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
    using (var writer = new TarWriter(gzip, leaveOpen: true))
    {
        WriteTarEntry(writer, "ltehost/tunnel/tunnel1.conf", "old");
        WriteTarEntry(writer, "ltehost/tunnel/tunnel1.conf", "new");
    }

    var entries = TarGzArchive.Read(output.ToArray());
    AssertTrue(entries.Count == 1);
    AssertEqual("ltehost/tunnel/tunnel1.conf", entries[0].Path);
    AssertEqual("new", Encoding.UTF8.GetString(entries[0].Data));
}

static void TarReadKeepsEmptyRegularFiles()
{
    using var output = new MemoryStream();
    using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
    using (var writer = new TarWriter(gzip, leaveOpen: true))
    {
        WriteTarEntry(writer, "usr/local/apache/htdocs/conf/rsa.conf", "");
    }

    var entries = TarGzArchive.Read(output.ToArray());
    AssertTrue(entries.Count == 1);
    AssertEqual("usr/local/apache/htdocs/conf/rsa.conf", entries[0].Path);
    AssertTrue(entries[0].Data.Length == 0);
}

static void WriteTarEntry(TarWriter writer, string path, string content)
{
    var data = Encoding.UTF8.GetBytes(content);
    var entry = new UstarTarEntry(TarEntryType.RegularFile, path)
    {
        DataStream = new MemoryStream(data, writable: false),
        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        ModificationTime = DateTimeOffset.UnixEpoch,
    };
    writer.WriteEntry(entry);
}

static void ExportPathsIncludeNonWirelessSupplements()
{
    var paths = TauConfigPaths.GetExportPaths(ExportOptions.SafeDefaults());
    AssertTrue(paths.Contains("usr/local/apache/htdocs/conf/gre", StringComparer.Ordinal));
    AssertTrue(paths.Contains("usr/local/apache/htdocs/conf/pki", StringComparer.Ordinal));
    AssertTrue(paths.Contains("usr/local/apache/htdocs/conf/ospf", StringComparer.Ordinal));
    AssertTrue(paths.Contains("etc/config/staticroute", StringComparer.Ordinal));
    AssertTrue(paths.Contains("etc/config/pim", StringComparer.Ordinal));
    AssertTrue(paths.Contains("ltehost/config/arping_bd_req_ip.cfg", StringComparer.Ordinal));
    AssertTrue(paths.Contains("tmp/tau_config_tool_state/snmp_cpu_check", StringComparer.Ordinal));
    AssertTrue(paths.Contains("ltehost/config/soft_sim_modem1.cfg", StringComparer.Ordinal));
    AssertTrue(paths.Contains("ltehost/config/soft_sim_modem2.cfg", StringComparer.Ordinal));
    AssertTrue(paths.Contains("ltehost/config/double_soft_sim.cfg", StringComparer.Ordinal));
    AssertTrue(paths.Contains("ltehost/config/sim_select_detecting.cfg", StringComparer.Ordinal));
    AssertFalse(paths.Contains("etc/config/license", StringComparer.Ordinal));
}

static void ExportPathsIncludeObservedWebRoutingVpnFiles()
{
    var safePaths = TauConfigPaths.GetExportPaths(ExportOptions.SafeDefaults());
    AssertTrue(safePaths.Contains("ltehost/vlan.sh", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("ltehost/restore_cfg", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("etc/quagga/zebra.conf", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("etc/quagga/ospfd.conf", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("etc/pimd.conf", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("etc/keepalived/keepalived.conf", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("etc/ipsec.conf", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("etc/ipsec.secrets", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("etc/ipsec.d", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("usr/local/apache/htdocs/conf/menu.xml", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("usr/local/apache/htdocs/conf/menu-all.xml", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("usr/local/apache/htdocs/conf/features.xml", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("usr/local/apache/htdocs/conf/vpnstartup.conf", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("usr/local/apache/htdocs/conf/ipsec.conf", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("usr/local/apache/htdocs/conf/pimd.conf", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("usr/local/apache/htdocs/conf/tunnel1.conf", StringComparer.Ordinal));
    AssertTrue(safePaths.Contains("ltehost/tunnel/tunnel100.conf", StringComparer.Ordinal));
    AssertFalse(safePaths.Contains("ltehost/tunnel", StringComparer.Ordinal));
    AssertFalse(safePaths.Contains("usr/local/apache/htdocs/conf/sct_tau.lic", StringComparer.Ordinal));

    var licensePaths = TauConfigPaths.GetExportPaths(new ExportOptions { IncludeLicenses = true });
    AssertTrue(licensePaths.Contains("usr/local/apache/htdocs/conf/sct_tau.lic", StringComparer.Ordinal));
    AssertTrue(licensePaths.Contains("etc/config/license", StringComparer.Ordinal));
}

static void ImportScriptAvoidsDeviceShellFunctions()
{
    var manifest = new TauConfigManifest
    {
        PayloadSha256 = "payload-hash",
        IncludesDeviceIdentity = true,
        IncludesManagementMac = true,
        IncludesHardwareDetection = true,
        Files = ["etc/config/easycwmp", "etc/config/route"],
        ManagedPaths = ["etc/config/easycwmp", "etc/config/route", "usr/local/apache/htdocs/conf/gre"],
        FileHashes = new Dictionary<string, string>
        {
            ["etc/config/easycwmp"] = "111",
            ["etc/config/route"] = "222",
        },
    };
    var options = new ImportOptions
    {
        Force = true,
        DeleteMissingManagedFiles = true,
        VerifyFileHashes = true,
        RunPostImportHooks = true,
        PreserveManagementAddress = false,
    };

    var method = typeof(TauDeviceService).GetMethod("BuildImportScript", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("BuildImportScript");
    var script = (string)method.Invoke(null,
    [
        "http://192.168.13.203:12345/payload.tar.gz",
        manifest.PayloadSha256,
        manifest.Files,
        manifest.ManagedPaths,
        manifest.FileHashes,
        manifest,
        options,
    ])!;

    AssertFalse(script.Contains("() {", StringComparison.Ordinal));
    AssertFalse(script.Contains("${", StringComparison.Ordinal));
    AssertFalse(script.Contains("elif ", StringComparison.Ordinal));
    AssertFalse(script.Contains("case ", StringComparison.Ordinal));
    AssertFalse(script.Contains("for ", StringComparison.Ordinal));
    AssertFalse(script.Contains("<<", StringComparison.Ordinal));
    AssertTrue(script.Contains("while IFS= read -r f", StringComparison.Ordinal));
    AssertTrue(script.Contains("tar -tzf \"$pkg\" > \"$pkgfiles\"", StringComparison.Ordinal));
    AssertTrue(script.Contains("printf '%s\\n' 'usr/local/apache/htdocs/conf/gre' >> \"$managed_paths\"", StringComparison.Ordinal));
    AssertTrue(script.Contains("printf '%s\\n' '222 etc/config/route' >> \"$expected_hashes\"", StringComparison.Ordinal));
    AssertTrue(script.Contains("find \"/$p\" -type f", StringComparison.Ordinal));
    AssertTrue(script.Contains("sha256sum \"/$f\"", StringComparison.Ordinal));
    AssertTrue(script.Contains("__VERIFY_MISMATCH__=$f", StringComparison.Ordinal));
    AssertTrue(script.Contains("__POST_HOOK__=lan-arp-detect", StringComparison.Ordinal));
    AssertTrue(script.Contains("/ltehost/arping_bd_req_ip_loop.sh", StringComparison.Ordinal));
    AssertTrue(script.Contains("/ltehost/show_system_info.sh 1", StringComparison.Ordinal));
    AssertTrue(script.Contains("/ltehost/show_system_info.sh -q", StringComparison.Ordinal));
    AssertFalse(script.Contains("switch_manage", StringComparison.Ordinal));
    AssertFalse(script.Contains("sw_config_manage", StringComparison.Ordinal));
    AssertFalse(script.Contains("grep -F -x", StringComparison.Ordinal));
    AssertTrue(script.Contains("__PHASE__=preserve-skip", StringComparison.Ordinal));
    AssertTrue(script.Contains("__PHASE__=restore-preserved-skip", StringComparison.Ordinal));
    AssertFalse(script.Contains("serial_number\" >> \"$preserve_jobs\"", StringComparison.Ordinal));
}

static void ExportScriptUsesTarFileList()
{
    var method = typeof(TauDeviceService).GetMethod("BuildExportScript", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("BuildExportScript");
    var script = (string)method.Invoke(null, [ExportOptions.SafeDefaults()])!;

    AssertTrue(script.Contains("tar -czf \"$archive\" -C / -T \"$existing\"", StringComparison.Ordinal));
    AssertTrue(script.Contains("/ltehost/show_system_info.sh get > \"$state_dir/snmp_cpu_check\"", StringComparison.Ordinal));
    AssertTrue(script.Contains("tmp/tau_config_tool_state/snmp_cpu_check", StringComparison.Ordinal));
    AssertFalse(script.Contains("$(cat \"$existing\")", StringComparison.Ordinal));
}

static void DumpImportScript(string packagePath)
{
    using var file = File.OpenRead(packagePath);
    var package = TauConfigPackage.Read(file);
    var manifest = package.Manifest;
    var managedPaths = manifest.ManagedPaths.Count > 0 ? manifest.ManagedPaths : manifest.Files;
    var options = new ImportOptions
    {
        Force = true,
        DeleteMissingManagedFiles = true,
        VerifyFileHashes = true,
        RunPostImportHooks = true,
    };

    var method = typeof(TauDeviceService).GetMethod("BuildImportScript", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("BuildImportScript");
    var script = (string)method.Invoke(null,
    [
        "http://192.168.13.203:64151/payload.tar.gz",
        manifest.PayloadSha256,
        manifest.Files,
        managedPaths,
        manifest.FileHashes,
        manifest,
        options,
    ])!;

    var lines = script.Replace("\r\n", "\n").Split('\n');
    for (var i = 1; i <= lines.Length; i++)
    {
        Console.WriteLine($"{i,4}: {lines[i - 1]}");
    }
}

static void TelnetWrapperHidesCompletionToken()
{
    const string token = "__TAU_TOOL_DONE_0123456789abcdef0123456789abcdef__";
    var method = typeof(TelnetConnection).GetMethod("BuildWrappedCommand", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("BuildWrappedCommand");
    var wrapped = (string)method.Invoke(null, ["true", token])!;

    AssertFalse(wrapped.Contains(token, StringComparison.Ordinal));
    AssertTrue(wrapped.Contains("tau_done_a=", StringComparison.Ordinal));
    AssertTrue(wrapped.Contains("tau_done_b=", StringComparison.Ordinal));
}

static void TelnetUploadTokenIsHiddenFromEchoedCommand()
{
    const string token = "__TAU_UPLOAD_DONE_0123456789abcdef0123456789abcdef__";
    var method = typeof(TelnetConnection).GetMethod("BuildSplitTokenEchoCommand", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("BuildSplitTokenEchoCommand");
    var command = (string)method.Invoke(null, [token, "tau_upload", ""])!;
    var linesMethod = typeof(TelnetConnection).GetMethod("BuildSplitTokenEchoLines", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("BuildSplitTokenEchoLines");
    var lines = ((IEnumerable<string>)linesMethod.Invoke(null, [token, "tau_upload", ""])!).ToList();

    AssertFalse(command.Contains(token, StringComparison.Ordinal));
    AssertTrue(command.Contains("tau_upload_a=", StringComparison.Ordinal));
    AssertTrue(command.Contains("tau_upload_b=", StringComparison.Ordinal));
    AssertTrue(command.Contains("echo \"$tau_upload_a$tau_upload_b\"", StringComparison.Ordinal));
    AssertTrue(lines.Count == 3);
    AssertFalse(lines.Any(line => line.Contains(token, StringComparison.Ordinal)));
    AssertTrue(lines.All(line => line.Length < 80));
}

static async Task ComparePackageAsync(string host, string packagePath)
{
    var manifest = TauDeviceService.ReadManifest(packagePath);
    var options = new ExportOptions
    {
        IncludeDeviceIdentity = manifest.IncludesDeviceIdentity,
        IncludeManagementMac = manifest.IncludesManagementMac,
        IncludeHardwareDetection = manifest.IncludesHardwareDetection,
        IncludeLicenses = manifest.IncludesLicenses,
    };
    var service = new TauDeviceService();
    service.Log += Console.WriteLine;
    var connection = new TauConnectionOptions
    {
        Host = host,
        Port = 23,
        UserName = "root",
        Password = "sct123",
        Timeout = TimeSpan.FromSeconds(25),
    };

    var diffs = await service.CompareAsync(connection, packagePath, options);
    Console.WriteLine($"DIFF_COUNT={diffs.Count}");
    foreach (var diff in diffs.Take(200))
    {
        Console.WriteLine($"{diff.Status}\t{diff.Path}\tpackage={diff.PackageHash}\tdevice={diff.DeviceHash}");
    }
}

static async Task RunCommandAsync(string host, string command)
{
    var connection = new TauConnectionOptions
    {
        Host = host,
        Port = 23,
        UserName = "root",
        Password = "sct123",
        Timeout = TimeSpan.FromSeconds(25),
    };

    await using var telnet = new TelnetConnection(connection);
    await telnet.ConnectAndLoginAsync();
    var result = await telnet.RunCommandAsync(command, TimeSpan.FromMinutes(2));
    Console.Write(result.Output);
    if (result.ExitCode != 0)
    {
        Environment.Exit(result.ExitCode);
    }
}

static void TelnetProtocolStripsNegotiationBytes()
{
    var bytes = new byte[]
    {
        255, 251, 1,
        255, 253, 31,
        (byte)'A', (byte)'T', (byte)'9', (byte)'1',
        255, 252, 3,
        (byte)'\r', (byte)'\n',
    };

    var text = TelnetProtocol.DecodeText(bytes);
    AssertEqual("AT91\r\n", text);
}

static async Task TestPreservationOnDeviceAsync(string host)
{
    // Exercise the generated production restoration script against isolated files,
    // using the same awk and UCI versions as the actual device.
    var root = "/tmp/tau_preserve_test_" + Guid.NewGuid().ToString("N");
    var method = typeof(TauDeviceService).GetMethod("BuildRestorePreservedScript", BindingFlags.NonPublic | BindingFlags.Static)!;
    var restore = (string)method.Invoke(null, [true])!;
    static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    var old = "config device\n option serial_number 'target-serial'\n option software_version 'target-version'\nconfig notifications\n option enable '1'\n";
    var current = "config device\n option product_class 'TAU'\nconfig notifications\n option enable '1'\n";
    var jobs = $"{root}/old|{root}/easycwmp|serial_number\n{root}/old|{root}/easycwmp|software_version\n{root}/old_eth|{root}/eth|MAC\n{root}/old_eth|{root}/eth|IP\n";
    var script = $"set -e\nroot={Quote(root)}\nmkdir -p \"$root\"\npreserve_jobs=\"$root/jobs\"\n" +
        $"printf '%s' {Quote(old)} > \"$root/old\"\n" +
        $"printf '%s' {Quote(current)} > \"$root/easycwmp\"\n" +
        $"printf '%s' {Quote("MAC=00:11:22:33:44:55\nIP=172.19.13.62\n")} > \"$root/old_eth\"\n" +
        $"printf '%s' {Quote("IP=192.168.13.21\nMASK=255.255.255.0\n")} > \"$root/eth\"\n" +
        $"printf '%s' {Quote(jobs)} > \"$root/jobs\"\n" + restore + "\n" +
        "[ \"$(uci -c \"$root\" get easycwmp.@device[0].serial_number)\" = target-serial ]\n" +
        "[ \"$(uci -c \"$root\" get easycwmp.@device[0].software_version)\" = target-version ]\n" +
        "[ \"$(uci -c \"$root\" get easycwmp.@notifications[0].enable)\" = 1 ]\n" +
        "[ -z \"$(uci -c \"$root\" get easycwmp.@notifications[0].serial_number 2>/dev/null)\" ]\n" +
        "grep '^IP=172.19.13.62$' \"$root/eth\"\ngrep '^MAC=00:11:22:33:44:55$' \"$root/eth\"\n" +
        restore + "\n" + // A second restoration must remain valid and not duplicate the options.
        "[ \"$(grep -c 'option serial_number' \"$root/easycwmp\")\" = 1 ]\n" +
        "echo __PRESERVATION_TEST_PASSED__\nrm -f \"$root/old\" \"$root/easycwmp\" \"$root/old_eth\" \"$root/eth\" \"$root/jobs\"\nrmdir \"$root\"\n";
    await using var connection = new TelnetConnection(new TauConnectionOptions { Host = host });
    await connection.ConnectAndLoginAsync();
    var result = await connection.RunScriptAsync(script, TimeSpan.FromMinutes(1));
    if (result.ExitCode != 0 || !result.Output.Contains("__PRESERVATION_TEST_PASSED__", StringComparison.Ordinal))
        throw new InvalidOperationException(result.Output);
    Console.WriteLine("PASS generated restoration preserves UCI section, management IP, MAC, and repeated execution");
}

static void AssertTrue(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("Expected condition to be true.");
    }
}

static void AssertFalse(bool condition)
{
    if (condition)
    {
        throw new InvalidOperationException("Expected condition to be false.");
    }
}

static void AssertEqual(string expected, string actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static async Task RunSmokeAsync(string host)
{
    try
    {
        var service = new TauDeviceService();
        service.Log += Console.WriteLine;
        var connection = new TauConnectionOptions
        {
            Host = host,
            Port = 23,
            UserName = "root",
            Password = "sct123",
            Timeout = TimeSpan.FromSeconds(25),
        };

        var info = await service.ReadDeviceInfoAsync(connection);
        Console.WriteLine($"DEVICE software={info.SoftwareVersion} hardware={info.HardwareVersion} profile={info.HardwareProfile} ip={info.ManagementIp}");

        var output = Path.Combine(Path.GetTempPath(), $"tau_smoke_{DateTime.Now:yyyyMMdd_HHmmss}.taucfg");
        await service.ExportAsync(connection, output, ExportOptions.SafeDefaults());
        using var file = File.OpenRead(output);
        var package = TauConfigPackage.Read(file);
        Console.WriteLine($"EXPORT path={output} files={package.Manifest.Files.Count} sha256={package.Manifest.PayloadSha256}");
    }
    catch (Exception ex)
    {
        Console.WriteLine("SMOKE_ERROR=" + ex.Message);
        Environment.Exit(1);
    }
}
