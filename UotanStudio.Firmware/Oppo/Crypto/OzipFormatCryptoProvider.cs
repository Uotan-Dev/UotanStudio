using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Crypto;

/// <summary>
/// <para>提供 OZip 格式的加解密操作实现。</para>
/// Provides encryption and decryption operations for the OZip format.
/// </summary>
/// <remarks>
/// <para>
/// 此类实现 <see cref="IOppFormatCryptoProvider"/> 接口，为 OZip 加密固件提供
/// AES-128-ECB 解密、密钥自动检测和密钥管理功能。
/// </para>
/// This class implements the <see cref="IOppFormatCryptoProvider"/> interface, providing
/// AES-128-ECB decryption, automatic key detection, and key management for OZip encrypted firmware.
/// </remarks>
public sealed class OzipFormatCryptoProvider : IOppFormatCryptoProvider
{
    private const string KeyIdPrefix = "OZ";

    private readonly List<OzipKeyEntry> _customKeys = [];

    /// <summary>
    /// <para>获取此提供者支持的固件格式。</para>
    /// Gets the firmware format supported by this provider.
    /// </summary>
    public OppFormat SupportedFormat => OppFormat.Ozip;

    /// <summary>
    /// <para>使用指定密钥解密数据。</para>
    /// Decrypts data using the specified key.
    /// </summary>
    /// <param name="data"><para>要解密的数据。</para>The data to decrypt.</param>
    /// <param name="keyId"><para>密钥标识符（格式为 "OZ-{index}" 或密钥的十六进制字符串）。</para>Key identifier (format "OZ-{index}" or the key's hex string).</param>
    /// <returns><para>解密后的数据。</para>The decrypted data.</returns>
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
    /// <para>使用指定密钥加密数据。</para>
    /// Encrypts data using the specified key.
    /// </summary>
    public byte[] Encrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);

        throw new OppDecryptionException(
            "OZip encryption is not supported. OZip is a decryption-only format.");
    }

    /// <summary>
    /// <para>使用指定密钥解密流数据。</para>
    /// Decrypts stream data using the specified key.
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
    /// <para>使用指定密钥加密流数据。</para>
    /// Encrypts stream data using the specified key.
    /// </summary>
    public void Encrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        throw new OppDecryptionException(
            "OZip encryption is not supported. OZip is a decryption-only format.");
    }

    /// <summary>
    /// <para>自动推断密钥并解密数据。</para>
    /// Automatically infers the key and decrypts data.
    /// </summary>
    /// <param name="data"><para>要解密的数据（完整的 OZip 文件内容）。</para>The data to decrypt (complete OZip file contents).</param>
    /// <returns><para>解密后的数据；若无法检测密钥则返回 null。</para>The decrypted data, or null if the key cannot be detected.</returns>
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
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// Queries whether the specified key identifier is supported.
    /// </summary>
    public bool IsSupported(string keyId) => ResolveKey(keyId) != null;

    /// <summary>
    /// <para>获取此格式的所有已知密钥标识符列表。</para>
    /// Gets the list of all known key identifiers for this format.
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
    /// <para>添加自定义密钥。</para>
    /// Adds a custom key.
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
    /// <para>导出发现的密钥。</para>
    /// Exports discovered keys.
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
    /// <para>清除所有密钥数据。</para>
    /// Clears all key data.
    /// </summary>
    public void Clear() => _customKeys.Clear();

    /// <summary>
    /// <para>在已知密钥中搜索匹配的密钥（仅内置密钥库；不执行暴力破解）。</para>
    /// Searches the known keys for a match (built-in key database only; no brute-force).
    /// </summary>
    /// <param name="firstEncrypted16Bytes"><para>加密数据的前 16 字节。</para>The first 16 bytes of encrypted data.</param>
    /// <returns><para>匹配的密钥条目；若未找到则返回 null。</para>The matched key entry, or null if not found.</returns>
    public OzipKeyEntry? SearchKey(byte[] firstEncrypted16Bytes)
    {
        Guard.ThrowIfNull(firstEncrypted16Bytes);
        return OzipDecryptor.DetectKey(firstEncrypted16Bytes);
    }

    private OzipKeyEntry? ResolveKey(string keyId)
    {
        // Try "OZ-{index}" format
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

        // Try as hex string
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