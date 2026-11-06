using System.Text.Json.Serialization;

namespace TauConfigManager.Core;

public sealed class ExportOptions
{
    public bool IncludeDeviceIdentity { get; set; }
    public bool IncludeManagementMac { get; set; }
    public bool IncludeHardwareDetection { get; set; }
    public bool IncludeLicenses { get; set; }

    public static ExportOptions SafeDefaults()
    {
        return new ExportOptions
        {
            IncludeDeviceIdentity = false,
            IncludeManagementMac = false,
            IncludeHardwareDetection = false,
            IncludeLicenses = false,
        };
    }
}

public sealed class ImportOptions
{
    public bool Force { get; set; }
    public bool DeleteMissingManagedFiles { get; set; }
    public bool VerifyFileHashes { get; set; } = true;
    public bool RunPostImportHooks { get; set; } = true;
    public bool ScheduleReboot { get; set; }
    public bool PreserveManagementAddress { get; set; } = true;
}

public sealed class TauConnectionOptions
{
    public string Host { get; set; } = "192.168.17.200";
    public int Port { get; set; } = 23;
    public string UserName { get; set; } = "root";
    public string Password { get; set; } = "sct123";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
}

public sealed class TauDeviceInfo
{
    public string Host { get; set; } = "";
    public string SoftwareVersion { get; set; } = "";
    public string HardwareVersion { get; set; } = "";
    public string EngineeringVersion { get; set; } = "";
    public string HardwareProfile { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string ManagementIp { get; set; } = "";
    public string ManagementMac { get; set; } = "";
    public string FeatureHash { get; set; } = "";
    public IReadOnlyList<string> Features { get; set; } = Array.Empty<string>();
}

public sealed class TauConfigManifest
{
    public string PackageFormat { get; set; } = "tau-config-v1";
    public string ConfigScopeVersion { get; set; } = "";
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
    public string SourceIp { get; set; } = "";
    public string SoftwareVersion { get; set; } = "";
    public string HardwareVersion { get; set; } = "";
    public string EngineeringVersion { get; set; } = "";
    public string HardwareProfile { get; set; } = "";
    public string FeatureHash { get; set; } = "";
    public string PayloadSha256 { get; set; } = "";
    public bool IncludesDeviceIdentity { get; set; }
    public bool IncludesManagementMac { get; set; }
    public bool IncludesHardwareDetection { get; set; }
    public bool IncludesLicenses { get; set; }
    public List<string> Files { get; set; } = new();
    public List<string> ManagedPaths { get; set; } = new();
    public Dictionary<string, string> FileHashes { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public string Summary => $"{SoftwareVersion} / {HardwareProfile} / {ExportedAt:yyyy-MM-dd HH:mm:ss}";
}

public sealed class TauConfigPackageData
{
    public TauConfigPackageData(TauConfigManifest manifest, byte[] payload)
    {
        Manifest = manifest;
        Payload = payload;
    }

    public TauConfigManifest Manifest { get; }
    public byte[] Payload { get; }
}

public sealed class PackagePrecheckResult
{
    public bool IsCompatible => Errors.Count == 0;
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
}

public sealed class ConfigDiffEntry
{
    public string Path { get; set; } = "";
    public string Status { get; set; } = "";
    public string PackageHash { get; set; } = "";
    public string DeviceHash { get; set; } = "";
}

public sealed class ImportResult
{
    public bool Success { get; set; }
    public string BackupPath { get; set; } = "";
    public string Output { get; set; } = "";
    public int DeletedFiles { get; set; }
    public bool FileVerificationPassed { get; set; }
    public List<string> VerificationFailures { get; set; } = new();
}
