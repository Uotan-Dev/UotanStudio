using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto.Algorithms;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto.KeyDatabase;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Diagnostics;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto;

internal sealed class OfpMtkCryptoProvider(IOppLogger? logger = null) : IOppFormatCryptoProvider, IDisposable
{
    private readonly IOppLogger _logger = logger ?? NullOppLogger.Instance;
    private readonly KeyDerivationCache _keyCache = new();
    private readonly List<MtkKeyEntry> _customMtkKeys = [];
    private bool _disposed;

    public OppFormat SupportedFormat => OppFormat.OfpMtk;

    /// <summary>
    /// Decrypts data using the specified key.
    /// <para>使用指定密钥解密数据。</para>
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
    /// Encrypts data using the specified key.
    /// <para>使用指定密钥加密数据。</para>
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
    /// Decrypts stream data using the specified key.
    /// <para>使用指定密钥解密流数据。</para>
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
    /// Encrypts stream data using the specified key.
    /// <para>使用指定密钥加密流数据。</para>
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
    /// Automatically infers the MTK key and decrypts data.
    /// <para>自动推断 MTK 密钥并解密数据。</para>
    /// </summary>
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
