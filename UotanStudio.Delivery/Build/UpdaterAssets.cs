using UotanStudio.Compress;
using UotanStudio.Compress.Codecs.Zstd;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Delivery.Build;

/// <summary>
/// Materializes the embedded arm64 Edify updater: one zstd frame decoded in memory, checked against the pinned length
/// and SHA-256, then published atomically beside the destination.
/// <para>把内嵌的 arm64 Edify 更新器落盘：在内存中解码一个 zstd 帧，按固定的长度与 SHA-256 校验，再原子替换目标文件。</para>
/// </summary>
internal static class UpdaterAssets
{
    private const string ResourceName = "UotanStudio.Delivery.Assets.update-binary-arm64";
    private const int UpdaterByteLength = 2_002_464;
    private const string UpdaterSha256 = "32cbc832c636b660b331844fdd75c5df3fd606bd10f5c9f02d9b8bc25eba9567";

    /// <summary>
    /// Returns the path of the verified native updater, decoding the embedded frame on first use.
    /// <para>返回已校验的原生更新器路径，首次使用时解码内嵌帧。</para>
    /// </summary>
    /// <returns>Absolute path of the executable updater in the tool cache.
    /// <para>工具缓存中可执行更新器的绝对路径。</para></returns>
    /// <exception cref="FileNotFoundException">The embedded updater resource is missing from the assembly.
    /// <para>程序集中缺少内嵌的更新器资源。</para></exception>
    /// <exception cref="InvalidDataException">The embedded frame does not decode to the pinned length and digest.
    /// <para>内嵌帧解码后与固定的长度、摘要不符。</para></exception>
    internal static string Extract()
    {
        var destination = Path.Combine(Path.GetTempPath(), "UotanStudio", "delivery-updater", "update-binary-arm64");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination) || !Matches(destination))
        {
            Publish(LoadVerified(), destination);
        }

        DeliveryUnixPermissions.SetExecutable(destination);
        return destination;
    }

    private static bool Matches(string path)
    {
        using var source = File.OpenRead(path);
        return source.Length == UpdaterByteLength
            && string.Equals(Convert.ToHexStringLower(Sha256.ComputeHash(source)), UpdaterSha256, StringComparison.Ordinal);
    }

    private static void Publish(byte[] bytes, string destination)
    {
        var temporary = destination + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static byte[] LoadVerified()
    {
        var assembly = typeof(UpdaterAssets).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException(
                $"The embedded Recovery updater resource '{ResourceName}' is missing from {assembly.GetName().Name}. "
                + $"程序集 {assembly.GetName().Name} 中缺少内嵌的 Recovery 更新器资源 '{ResourceName}'。",
                ResourceName);

        var compressed = new byte[checked((int)stream.Length)];
        stream.ReadExactly(compressed);

        byte[] bytes;
        try
        {
            bytes = ZstdBlockCodec.Decompress(compressed, UpdaterByteLength);
        }
        catch (CompressionException error)
        {
            throw new InvalidDataException(
                $"The embedded Recovery updater frame is not a valid zstd frame of {UpdaterByteLength} bytes. "
                + $"内嵌的 Recovery 更新器帧不是 {UpdaterByteLength} 字节的有效 zstd 帧。",
                error);
        }

        var digest = Sha256.ComputeHexLower(bytes);
        if (!string.Equals(digest, UpdaterSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The embedded Recovery updater hashes to {digest} instead of {UpdaterSha256}. "
                + $"内嵌的 Recovery 更新器摘要为 {digest}，与固定的 {UpdaterSha256} 不符。");
        }

        return bytes;
    }
}