using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;

internal sealed class OfpQcCryptoProvider(IOppLogger? logger = null) : IOppFormatCryptoProvider, IDisposable
{
    private const int ProbeWindowBytes = 64 * 1024;

    private static ReadOnlySpan<byte> XmlDeclaration => "<?xml"u8;

    private readonly IOppLogger _logger = logger ?? NullOppLogger.Instance;
    private readonly KeyDerivationCache _keyCache = new();
    private readonly List<QcKeyEntry> _customQcKeys = [];
    private bool _disposed;

    public OppFormat SupportedFormat => OppFormat.OfpQc;

    /// <summary>Decrypts data using the specified key.<para>使用指定密钥解密数据。</para></summary>
    /// <param name="data">Ciphertext of the OFP QC container; it is read only and never modified in place.
    /// <para>OFP QC 容器的密文；仅读取，绝不就地修改。</para></param>
    /// <param name="keyId">QC key identifier, matched case-insensitively against the built-in database and the custom keys.
    /// <para>QC 密钥标识符，与内置密钥库和自定义密钥按忽略大小写匹配。</para></param>
    /// <returns>The AES-128-CFB plaintext as a newly allocated array.
    /// <para>以新分配数组返回的 AES-128-CFB 明文。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException"><paramref name="keyId"/> resolves to no QC key.
    /// <para><paramref name="keyId"/> 无法解析为任何 QC 密钥。</para></exception>
    public byte[] Decrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"QC key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveQcKey(entry);
        return AesCfb128.Decrypt(data, aesKey, aesIv);
    }

    /// <summary>Encrypts data using the specified key.<para>使用指定密钥加密数据。</para></summary>
    /// <param name="data">Plaintext of the OFP QC container; it is read only and never modified in place.
    /// <para>OFP QC 容器的明文；仅读取，绝不就地修改。</para></param>
    /// <param name="keyId">QC key identifier, matched case-insensitively against the built-in database and the custom keys.
    /// <para>QC 密钥标识符，与内置密钥库和自定义密钥按忽略大小写匹配。</para></param>
    /// <returns>The AES-128-CFB ciphertext as a newly allocated array.
    /// <para>以新分配数组返回的 AES-128-CFB 密文。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException"><paramref name="keyId"/> resolves to no QC key.
    /// <para><paramref name="keyId"/> 无法解析为任何 QC 密钥。</para></exception>
    public byte[] Encrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"QC key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveQcKey(entry);
        return AesCfb128.Encrypt(data, aesKey, aesIv);
    }

    /// <summary>Decrypts stream data using the specified key.<para>使用指定密钥解密流数据。</para></summary>
    /// <param name="input">Readable source stream positioned where the QC payload starts; the provider reads it but never closes it.
    /// <para>定位到 QC 负载起始处的可读取源流；提供者只读取且绝不关闭。</para></param>
    /// <param name="output">Stream receiving the plaintext; the provider writes to it, and neither flushes nor closes it.
    /// <para>接收明文的流；提供者向其写入，且既不调用刷新也不关闭。</para></param>
    /// <param name="keyId">QC key identifier, matched case-insensitively against the built-in database and the custom keys.
    /// <para>QC 密钥标识符，与内置密钥库和自定义密钥按忽略大小写匹配。</para></param>
    /// <param name="cancellationToken">Forwarded to the AES-128-CFB stream loop and checked between blocks.
    /// <para>贯穿传给 AES-128-CFB 的流式循环，并在各块之间检查。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException"><paramref name="keyId"/> resolves to no QC key.
    /// <para><paramref name="keyId"/> 无法解析为任何 QC 密钥。</para></exception>
    public void Decrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"QC key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveQcKey(entry);
        AesCfb128.Decrypt(input, output, aesKey, aesIv, cancellationToken);
    }

    /// <summary>Encrypts stream data using the specified key.<para>使用指定密钥加密流数据。</para></summary>
    /// <param name="input">Readable source stream positioned where the plaintext payload starts; the provider reads it but never closes it.
    /// <para>定位到明文负载起始处的可读取源流；提供者只读取且绝不关闭。</para></param>
    /// <param name="output">Stream receiving the ciphertext; the provider writes to it, and neither flushes nor closes it.
    /// <para>接收密文的流；提供者向其写入，且既不调用刷新也不关闭。</para></param>
    /// <param name="keyId">QC key identifier, matched case-insensitively against the built-in database and the custom keys.
    /// <para>QC 密钥标识符，与内置密钥库和自定义密钥按忽略大小写匹配。</para></param>
    /// <param name="cancellationToken">Forwarded to the AES-128-CFB stream loop and checked between blocks.
    /// <para>贯穿传给 AES-128-CFB 的流式循环，并在各块之间检查。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException"><paramref name="keyId"/> resolves to no QC key.
    /// <para><paramref name="keyId"/> 无法解析为任何 QC 密钥。</para></exception>
    public void Encrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"QC key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveQcKey(entry);
        AesCfb128.Encrypt(input, output, aesKey, aesIv, cancellationToken);
    }

    /// <summary>
    /// Automatically infers the QC key and decrypts data.
    /// <para>自动推断 QC 密钥并解密数据。</para>
    /// </summary>
    /// <param name="data">Ciphertext tried against each built-in QC key and then each custom key; the key whose
    /// decryption decodes to UTF-8 text containing an <c>&lt;?xml</c> declaration is selected, and the whole array is
    /// then decrypted with it. Must not be <see langword="null"/>.
    /// <para>待试解的密文：先逐个用内置 QC 密钥、再用自定义密钥尝试；选中那个解密后可按 UTF-8 解码并含 <c>&lt;?xml</c> 声明的密钥，随后用其对整个数组解密。不得为 <see langword="null"/>。</para></param>
    /// <returns>The decrypted bytes when a key produced an XML declaration, otherwise <see langword="null"/>.
    /// <para>某个密钥解出 XML 声明时返回解密后的字节，否则返回 <see langword="null"/>。</para></returns>
    public byte[]? AutoDecrypt(byte[] data)
    {
        Guard.ThrowIfNull(data);

        var result = AutoDecryptQcXml(data);
        if (result != null)
        {
            _logger.Debug($"AutoDecrypt: matched QC key '{result.Value.KeyId}'.");
            return Decrypt(data, result.Value.KeyId);
        }

        return null;
    }

    /// <summary>
    /// Automatically detects QC key and decrypts XML data.
    /// <para>自动探测 QC 密钥并解密 XML 数据。</para>
    /// </summary>
    internal (string Xml, string KeyId)? AutoDecryptQcXml(byte[] encryptedXml)
    {
        Guard.ThrowIfNull(encryptedXml);

        var probeLength = Math.Min(encryptedXml.Length, ProbeWindowBytes);
        if (TryProbeKey(encryptedXml, probeLength, QcKeyDatabase.Entries, out var builtIn)
            || TryProbeKey(encryptedXml, probeLength, _customQcKeys, out builtIn))
        {
            return DecryptMatched(encryptedXml, builtIn!);
        }


        return FullScanForKey(encryptedXml);
    }

    private bool TryProbeKey(
        byte[] encryptedXml,
        int probeLength,
        IReadOnlyList<QcKeyEntry> entries,
        out QcKeyEntry? matched)
    {
        foreach (var entry in entries)
        {
            try
            {
                var (aesKey, aesIv) = _keyCache.GetOrDeriveQcKey(entry);
                if (AesCfb128.IndexOfDecrypted(encryptedXml, aesKey, aesIv, XmlDeclaration, probeLength) >= 0)
                {
                    matched = entry;
                    return true;
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                _logger.Warning($"AutoDecryptQcXml: key '{entry.KeyId}' failed.", ex);
            }
        }

        matched = null;
        return false;
    }

    private (string Xml, string KeyId) DecryptMatched(byte[] encryptedXml, QcKeyEntry entry)
    {
        var (aesKey, aesIv) = _keyCache.GetOrDeriveQcKey(entry);
        var decrypted = AesCfb128.Decrypt(encryptedXml, aesKey, aesIv);
        var text = Encoding.UTF8.GetString(decrypted);
        _logger.Debug($"AutoDecryptQcXml: matched key '{entry.KeyId}'.");
        return (TruncateXmlAtLastCloseTag(text), entry.KeyId);
    }

    private (string Xml, string KeyId)? FullScanForKey(byte[] encryptedXml)
    {
        var buffer = new byte[encryptedXml.Length];
        foreach (var entry in QcKeyDatabase.Entries)
        {
            if (TryFullScan(encryptedXml, buffer, entry, out var result))
            {
                _logger.Debug($"AutoDecryptQcXml: matched key '{entry.KeyId}'.");
                return result;
            }
        }

        foreach (var entry in _customQcKeys)
        {
            if (TryFullScan(encryptedXml, buffer, entry, out var result))
            {
                _logger.Debug($"AutoDecryptQcXml: matched user key '{entry.KeyId}'.");
                return result;
            }
        }

        return null;
    }

    private bool TryFullScan(byte[] encryptedXml, byte[] buffer, QcKeyEntry entry, out (string Xml, string KeyId) result)
    {
        try
        {
            encryptedXml.CopyTo(buffer, 0);
            var (aesKey, aesIv) = _keyCache.GetOrDeriveQcKey(entry);
            AesCfb128.DecryptInPlace(buffer, aesKey, aesIv);
            var text = Encoding.UTF8.GetString(buffer);
            if (ContainsXmlDeclaration(text))
            {
                result = (TruncateXmlAtLastCloseTag(text), entry.KeyId);
                return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _logger.Warning($"AutoDecryptQcXml: key '{entry.KeyId}' failed.", ex);
        }

        result = default;
        return false;
    }

    /// <summary>
    /// Queries whether the specified key identifier is supported.
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// </summary>
    /// <param name="keyId">Key identifier matched case-insensitively against the built-in QC database and then the
    /// custom entries; <see langword="null"/> or an unknown value simply matches nothing.
    /// <para>先与内置 QC 密钥库、再与自定义条目做不区分大小写匹配的密钥标识符；传入 <see langword="null"/> 或未知值时只是无匹配。</para></param>
    /// <returns><see langword="true"/> when a built-in or custom entry carries that identifier; otherwise
    /// <see langword="false"/>. <para>内置或自定义条目中存在该标识符时为 <see langword="true"/>，否则为 <see langword="false"/>。</para></returns>
    public bool IsSupported(string keyId)
    {
        if (QcKeyDatabase.FindByKeyId(keyId) != null)
        {
            return true;
        }

        foreach (var entry in _customQcKeys)
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

        foreach (var entry in QcKeyDatabase.Entries)
        {
            result.Add(entry.KeyId);
        }

        foreach (var entry in _customQcKeys)
        {
            result.Add(entry.KeyId);
        }

        return result;
    }

    /// <summary>
    /// Adds a custom key.
    /// <para>添加自定义密钥。</para>
    /// </summary>
    /// <param name="keyEntry">A <see cref="QcKeyEntry"/> appended to the custom list, where it is consulted after the
    /// built-in database and by auto-detection; the entry is stored by reference and duplicate identifiers are kept.
    /// <para>要追加到自定义列表的 <see cref="QcKeyEntry"/>，其检索顺序排在内置密钥库之后，自动探测同样会用到；条目按引用存储，标识符重复时一并保留。</para></param>
    public void AddCustomKey(object keyEntry)
    {
        Guard.ThrowIfNull(keyEntry);

        if (keyEntry is QcKeyEntry qc)
        {
            _customQcKeys.Add(qc);
            return;
        }

        throw new ArgumentException(
            $"Unsupported key entry type for QC format: {keyEntry.GetType().Name}. Expected QcKeyEntry.",
            nameof(keyEntry));
    }

    /// <summary>
    /// Exports discovered keys.
    /// <para>导出发现的密钥。</para>
    /// </summary>
    /// <param name="filePath">Destination path accepted by the interface; this QC provider publishes no discovered-key
    /// file, so the value is neither validated nor used and nothing is written or deleted.
    /// <para>接口要求的目标路径；该 QC 提供者不发布已发现密钥文件，因此该值既不校验也不使用，不会写入或删除任何内容。</para></param>
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
        _customQcKeys.Clear();
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

    private QcKeyEntry? FindKey(string keyId)
    {
        return QcKeyDatabase.FindByKeyId(keyId)
            ?? _customQcKeys.FirstOrDefault(k => string.Equals(k.KeyId, keyId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsXmlDeclaration(string text) => text.Contains("<?xml", StringComparison.Ordinal);

    private static string TruncateXmlAtLastCloseTag(string text)
    {
        var lastCloseTag = text.LastIndexOf('>');
        return lastCloseTag >= 0 && lastCloseTag < text.Length - 1 ? text[..(lastCloseTag + 1)] : text;
    }
}