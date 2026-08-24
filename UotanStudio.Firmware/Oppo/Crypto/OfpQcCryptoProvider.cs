using System.Text;
using UotanStudio.Firmware.Oppo.Crypto.Algorithms;
using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;
using UotanStudio.Firmware.Oppo.Diagnostics;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Crypto;

/// <summary>
/// <para>OFP QC 格式的加密提供者，实现 QC 密钥的自动检测与加解密。</para>
/// Crypto provider for the OFP QC format, implementing QC key auto-detection and encryption/decryption.
/// </summary>
/// <remarks>
/// <para>初始化 <see cref="OfpQcCryptoProvider"/> 类的新实例。</para>
/// Initializes a new instance of the <see cref="OfpQcCryptoProvider"/> class.
/// </remarks>
/// <param name="logger"><para>可选的日志记录器实例。</para>Optional logger instance.</param>
internal sealed class OfpQcCryptoProvider(IOppLogger? logger = null) : IOppFormatCryptoProvider, IDisposable
{
    private readonly IOppLogger _logger = logger ?? NullOppLogger.Instance;
    private readonly KeyDerivationCache _keyCache = new();
    private readonly List<QcKeyEntry> _customQcKeys = [];
    private bool _disposed;

    /// <summary>
    /// <para>获取此提供者支持的固件格式。</para>
    /// Gets the firmware format supported by this provider.
    /// </summary>
    public OppFormat SupportedFormat => OppFormat.OfpQc;

    /// <summary>
    /// <para>使用指定密钥解密数据。</para>
    /// Decrypts data using the specified key.
    /// </summary>
    /// <param name="data"><para>要解密的数据。</para>The data to decrypt.</param>
    /// <param name="keyId"><para>QC 密钥标识符。</para>The QC key identifier.</param>
    /// <returns><para>解密后的数据。</para>The decrypted data.</returns>
    public byte[] Decrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"QC key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveQcKey(entry);
        return AesCfb128.Decrypt(data, aesKey, aesIv);
    }

    /// <summary>
    /// <para>使用指定密钥加密数据。</para>
    /// Encrypts data using the specified key.
    /// </summary>
    /// <param name="data"><para>要加密的数据。</para>The data to encrypt.</param>
    /// <param name="keyId"><para>QC 密钥标识符。</para>The QC key identifier.</param>
    /// <returns><para>加密后的数据。</para>The encrypted data.</returns>
    public byte[] Encrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"QC key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveQcKey(entry);
        return AesCfb128.Encrypt(data, aesKey, aesIv);
    }

    /// <summary>
    /// <para>使用指定密钥解密流数据。</para>
    /// Decrypts stream data using the specified key.
    /// </summary>
    public void Decrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"QC key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveQcKey(entry);
        AesCfb128.Decrypt(input, output, aesKey, aesIv, cancellationToken);
    }

    /// <summary>
    /// <para>使用指定密钥加密流数据。</para>
    /// Encrypts stream data using the specified key.
    /// </summary>
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
    /// <para>自动推断 QC 密钥并解密数据。</para>
    /// Automatically infers the QC key and decrypts data.
    /// </summary>
    /// <param name="data"><para>要解密的数据。</para>The data to decrypt.</param>
    /// <returns><para>解密后的数据，若无密钥可用则返回 null。</para>The decrypted data, or null if no key works.</returns>
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
    /// <para>自动探测 QC 密钥并解密 XML 数据。</para>
    /// Automatically detects QC key and decrypts XML data.
    /// </summary>
    /// <param name="encryptedXml"><para>加密的 XML 数据。</para>The encrypted XML data.</param>
    /// <returns><para>解密后的 XML 字符串和密钥标识，若无密钥可用则返回 null。</para>The decrypted XML string and key ID, or null if no key works.</returns>
    internal (string Xml, string KeyId)? AutoDecryptQcXml(byte[] encryptedXml)
    {
        Guard.ThrowIfNull(encryptedXml);

        foreach (var entry in QcKeyDatabase.Entries)
        {
            try
            {
                var (aesKey, aesIv) = _keyCache.GetOrDeriveQcKey(entry);
                var decrypted = AesCfb128.Decrypt(encryptedXml, aesKey, aesIv);
                var text = Encoding.UTF8.GetString(decrypted);

                if (ContainsXmlDeclaration(text))
                {
                    _logger.Debug($"AutoDecryptQcXml: matched key '{entry.KeyId}'.");
                    return (TruncateXmlAtLastCloseTag(text), entry.KeyId);
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                _logger.Warning($"AutoDecryptQcXml: key '{entry.KeyId}' failed.", ex);
            }
        }

        // Try user keys added via AddCustomKey
        foreach (var entry in _customQcKeys)
        {
            try
            {
                var (aesKey, aesIv) = _keyCache.GetOrDeriveQcKey(entry);
                var decrypted = AesCfb128.Decrypt(encryptedXml, aesKey, aesIv);
                var text = Encoding.UTF8.GetString(decrypted);

                if (ContainsXmlDeclaration(text))
                {
                    _logger.Debug($"AutoDecryptQcXml: matched user key '{entry.KeyId}'.");
                    return (TruncateXmlAtLastCloseTag(text), entry.KeyId);
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                _logger.Warning($"AutoDecryptQcXml: user key '{entry.KeyId}' failed.", ex);
            }
        }

        return null;
    }

    /// <summary>
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// Queries whether the specified key identifier is supported.
    /// </summary>
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
    /// <para>获取此格式的所有已知密钥标识符列表。</para>
    /// Gets the list of all known key identifiers for this format.
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
    /// <para>添加自定义密钥。</para>
    /// Adds a custom key.
    /// </summary>
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
    /// <para>导出发现的密钥。</para>
    /// Exports discovered keys.
    /// </summary>
    public void ExportDiscoveredKeys(string filePath)
    {
        // No-op: the QC provider currently only uses built-in keys
    }

    /// <summary>
    /// <para>清除所有密钥数据。</para>
    /// Clears all key data.
    /// </summary>
    public void Clear()
    {
        _keyCache.Clear();
        _customQcKeys.Clear();
    }

    /// <summary>
    /// <para>释放此实例使用的所有资源。</para>
    /// Releases all resources used by this instance.
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

    private static bool ContainsXmlDeclaration(string text) => text.Contains("<?xml");

    private static string TruncateXmlAtLastCloseTag(string text)
    {
        var lastCloseTag = text.LastIndexOf('>');
        return lastCloseTag >= 0 && lastCloseTag < text.Length - 1 ? text[..(lastCloseTag + 1)] : text;
    }
}