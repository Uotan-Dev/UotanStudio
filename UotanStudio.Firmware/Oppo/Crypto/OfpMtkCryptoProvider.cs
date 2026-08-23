using UotanStudio.Firmware.Oppo.Crypto.Algorithms;
using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;
using UotanStudio.Firmware.Oppo.Diagnostics;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Crypto;

/// <summary>
/// <para>OFP MTK 格式的加密提供者，实现 MTK 密钥的自动检测与加解密。</para>
/// Crypto provider for the OFP MTK format, implementing MTK key auto-detection and encryption/decryption.
/// </summary>
/// <remarks>
/// <para>初始化 <see cref="OfpMtkCryptoProvider"/> 类的新实例。</para>
/// Initializes a new instance of the <see cref="OfpMtkCryptoProvider"/> class.
/// </remarks>
/// <param name="logger"><para>可选的日志记录器实例。</para>Optional logger instance.</param>
internal sealed class OfpMtkCryptoProvider(IOppLogger? logger = null) : IOppFormatCryptoProvider, IDisposable
{
    private readonly IOppLogger _logger = logger ?? NullOppLogger.Instance;
    private readonly KeyDerivationCache _keyCache = new();
    private readonly List<MtkKeyEntry> _customMtkKeys = [];
    private bool _disposed;

    /// <summary>
    /// <para>获取此提供者支持的固件格式。</para>
    /// Gets the firmware format supported by this provider.
    /// </summary>
    public OppFormat SupportedFormat => OppFormat.OfpMtk;

    /// <summary>
    /// <para>使用指定密钥解密数据。</para>
    /// Decrypts data using the specified key.
    /// </summary>
    public byte[] Decrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"MTK key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveMtkKey(entry);
        return AesCfb128.Decrypt(data, aesKey, aesIv);
    }

    /// <summary>
    /// <para>使用指定密钥加密数据。</para>
    /// Encrypts data using the specified key.
    /// </summary>
    public byte[] Encrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"MTK key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveMtkKey(entry);
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
            ?? throw new OppDecryptionException($"MTK key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveMtkKey(entry);
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
            ?? throw new OppDecryptionException($"MTK key '{keyId}' not found in the key database.");

        var (aesKey, aesIv) = _keyCache.GetOrDeriveMtkKey(entry);
        AesCfb128.Encrypt(input, output, aesKey, aesIv, cancellationToken);
    }

    /// <summary>
    /// <para>自动推断 MTK 密钥并解密数据。</para>
    /// Automatically infers the MTK key and decrypts data.
    /// </summary>
    /// <param name="data"><para>要解密的数据。</para>The data to decrypt.</param>
    /// <returns><para>解密后的数据，若无密钥可用则返回 null。</para>The decrypted data, or null if no key works.</returns>
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
    /// <para>自动探测 MTK 密钥。</para>
    /// Automatically detects the MTK key.
    /// </summary>
    /// <param name="first16Bytes"><para>文件的前 16 字节数据。</para>The first 16 bytes of the file data.</param>
    /// <returns><para>匹配的密钥条目，若无密钥可用则返回 null。</para>The matching key entry, or null if no key works.</returns>
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

                // Check if decrypted data starts with "MMM" (0x4D, 0x4D, 0x4D)
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

        // Try user keys added via AddCustomKey
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
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// Queries whether the specified key identifier is supported.
    /// </summary>
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
    /// <para>获取此格式的所有已知密钥标识符列表。</para>
    /// Gets the list of all known key identifiers for this format.
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
    /// <para>添加自定义密钥。</para>
    /// Adds a custom key.
    /// </summary>
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
    /// <para>导出发现的密钥。</para>
    /// Exports discovered keys.
    /// </summary>
    public void ExportDiscoveredKeys(string filePath)
    {
        // No-op: the MTK provider currently only uses built-in keys
    }

    /// <summary>
    /// <para>清除所有密钥数据。</para>
    /// Clears all key data.
    /// </summary>
    public void Clear()
    {
        _keyCache.Clear();
        _customMtkKeys.Clear();
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

    private MtkKeyEntry? FindKey(string keyId)
    {
        return MtkKeyDatabase.FindByKeyId(keyId)
            ?? _customMtkKeys.FirstOrDefault(k => string.Equals(k.KeyId, keyId, StringComparison.OrdinalIgnoreCase));
    }
}