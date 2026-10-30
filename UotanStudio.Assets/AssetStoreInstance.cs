using System.Collections.Concurrent;
using UotanStudio.Compress;
using UotanStudio.Compress.Codecs.Zstd;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Assets;

internal sealed class AssetStoreInstance
{
    private const int DirectoryHashLength = 16;

    private readonly IReadOnlyDictionary<string, AssetBundle> _bundles;
    private readonly IAssetPayloadSource _source;
    private readonly string _cacheRoot;
    private readonly ConcurrentDictionary<string, Lock> _gates = new(StringComparer.Ordinal);

    internal AssetStoreInstance(
        IReadOnlyDictionary<string, AssetBundle> bundles,
        IAssetPayloadSource source,
        string cacheRoot)
    {
        ArgumentNullException.ThrowIfNull(bundles);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _bundles = bundles;
        _source = source;
        _cacheRoot = cacheRoot;
    }

    internal AssetBundle Resolve(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _bundles.TryGetValue(key, out var bundle) ? bundle : AssetBundle.Empty(key);
    }

    internal byte[] Read(AssetEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var resourceName = ResourceName(entry);
        var frame = _source.Read(resourceName);
        byte[] payload;
        try
        {
            payload = ZstdBlockCodec.Decompress(frame, checked((int)entry.Length));
        }
        catch (CompressionException error)
        {
            throw new InvalidDataException(
                $"The asset frame '{resourceName}' is not a zstd frame that decodes to {entry.Length} bytes. "
                + $"资源帧 '{resourceName}' 不是可解出 {entry.Length} 字节的 zstd 帧。", error);
        }

        if (payload.Length != entry.Length)
        {
            throw new InvalidDataException(
                $"The asset frame '{resourceName}' decoded to {payload.Length} bytes while the manifest declares "
                + $"{entry.Length}. 资源帧 '{resourceName}' 解出 {payload.Length} 字节，与清单声明的 {entry.Length} 不符。");
        }

        var digest = Sha256.ComputeHexLower(payload);
        if (!string.Equals(digest, entry.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The asset '{resourceName}' hashes to {digest} instead of {entry.Sha256}. "
                + $"资源 '{resourceName}' 摘要为 {digest}，与清单记录的 {entry.Sha256} 不符。");
        }

        return payload;
    }

    internal string Materialize(AssetBundle bundle, CancellationToken cancellationToken)
    {
        EnsurePresent(bundle);
        var gate = _gates.GetOrAdd(bundle.Key, static _ => new Lock());
        lock (gate)
        {
            var directory = CacheDirectory(bundle);
            Directory.CreateDirectory(directory);
            foreach (var entry in bundle.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetPath = Path.Combine(directory, entry.Path);
                var target = new FileInfo(targetPath);
                if (target.Exists && target.Length == entry.Length)
                {
                    continue;
                }

                WriteEntry(entry, targetPath);
            }

            return directory;
        }
    }

    internal IReadOnlyList<string> Extract(AssetBundle bundle, string destinationDirectory,
        CancellationToken cancellationToken)
    {
        EnsurePresent(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!Directory.Exists(destinationDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The asset destination '{destinationDirectory}' does not exist. 资源目标目录 '{destinationDirectory}' 不存在。");
        }

        var names = new List<string>(bundle.Entries.Count);
        foreach (var entry in bundle.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteEntry(entry, Path.Combine(destinationDirectory, entry.Path));
            names.Add(entry.Path);
        }

        return names;
    }

    private static string ResourceName(AssetEntry entry) => $"assets/{entry.Bundle}/{entry.Path}.zst";

    private static void EnsurePresent(AssetBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (!bundle.IsPresent)
        {
            throw new InvalidOperationException(
                $"The asset bundle '{bundle.Key}' is not shipped in this build. 本次构建未随包资源 '{bundle.Key}'。");
        }
    }

    private string CacheDirectory(AssetBundle bundle)
    {
        var directory = _cacheRoot;
        foreach (var segment in bundle.Key.Split('/'))
        {
            directory = Path.Combine(directory, segment);
        }

        return Path.Combine(directory, bundle.Digest[..DirectoryHashLength]);
    }

    private void WriteEntry(AssetEntry entry, string targetPath)
    {
        var payload = Read(entry);
        var temporaryPath = targetPath + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, payload);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            Array.Clear(payload);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}