using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Crypto;

public sealed class OzipFormatCryptoProvider : IOppFormatCryptoProvider
{
    private const string KeyIdPrefix = "OZ";

    private readonly List<OzipKeyEntry> _customKeys = [];

    public OppFormat SupportedFormat => OppFormat.Ozip;

    /// <summary>
    /// Decrypts data using the specified key.
    /// <para>使用指定密钥解密数据。</para>
    /// </summary>
    public byte[] Decrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);
        Guard.ThrowIfNullOrWhiteSpace(keyId);

        var keyEntry = ResolveKey(keyId) ?? throw new OppDecryptionException($"Key '{keyId}' is not supported for OZip format.");
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        OzipDecryptor.DecryptFile(input, output, keyEntry.KeyBytes);
        return output.ToArray();
    }

    /// <summary>
    /// Encrypts data using the specified key.
    /// <para>使用指定密钥加密数据。</para>
    /// </summary>
    public byte[] Encrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);

        throw new OppDecryptionException(
            "OZip encryption is not supported. OZip is a decryption-only format.");
    }

    /// <summary>
    /// Decrypts stream data using the specified key.
    /// <para>使用指定密钥解密流数据。</para>
    /// </summary>
    public void Decrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);
        Guard.ThrowIfNullOrWhiteSpace(keyId);

        cancellationToken.ThrowIfCancellationRequested();

        var keyEntry = ResolveKey(keyId) ?? throw new OppDecryptionException($"Key '{keyId}' is not supported for OZip format.");
        OzipDecryptor.DecryptFile(input, output, keyEntry.KeyBytes);
    }

    /// <summary>
    /// Encrypts stream data using the specified key.
    /// <para>使用指定密钥加密流数据。</para>
    /// </summary>
    public void Encrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        throw new OppDecryptionException(
            "OZip encryption is not supported. OZip is a decryption-only format.");
    }

    /// <summary>
    /// Automatically infers the key and decrypts data.
    /// <para>自动推断密钥并解密数据。</para>
    /// </summary>
    public byte[]? AutoDecrypt(byte[] data)
    {
        Guard.ThrowIfNull(data);

        using var input = new MemoryStream(data);

        if (!OzipHeader.IsOzipFile(input))
        {
            return null;
        }

        try
        {
            using var output = new MemoryStream();
            OzipDecryptor.DecryptFileWithAutoDetection(input, output);
            return output.ToArray();
        }
        catch (OppDecryptionException)
        {
            return null;
        }
    }

    /// <summary>
    /// Queries whether the specified key identifier is supported.
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// </summary>
    public bool IsSupported(string keyId) => ResolveKey(keyId) != null;

    /// <summary>
    /// Gets the list of all known key identifiers for this format.
    /// <para>获取此格式的所有已知密钥标识符列表。</para>
    /// </summary>
    public IReadOnlyList<string> GetKnownKeys()
    {
        var result = new List<string>();
        for (var i = 0; i < OzipKeyDatabase.Count; i++)
        {
            result.Add($"{KeyIdPrefix}-{i}");
        }

        for (var i = 0; i < _customKeys.Count; i++)
        {
            result.Add($"{KeyIdPrefix}-custom-{i}");
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

        if (keyEntry is OzipKeyEntry ozipEntry)
        {
            _customKeys.Add(ozipEntry);
            return;
        }

        throw new ArgumentException($"Unsupported key entry type: {keyEntry.GetType().Name}. Expected OzipKeyEntry.", nameof(keyEntry));
    }

    /// <summary>
    /// Exports discovered keys.
    /// <para>导出发现的密钥。</para>
    /// </summary>
    public void ExportDiscoveredKeys(string filePath)
    {
        Guard.ThrowIfNullOrWhiteSpace(filePath);

        var lines = new List<string>();
        foreach (var entry in OzipKeyDatabase.GetAllKeys())
        {
            lines.Add(entry.KeyHex);
        }

        foreach (var entry in _customKeys)
        {
            lines.Add(entry.KeyHex);
        }

        File.WriteAllLines(filePath, lines);
    }

    /// <summary>
    /// Clears all key data.
    /// <para>清除所有密钥数据。</para>
    /// </summary>
    public void Clear() => _customKeys.Clear();

    /// <summary>
    /// Searches the known keys for a match (built-in key database only; no brute-force).
    /// <para>在已知密钥中搜索匹配的密钥（仅内置密钥库；不执行暴力破解）。</para>
    /// </summary>
    public OzipKeyEntry? SearchKey(byte[] firstEncrypted16Bytes)
    {
        Guard.ThrowIfNull(firstEncrypted16Bytes);
        return OzipDecryptor.DetectKey(firstEncrypted16Bytes);
    }

    private OzipKeyEntry? ResolveKey(string keyId)
    {

        if (keyId.StartsWith($"{KeyIdPrefix}-custom-", StringComparison.OrdinalIgnoreCase))
        {
            var indexPart = keyId[$"{KeyIdPrefix}-custom-".Length..];
            return int.TryParse(indexPart, out var customIndex) &&
                customIndex >= 0 && customIndex < _customKeys.Count
                ? _customKeys[customIndex]
                : null;
        }

        if (keyId.StartsWith($"{KeyIdPrefix}-", StringComparison.OrdinalIgnoreCase))
        {
            var indexPart = keyId[$"{KeyIdPrefix}-".Length..];
            return int.TryParse(indexPart, out var index) &&
                index >= 0 && index < OzipKeyDatabase.Count
                ? OzipKeyDatabase.FindByIndex(index)
                : null;
        }


        if (keyId.Length == 32 && IsHexString(keyId))
        {
            try
            {
                return new OzipKeyEntry(keyId.ToUpperInvariant());
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsHexString(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
