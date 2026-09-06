using System.Text;
using UotanStudio.Firmware.Oppo.Crypto.Algorithms;
using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;
using UotanStudio.Firmware.Oppo.Diagnostics;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Crypto;

internal sealed class OfpQcCryptoProvider(IOppLogger? logger = null) : IOppFormatCryptoProvider, IDisposable
{
    private readonly IOppLogger _logger = logger ?? NullOppLogger.Instance;
    private readonly KeyDerivationCache _keyCache = new();
    private readonly List<QcKeyEntry> _customQcKeys = [];
    private bool _disposed;

    public OppFormat SupportedFormat => OppFormat.OfpQc;

    /// <summary>
    /// Decrypts data using the specified key.
    /// <para>使用指定密钥解密数据。</para>
    /// </summary>
    public byte[] Decrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"QC key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveQcKey(entry);
        return AesCfb128.Decrypt(data, aesKey, aesIv);
    }

    /// <summary>
    /// Encrypts data using the specified key.
    /// <para>使用指定密钥加密数据。</para>
    /// </summary>
    public byte[] Encrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"QC key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveQcKey(entry);
        return AesCfb128.Encrypt(data, aesKey, aesIv);
    }

    /// <summary>
    /// Decrypts stream data using the specified key.
    /// <para>使用指定密钥解密流数据。</para>
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
    /// Encrypts stream data using the specified key.
    /// <para>使用指定密钥加密流数据。</para>
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
    /// Automatically infers the QC key and decrypts data.
    /// <para>自动推断 QC 密钥并解密数据。</para>
    /// </summary>
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
    /// Queries whether the specified key identifier is supported.
    /// <para>查询是否支持指定的密钥标识符。</para>
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

    private static bool ContainsXmlDeclaration(string text) => text.Contains("<?xml");

    private static string TruncateXmlAtLastCloseTag(string text)
    {
        var lastCloseTag = text.LastIndexOf('>');
        return lastCloseTag >= 0 && lastCloseTag < text.Length - 1 ? text[..(lastCloseTag + 1)] : text;
    }
}
