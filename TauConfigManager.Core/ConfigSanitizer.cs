using System.Text;

namespace TauConfigManager.Core;

public static class ConfigSanitizer
{
    private static readonly string[] DeviceIdentityKeys =
    {
        "serial_number",
        "software_version",
        "hardware_version",
        "engineering_sw_version",
        "imei",
    };

    private static readonly string[] HardwareDetectionKeys =
    {
        "hw_version_detect_num",
        "hw_version_detect_num_save",
        "hw_version_detect_colour",
        "hw_version_pcb",
    };

    public static string SanitizeText(string path, string content, ExportOptions options)
    {
        var normalizedPath = NormalizePath(path);
        var result = new StringBuilder(content.Length);

        foreach (var rawLine in SplitLines(content))
        {
            var line = rawLine.TrimStart();
            if (!options.IncludeManagementMac && IsManagementMacLine(normalizedPath, line))
            {
                continue;
            }

            if (!options.IncludeDeviceIdentity && ContainsKey(line, DeviceIdentityKeys))
            {
                continue;
            }

            if (!options.IncludeHardwareDetection && ContainsKey(line, HardwareDetectionKeys))
            {
                continue;
            }

            result.Append(rawLine);
        }

        return result.ToString();
    }

    public static bool ShouldIncludePath(string path, ExportOptions options)
    {
        var normalized = NormalizePath(path);
        if (!options.IncludeLicenses && IsLicensePath(normalized))
        {
            return false;
        }

        return true;
    }

    private static bool IsManagementMacLine(string normalizedPath, string trimmedLine)
    {
        return normalizedPath.StartsWith("etc/eth0", StringComparison.Ordinal) &&
               trimmedLine.StartsWith("MAC=", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsKey(string trimmedLine, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            if (trimmedLine.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) ||
                trimmedLine.StartsWith("option " + key + " ", StringComparison.OrdinalIgnoreCase) ||
                trimmedLine.StartsWith("option " + key + "\t", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static bool IsLicensePath(string normalizedPath)
    {
        return normalizedPath.Equals("etc/config/license", StringComparison.Ordinal) ||
               normalizedPath.Equals("usr/local/apache/htdocs/conf/sct_tau.lic", StringComparison.Ordinal) ||
               normalizedPath.Equals("usr/local/apache/htdocs/conf/license", StringComparison.Ordinal) ||
               normalizedPath.StartsWith("usr/local/apache/htdocs/conf/license/", StringComparison.Ordinal);
    }

    private static IEnumerable<string> SplitLines(string content)
    {
        if (content.Length == 0)
        {
            yield break;
        }

        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n')
            {
                continue;
            }

            yield return content.Substring(start, i - start + 1);
            start = i + 1;
        }

        if (start < content.Length)
        {
            yield return content.Substring(start);
        }
    }
}
