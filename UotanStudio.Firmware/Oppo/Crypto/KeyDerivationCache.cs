using System.Collections.Concurrent;
using UotanStudio.Firmware.Oppo.Crypto.Algorithms;
using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;

namespace UotanStudio.Firmware.Oppo.Crypto;

/// <summary>
/// <para>线程安全的密钥派生缓存，避免对相同密钥条目重复执行 MD5 计算和密钥派生操作。</para>
/// Thread-safe key derivation cache that avoids repeated MD5 computation and key derivation for the same key entries.
/// </summary>
internal sealed class KeyDerivationCache : IDisposable
{
    private readonly ConcurrentDictionary<string, (byte[] AesKey, byte[] AesIv)> _qcKeyCache = new();
    private readonly ConcurrentDictionary<string, (byte[] AesKey, byte[] AesIv)> _mtkKeyCache = new();

    /// <summary>
    /// <para>获取或派生 QC 密钥。</para>
    /// Gets or derives a QC key.
    /// </summary>
    /// <param name="entry"><para>QC 密钥条目。</para>The QC key entry.</param>
    /// <returns><para>包含 AES 密钥和 IV 的元组。</para>A tuple containing the AES key and IV.</returns>
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
    /// <para>获取或派生 MTK 密钥。</para>
    /// Gets or derives an MTK key.
    /// </summary>
    /// <param name="entry"><para>MTK 密钥条目。</para>The MTK key entry.</param>
    /// <returns><para>包含 AES 密钥和 IV 的元组。</para>A tuple containing the AES key and IV.</returns>
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
    /// <para>清除所有缓存条目，释放已缓存的派生结果。</para>
    /// Clears all cache entries, releasing the cached derived results.
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
    /// <para>释放此实例使用的所有资源，清除缓存的密钥材料。</para>
    /// Releases all resources used by this instance, clearing cached key material.
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