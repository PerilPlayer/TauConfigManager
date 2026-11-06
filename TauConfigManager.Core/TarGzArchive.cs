using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace TauConfigManager.Core;

public sealed class TarGzEntry
{
    public string Path { get; set; } = "";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public UnixFileMode Mode { get; set; } =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
}

public static class TarGzArchive
{
    public static IReadOnlyList<TarGzEntry> Read(byte[] tarGz)
    {
        using var source = new MemoryStream(tarGz);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var entries = new Dictionary<string, TarGzEntry>(StringComparer.Ordinal);
        var order = new List<string>();

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            if (entry.EntryType != TarEntryType.RegularFile)
            {
                continue;
            }

            var normalizedPath = NormalizePath(entry.Name);
            using var data = new MemoryStream();
            entry.DataStream?.CopyTo(data);
            if (!entries.ContainsKey(normalizedPath))
            {
                order.Add(normalizedPath);
            }

            entries[normalizedPath] = new TarGzEntry
            {
                Path = normalizedPath,
                Data = data.ToArray(),
                Mode = entry.Mode,
            };
        }

        return order.Select(path => entries[path]).ToList();
    }

    public static byte[] Write(IEnumerable<TarGzEntry> entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            var uniqueEntries = new Dictionary<string, TarGzEntry>(StringComparer.Ordinal);
            foreach (var sourceEntry in entries)
            {
                uniqueEntries[NormalizePath(sourceEntry.Path)] = sourceEntry;
            }

            foreach (var pair in uniqueEntries.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                var sourceEntry = pair.Value;
                var entry = new UstarTarEntry(TarEntryType.RegularFile, pair.Key)
                {
                    DataStream = new MemoryStream(sourceEntry.Data, writable: false),
                    Mode = sourceEntry.Mode,
                    ModificationTime = DateTimeOffset.UnixEpoch,
                };
                writer.WriteEntry(entry);
            }
        }

        return output.ToArray();
    }

    public static byte[] Sanitize(byte[] tarGz, ExportOptions options)
    {
        var entries = new List<TarGzEntry>();
        foreach (var entry in Read(tarGz))
        {
            if (!ConfigSanitizer.ShouldIncludePath(entry.Path, options))
            {
                continue;
            }

            if (LooksLikeText(entry.Data))
            {
                var text = Encoding.UTF8.GetString(entry.Data);
                var sanitized = ConfigSanitizer.SanitizeText(entry.Path, text, options);
                entries.Add(new TarGzEntry
                {
                    Path = entry.Path,
                    Data = Encoding.UTF8.GetBytes(sanitized),
                    Mode = entry.Mode,
                });
            }
            else
            {
                entries.Add(entry);
            }
        }

        return Write(entries);
    }

    public static IReadOnlyList<ConfigDiffEntry> Compare(byte[] packagePayload, byte[] devicePayload)
    {
        var packageEntries = Read(packagePayload).ToDictionary(e => e.Path, e => TauConfigPackage.ComputeSha256(e.Data), StringComparer.Ordinal);
        var deviceEntries = Read(devicePayload).ToDictionary(e => e.Path, e => TauConfigPackage.ComputeSha256(e.Data), StringComparer.Ordinal);
        var allPaths = packageEntries.Keys.Concat(deviceEntries.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var diffs = new List<ConfigDiffEntry>();

        foreach (var path in allPaths)
        {
            packageEntries.TryGetValue(path, out var packageHash);
            deviceEntries.TryGetValue(path, out var deviceHash);
            var status = packageHash == null
                ? "仅目标设备存在"
                : deviceHash == null
                    ? "仅配置包存在"
                    : string.Equals(packageHash, deviceHash, StringComparison.OrdinalIgnoreCase)
                        ? "一致"
                        : "不同";

            diffs.Add(new ConfigDiffEntry
            {
                Path = path,
                Status = status,
                PackageHash = packageHash ?? "",
                DeviceHash = deviceHash ?? "",
            });
        }

        return diffs;
    }

    private static bool LooksLikeText(byte[] data)
    {
        if (data.Length == 0)
        {
            return true;
        }

        var scanLength = Math.Min(data.Length, 4096);
        for (var i = 0; i < scanLength; i++)
        {
            if (data[i] == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }
}
