using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;

internal sealed class OfpMtkCryptoProvider(IOppLogger? logger = null) : IOppFormatCryptoProvider, IDisposable
{
    private readonly IOppLogger _logger = logger ?? NullOppLogger.Instance;
    private readonly KeyDerivationCache _keyCache = new();
    private readonly List<MtkKeyEntry> _customMtkKeys = [];
    private bool _disposed;

    public OppFormat SupportedFormat => OppFormat.OfpMtk;

    /// <summary>Decrypts data using the specified key.<para>使用指定密钥解密数据。</para></summary>
    /// <param name="data">Ciphertext of the OFP MTK container; it is read only and never modified in place.
    /// <para>OFP MTK 容器的密文；仅读取，绝不就地修改。</para></param>
    /// <param name="keyId">MTK key identifier, matched case-insensitively against the built-in database and the custom keys.
    /// <para>MTK 密钥标识符，与内置密钥库和自定义密钥按忽略大小写匹配。</para></param>
    /// <returns>The AES-128-CFB plaintext as a newly allocated array.
    /// <para>以新分配数组返回的 AES-128-CFB 明文。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException"><paramref name="keyId"/> resolves to no MTK key.
    /// <para><paramref name="keyId"/> 无法解析为任何 MTK 密钥。</para></exception>
    public byte[] Decrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"MTK key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveMtkKey(entry);
        return AesCfb128.Decrypt(data, aesKey, aesIv);
    }

    /// <summary>Encrypts data using the specified key.<para>使用指定密钥加密数据。</para></summary>
    /// <param name="data">Plaintext of the OFP MTK container; it is read only and never modified in place.
    /// <para>OFP MTK 容器的明文；仅读取，绝不就地修改。</para></param>
    /// <param name="keyId">MTK key identifier, matched case-insensitively against the built-in database and the custom keys.
    /// <para>MTK 密钥标识符，与内置密钥库和自定义密钥按忽略大小写匹配。</para></param>
    /// <returns>The AES-128-CFB ciphertext as a newly allocated array.
    /// <para>以新分配数组返回的 AES-128-CFB 密文。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException"><paramref name="keyId"/> resolves to no MTK key.
    /// <para><paramref name="keyId"/> 无法解析为任何 MTK 密钥。</para></exception>
    public byte[] Encrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"MTK key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveMtkKey(entry);
        return AesCfb128.Encrypt(data, aesKey, aesIv);
    }

    /// <summary>Decrypts stream data using the specified key.<para>使用指定密钥解密流数据。</para></summary>
    /// <param name="input">Readable source stream positioned where the MTK payload starts; the provider reads it but never closes it.
    /// <para>定位到 MTK 负载起始处的可读取源流；提供者只读取且绝不关闭。</para></param>
    /// <param name="output">Stream receiving the plaintext; the provider writes to it, and neither flushes nor closes it.
    /// <para>接收明文的流；提供者向其写入，且既不调用刷新也不关闭。</para></param>
    /// <param name="keyId">MTK key identifier, matched case-insensitively against the built-in database and the custom keys.
    /// <para>MTK 密钥标识符，与内置密钥库和自定义密钥按忽略大小写匹配。</para></param>
    /// <param name="cancellationToken">Forwarded to the AES-128-CFB stream loop and checked between blocks.
    /// <para>贯穿传给 AES-128-CFB 的流式循环，并在各块之间检查。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException"><paramref name="keyId"/> resolves to no MTK key.
    /// <para><paramref name="keyId"/> 无法解析为任何 MTK 密钥。</para></exception>
    public void Decrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"MTK key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveMtkKey(entry);
        AesCfb128.Decrypt(input, output, aesKey, aesIv, cancellationToken);
    }

    /// <summary>Encrypts stream data using the specified key.<para>使用指定密钥加密流数据。</para></summary>
    /// <param name="input">Readable source stream positioned where the plaintext payload starts; the provider reads it but never closes it.
    /// <para>定位到明文负载起始处的可读取源流；提供者只读取且绝不关闭。</para></param>
    /// <param name="output">Stream receiving the ciphertext; the provider writes to it, and neither flushes nor closes it.
    /// <para>接收密文的流；提供者向其写入，且既不调用刷新也不关闭。</para></param>
    /// <param name="keyId">MTK key identifier, matched case-insensitively against the built-in database and the custom keys.
    /// <para>MTK 密钥标识符，与内置密钥库和自定义密钥按忽略大小写匹配。</para></param>
    /// <param name="cancellationToken">Forwarded to the AES-128-CFB stream loop and checked between blocks.
    /// <para>贯穿传给 AES-128-CFB 的流式循环，并在各块之间检查。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException"><paramref name="keyId"/> resolves to no MTK key.
    /// <para><paramref name="keyId"/> 无法解析为任何 MTK 密钥。</para></exception>
    public void Encrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"MTK key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveMtkKey(entry);
        AesCfb128.Encrypt(input, output, aesKey, aesIv, cancellationToken);
    }

    /// <summary>
    /// Automatically infers the MTK key and decrypts data.
    /// <para>自动推断 MTK 密钥并解密数据。</para>
    /// </summary>
    /// <param name="data">Ciphertext to decrypt; its first 16 bytes are trial-decrypted against each built-in and then
    /// each custom key until one yields the <c>MMM</c> marker. Must not be <see langword="null"/>.
    /// <para>待解密的密文；其前 16 字节会先用内置密钥、再用自定义密钥逐一试解，直到某个密钥解出 <c>MMM</c> 标记。不得为 <see langword="null"/>。</para></param>
    /// <returns>The whole decrypted payload when a key matched, or <see langword="null"/> when <paramref name="data"/> is
    /// shorter than 16 bytes or no key produced the marker.
    /// <para>匹配到密钥时返回完整解密载荷；<paramref name="data"/> 不足 16 字节，或没有任何密钥解出该标记时返回 <see langword="null"/>。</para></returns>
    public byte[]? AutoDecrypt(byte[] data)
    {
        Guard.ThrowIfNull(data);

        if (data.Length < 16)
        {
            return null;
        }

        var mtkKey = AutoDetectMtkKey(data);
        if (mtkKey != null)
        {
            _logger.Debug($"AutoDecrypt: matched MTK key '{mtkKey.KeyId}'.");
            var (aesKey, aesIv) = _keyCache.GetOrDeriveMtkKey(mtkKey);
            return AesCfb128.Decrypt(data, aesKey, aesIv);
        }

        return null;
    }

    /// <summary>
    /// Automatically detects the MTK key.
    /// <para>自动探测 MTK 密钥。</para>
    /// </summary>
    internal MtkKeyEntry? AutoDetectMtkKey(byte[] first16Bytes)
    {
        Guard.ThrowIfNull(first16Bytes);

        if (first16Bytes.Length < 16)
        {
            throw new ArgumentException("The input must be at least 16 bytes long.", nameof(first16Bytes));
        }

        foreach (var entry in MtkKeyDatabase.Entries)
        {
            try
            {
                var (aesKey, aesIv) = _keyCache.GetOrDeriveMtkKey(entry);
                var decrypted = AesCfb128.Decrypt(new ReadOnlySpan<byte>(first16Bytes, 0, 16), aesKey, aesIv);


                if (decrypted.Length >= 3 && decrypted[0] == 0x4D && decrypted[1] == 0x4D && decrypted[2] == 0x4D)
                {
                    _logger.Debug($"AutoDetectMtkKey: matched key '{entry.KeyId}'.");
                    return entry;
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                _logger.Warning($"AutoDetectMtkKey: key '{entry.KeyId}' failed.", ex);
            }
        }


        foreach (var entry in _customMtkKeys)
        {
            try
            {
                var (aesKey, aesIv) = _keyCache.GetOrDeriveMtkKey(entry);
                var decrypted = AesCfb128.Decrypt(new ReadOnlySpan<byte>(first16Bytes, 0, 16), aesKey, aesIv);

                if (decrypted.Length >= 3 && decrypted[0] == 0x4D && decrypted[1] == 0x4D && decrypted[2] == 0x4D)
                {
                    _logger.Debug($"AutoDetectMtkKey: matched user key '{entry.KeyId}'.");
                    return entry;
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                _logger.Warning($"AutoDetectMtkKey: user key '{entry.KeyId}' failed.", ex);
            }
        }

        return null;
    }

    /// <summary>
    /// Queries whether the specified key identifier is supported.
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// </summary>
    /// <param name="keyId">Key identifier matched case-insensitively against the built-in database and then the custom
    /// entries; <see langword="null"/> or an unknown value simply matches nothing.
    /// <para>先与内置密钥库、再与自定义条目做不区分大小写匹配的密钥标识符；传入 <see langword="null"/> 或未知值时只是无匹配。</para></param>
    /// <returns><see langword="true"/> when a built-in or custom entry carries that identifier; otherwise
    /// <see langword="false"/>. <para>内置或自定义条目中存在该标识符时为 <see langword="true"/>，否则为 <see langword="false"/>。</para></returns>
    public bool IsSupported(string keyId)
    {
        if (MtkKeyDatabase.FindByKeyId(keyId) != null)
        {
            return true;
        }

        foreach (var entry in _customMtkKeys)
        {
            if (string.Equals(entry.KeyId, keyId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the list of all known key identifiers for this format.
    /// <para>获取此格式的所有已知密钥标识符列表。</para>
    /// </summary>
    public IReadOnlyList<string> GetKnownKeys()
    {
        var result = new List<string>();

        foreach (var entry in MtkKeyDatabase.Entries)
        {
            result.Add(entry.KeyId);
        }

        foreach (var entry in _customMtkKeys)
        {
            result.Add(entry.KeyId);
        }

        return result;
    }

    /// <summary>
    /// Adds a custom key.
    /// <para>添加自定义密钥。</para>
    /// </summary>
    /// <param name="keyEntry">An <see cref="MtkKeyEntry"/> appended to the custom list, where it is consulted after the
    /// built-in database and by auto-detection; the entry is stored by reference and duplicate identifiers are kept.
    /// <para>要追加到自定义列表的 <see cref="MtkKeyEntry"/>，其检索顺序排在内置密钥库之后，自动探测同样会用到；条目按引用存储，标识符重复时一并保留。</para></param>
    public void AddCustomKey(object keyEntry)
    {
        Guard.ThrowIfNull(keyEntry);

        if (keyEntry is MtkKeyEntry mtk)
        {
            _customMtkKeys.Add(mtk);
            return;
        }

        throw new ArgumentException(
            $"Unsupported key entry type for MTK format: {keyEntry.GetType().Name}. Expected MtkKeyEntry.",
            nameof(keyEntry));
    }

    /// <summary>
    /// Exports discovered keys.
    /// <para>导出发现的密钥。</para>
    /// </summary>
    /// <param name="filePath">Destination path accepted by the interface; this MTK provider publishes no discovered-key
    /// file, so the value is neither validated nor used and nothing is written or deleted.
    /// <para>接口要求的目标路径；该 MTK 提供者不发布已发现密钥文件，因此该值既不校验也不使用，不会写入或删除任何内容。</para></param>
    public void ExportDiscoveredKeys(string filePath)
    {

    }

    /// <summary>
    /// Clears all key data.
    /// <para>清除所有密钥数据。</para>
    /// </summary>
    public void Clear()
    {
        _keyCache.Clear();
        _customMtkKeys.Clear();
    }

    /// <summary>
    /// Releases all resources used by this instance.
    /// <para>释放此实例使用的所有资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Clear();
        _disposed = true;
    }

    private MtkKeyEntry? FindKey(string keyId)
    {
        return MtkKeyDatabase.FindByKeyId(keyId)
            ?? _customMtkKeys.FirstOrDefault(k => string.Equals(k.KeyId, keyId, StringComparison.OrdinalIgnoreCase));
    }
}