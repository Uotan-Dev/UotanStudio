using System.Globalization;
using System.Reflection;
using System.Text;
using UotanStudio.Compress;
using UotanStudio.Compress.Codecs.Zstd;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Mirroring.Ohscrcpy;

/// <summary>
/// Materializes the embedded zstd device runtime into one cache directory keyed by the manifest digest.
/// <para>把内嵌的 zstd 设备运行时物化到按清单摘要命名的缓存目录。</para>
/// </summary>
internal static class OhscrcpyDeviceAssets
{
    private const string ResourcePrefix = "ohos/device/";
    private const string ManifestResourceName = ResourcePrefix + "manifest.txt";
    private const int DirectoryHashLength = 16;
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumUncompressedBytes = 64 * 1024 * 1024;

    private static readonly Lazy<string> Materialized =
        new(Materialize, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Cache directory holding the device runtime materialized from the embedded manifest.
    /// <para>保存由内嵌清单物化的设备运行时的缓存目录。</para>
    /// </summary>
    /// <exception cref="InvalidDataException">The manifest resource, a payload resource, a decoded length, or a
    /// digest is missing or does not match. <para>清单资源、负载资源、解码长度或摘要缺失或不符。</para></exception>
    internal static string RuntimeDirectory => Materialized.Value;

    private static string Materialize()
    {
        var assembly = typeof(OhscrcpyDeviceAssets).Assembly;
        var manifest = ReadManifest(assembly);
        var entries = ParseManifest(manifest);
        var directory = Path.Combine(
            Path.GetTempPath(),
            "UotanStudio",
            "ohos-device",
            Sha256.ComputeHexLower(manifest)[..DirectoryHashLength]);
        Directory.CreateDirectory(directory);
        foreach (var entry in entries)
        {
            MaterializeEntry(assembly, directory, entry);
        }

        return directory;
    }

    private static byte[] ReadManifest(Assembly assembly)
    {
        var manifest = TryReadResource(assembly, ManifestResourceName)
            ?? throw new InvalidDataException(
                $"The embedded device asset manifest '{ManifestResourceName}' is missing from "
                + $"{assembly.GetName().Name}. "
                + $"程序集 {assembly.GetName().Name} 中缺少内嵌的设备资源清单 '{ManifestResourceName}'。");
        if (manifest.Length is 0 or > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                $"The embedded device asset manifest is {manifest.Length} bytes, which is outside the accepted range. "
                + $"内嵌的设备资源清单为 {manifest.Length} 字节，超出可接受范围。");
        }

        return manifest;
    }

    private static List<ManifestEntry> ParseManifest(byte[] manifest)
    {
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(manifest);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException(
                "The embedded device asset manifest is not valid UTF-8. "
                + "内嵌的设备资源清单不是有效的 UTF-8。", error);
        }

        var entries = new List<ManifestEntry>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            entries.Add(ParseManifestEntry(trimmed));
        }

        return entries;
    }

    private static ManifestEntry ParseManifestEntry(string line)
    {
        var fields = line.Split('\t');
        if (fields.Length != 3)
        {
            throw new InvalidDataException(
                $"The embedded device asset manifest line '{line}' does not have three tab-separated fields. "
                + $"内嵌的设备资源清单行 '{line}' 不是三个制表符分隔的字段。");
        }

        var fileName = fields[0].Trim();
        if (fileName.Length == 0
            || fileName is "." or ".."
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName.Contains(Path.DirectorySeparatorChar)
            || fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException(
                $"The embedded device asset manifest names an unusable file '{fileName}'. "
                + $"内嵌的设备资源清单给出了不可用的文件名 '{fileName}'。");
        }

        if (!int.TryParse(fields[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            || length is < 0 or > MaximumUncompressedBytes)
        {
            throw new InvalidDataException(
                $"The embedded device asset manifest records an unusable length '{fields[1]}' for '{fileName}'. "
                + $"内嵌的设备资源清单为 '{fileName}' 记录了不可用的长度 '{fields[1]}'。");
        }

        var digest = fields[2].Trim();
        if (digest.Length != 2 * Sha256.HashSizeInBytes
            || !digest.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new InvalidDataException(
                $"The embedded device asset manifest records an unusable SHA-256 '{digest}' for '{fileName}'. "
                + $"内嵌的设备资源清单为 '{fileName}' 记录了不可用的 SHA-256 '{digest}'。");
        }

        return new ManifestEntry(fileName, length, digest);
    }

    private static void MaterializeEntry(Assembly assembly, string directory, ManifestEntry entry)
    {
        var targetPath = Path.Combine(directory, entry.FileName);
        var target = new FileInfo(targetPath);
        if (target.Exists && target.Length == entry.Length)
        {
            return;
        }

        var compressed = ReadPayload(assembly, entry.FileName);
        byte[] bytes;
        try
        {
            bytes = ZstdBlockCodec.Decompress(compressed, entry.Length);
        }
        catch (CompressionException error)
        {
            throw new InvalidDataException(
                $"The embedded device payload for '{entry.FileName}' is not a zstd frame that decodes to "
                + $"{entry.Length} bytes. "
                + $"内嵌的设备负载 '{entry.FileName}' 不是可解出 {entry.Length} 字节的 zstd 帧。", error);
        }

        var temporaryPath = targetPath + ".tmp";
        try
        {
            var digest = Sha256.ComputeHexLower(bytes);
            if (!string.Equals(digest, entry.Digest, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The embedded device asset '{entry.FileName}' hashes to {digest} instead of {entry.Digest}. "
                    + $"内嵌的设备资源 '{entry.FileName}' 摘要为 {digest}，与记录的 {entry.Digest} 不符。");
            }

            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            // The decoded bytes are on disk, so the buffer does not stay resident.
            // 解码字节已写入磁盘，缓冲区不再驻留。
            Array.Clear(bytes);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static byte[] ReadPayload(Assembly assembly, string fileName)
    {
        // The logical name contract is ohos/device/<fileName>; the build item is globbed as *.so.zst, so the
        // compressed suffix is accepted as well.
        return TryReadResource(assembly, ResourcePrefix + fileName)
            ?? TryReadResource(assembly, ResourcePrefix + fileName + ".zst")
            ?? throw new InvalidDataException(
                $"The embedded device payload for '{fileName}' is missing from {assembly.GetName().Name}; "
                + $"expected '{ResourcePrefix}{fileName}' or '{ResourcePrefix}{fileName}.zst'. "
                + $"程序集 {assembly.GetName().Name} 中缺少 '{fileName}' 的内嵌设备负载；需要 "
                + $"'{ResourcePrefix}{fileName}' 或 '{ResourcePrefix}{fileName}.zst'。");
    }

    private static byte[]? TryReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private readonly record struct ManifestEntry(string FileName, int Length, string Digest);
}