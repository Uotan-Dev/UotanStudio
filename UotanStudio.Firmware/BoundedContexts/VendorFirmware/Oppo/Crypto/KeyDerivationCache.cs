using System.Collections.Concurrent;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto.Algorithms;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto.KeyDatabase;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto;

internal sealed class KeyDerivationCache : IDisposable
{
    private readonly ConcurrentDictionary<string, (byte[] AesKey, byte[] AesIv)> _qcKeyCache = new();
    private readonly ConcurrentDictionary<string, (byte[] AesKey, byte[] AesIv)> _mtkKeyCache = new();

    /// <summary>
    /// Gets or derives a QC key.
    /// <para>获取或派生 QC 密钥。</para>
    /// </summary>
    public (byte[] AesKey, byte[] AesIv) GetOrDeriveQcKey(QcKeyEntry entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        var (aesKey, aesIv) = _qcKeyCache.GetOrAdd(entry.KeyId, _ => OppQcKeyDerivation.DeriveKey(entry));
        return ((byte[])aesKey.Clone(), (byte[])aesIv.Clone());
    }

    /// <summary>
    /// Gets or derives an MTK key.
    /// <para>获取或派生 MTK 密钥。</para>
    /// </summary>
    public (byte[] AesKey, byte[] AesIv) GetOrDeriveMtkKey(MtkKeyEntry entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        var (aesKey, aesIv) = _mtkKeyCache.GetOrAdd(entry.KeyId, _ => DeriveMtkKey(entry));
        return ((byte[])aesKey.Clone(), (byte[])aesIv.Clone());
    }

    /// <summary>
    /// Clears all cache entries, releasing the cached derived results.
    /// <para>清除所有缓存条目，释放已缓存的派生结果。</para>
    /// </summary>
    public void Clear()
    {
        foreach (var (aesKey, aesIv) in _qcKeyCache.Values)
        {
            Array.Clear(aesKey, 0, aesKey.Length);
            Array.Clear(aesIv, 0, aesIv.Length);
        }

        foreach (var (aesKey, aesIv) in _mtkKeyCache.Values)
        {
            Array.Clear(aesKey, 0, aesKey.Length);
            Array.Clear(aesIv, 0, aesIv.Length);
        }

        _qcKeyCache.Clear();
        _mtkKeyCache.Clear();
    }

    /// <summary>
    /// Releases all resources used by this instance, clearing cached key material.
    /// <para>释放此实例使用的所有资源，清除缓存的密钥材料。</para>
    /// </summary>
    public void Dispose() => Clear();

    private static (byte[] AesKey, byte[] AesIv) DeriveMtkKey(MtkKeyEntry entry)
    {
        return entry.Is3Element
            ? OppMtkKeyDerivation.DeriveKey3(entry)
            : entry.Is2Element
            ? OppMtkKeyDerivation.DeriveKey2(entry)
            : throw new InvalidOperationException(
            $"MTK key entry '{entry.KeyId}' is neither a 3-element nor a 2-element configuration.");
    }
}
