using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace TauConfigManager.Core;

public static class TauConfigPackage
{
    private const string ManifestEntryName = "manifest.json";
    private const string PayloadEntryName = "payload.tar.gz";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static void Write(Stream output, TauConfigManifest manifest, byte[] payload)
    {
        manifest.PayloadSha256 = ComputeSha256(payload);

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
        using (var entryStream = manifestEntry.Open())
        {
            JsonSerializer.Serialize(entryStream, manifest, JsonOptions);
        }

        var payloadEntry = archive.CreateEntry(PayloadEntryName, CompressionLevel.NoCompression);
        using (var entryStream = payloadEntry.Open())
        {
            entryStream.Write(payload);
        }
    }

    public static TauConfigPackageData Read(Stream input)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("配置包缺少 manifest.json。");
        var payloadEntry = archive.GetEntry(PayloadEntryName)
            ?? throw new InvalidDataException("配置包缺少 payload.tar.gz。");

        TauConfigManifest manifest;
        using (var manifestStream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<TauConfigManifest>(manifestStream)
                ?? throw new InvalidDataException("manifest.json 解析失败。");
        }

        byte[] payload;
        using (var payloadStream = payloadEntry.Open())
        using (var memory = new MemoryStream())
        {
            payloadStream.CopyTo(memory);
            payload = memory.ToArray();
        }

        var actualHash = ComputeSha256(payload);
        if (!string.Equals(actualHash, manifest.PayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"payload 校验失败。manifest={manifest.PayloadSha256}, actual={actualHash}");
        }

        return new TauConfigPackageData(manifest, payload);
    }

    public static string ComputeSha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static PackagePrecheckResult Precheck(TauConfigManifest manifest, TauDeviceInfo target)
    {
        var result = new PackagePrecheckResult();
        if (!string.Equals(manifest.PackageFormat, "tau-config-v1", StringComparison.Ordinal))
        {
            result.Errors.Add("配置包格式不支持。");
        }

        if (!string.Equals(manifest.SoftwareVersion, target.SoftwareVersion, StringComparison.Ordinal))
        {
            result.Errors.Add($"软件版本不一致：配置包 {manifest.SoftwareVersion}，目标设备 {target.SoftwareVersion}。");
        }

        if (!string.IsNullOrWhiteSpace(manifest.HardwareProfile) &&
            !string.IsNullOrWhiteSpace(target.HardwareProfile) &&
            !string.Equals(manifest.HardwareProfile, target.HardwareProfile, StringComparison.Ordinal))
        {
            result.Errors.Add($"硬件识别不一致：配置包 {manifest.HardwareProfile}，目标设备 {target.HardwareProfile}。");
        }

        if (!string.IsNullOrWhiteSpace(manifest.FeatureHash) &&
            !string.IsNullOrWhiteSpace(target.FeatureHash) &&
            !string.Equals(manifest.FeatureHash, target.FeatureHash, StringComparison.OrdinalIgnoreCase))
        {
            result.Warnings.Add("feature 集合 hash 不一致，建议人工确认功能模块是否完全一致。");
        }

        if (manifest.IncludesDeviceIdentity)
        {
            result.Warnings.Add("配置包包含设备唯一标识，导入前必须确认老设备已下线。");
        }

        if (manifest.IncludesManagementMac)
        {
            result.Warnings.Add("配置包包含管理口 MAC，导入后可能造成二层地址冲突。");
        }

        return result;
    }
}

