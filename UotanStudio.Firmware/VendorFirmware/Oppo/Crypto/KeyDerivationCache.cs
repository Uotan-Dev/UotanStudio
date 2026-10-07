using System.Collections.Concurrent;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;

internal sealed class KeyDerivationCache : IDisposable
{
    private readonly ConcurrentDictionary<string, (byte[] AesKey, byte[] AesIv)> _qcKeyCache = new();
    private readonly ConcurrentDictionary<string, (byte[] AesKey, byte[] AesIv)> _mtkKeyCache = new();

    /// <summary>
    /// Gets or derives a QC key.
    /// <para>获取或派生 QC 密钥。</para>
    /// </summary>
    /// <param name="entry">QC key material whose <c>KeyId</c> is the cache slot; a new entry is derived on the first
    /// lookup for that identifier and reused afterwards. Must not be <see langword="null"/>.
    /// <para>QC 密钥材料，其 <c>KeyId</c> 即缓存槽位；某个标识符首次查询时派生新结果，之后重复使用。不得为 <see langword="null"/>。</para></param>
    /// <returns>Fresh clones of the derived 16-byte AES key and IV, so mutating them does not affect the cached
    /// material. <para>派生的 AES 密钥与 IV 的新克隆，修改返回值不会影响缓存中的材料。</para></returns>
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
    /// <param name="entry">MTK key material whose <c>KeyId</c> is the cache slot; <c>Is3Element</c> selects the wrapped
    /// derivation and <c>Is2Element</c> the direct-text one. Must not be <see langword="null"/>.
    /// <para>MTK 密钥材料，其 <c>KeyId</c> 即缓存槽位；<c>Is3Element</c> 选择包装派生，<c>Is2Element</c> 选择直接文本派生。不得为 <see langword="null"/>。</para></param>
    /// <returns>Fresh clones of the derived AES key and IV, so mutating them does not affect the cached material. An
    /// entry in neither shape fails the derivation.
    /// <para>派生的 AES 密钥与 IV 的新克隆，修改返回值不会影响缓存中的材料。两种形态都不满足的条目会使派生失败。</para></returns>
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