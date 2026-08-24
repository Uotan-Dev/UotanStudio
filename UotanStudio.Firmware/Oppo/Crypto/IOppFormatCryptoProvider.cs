using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Crypto;

/// <summary>
/// <para>定义特定格式的加解密操作接口。</para>
/// Defines format-specific encryption and decryption operations.
/// </summary>
public interface IOppFormatCryptoProvider
{
    /// <summary>
    /// <para>获取此提供者支持的固件格式。</para>
    /// Gets the firmware format supported by this provider.
    /// </summary>
    OppFormat SupportedFormat { get; }

    /// <summary>
    /// <para>使用指定密钥解密数据。</para>
    /// Decrypts data using the specified key.
    /// </summary>
    byte[] Decrypt(byte[] data, string keyId);

    /// <summary>
    /// <para>使用指定密钥加密数据。</para>
    /// Encrypts data using the specified key.
    /// </summary>
    byte[] Encrypt(byte[] data, string keyId);

    /// <summary>
    /// <para>使用指定密钥解密流数据。</para>
    /// Decrypts stream data using the specified key.
    /// </summary>
    void Decrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken);

    /// <summary>
    /// <para>使用指定密钥加密流数据。</para>
    /// Encrypts stream data using the specified key.
    /// </summary>
    void Encrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken);

    /// <summary>
    /// <para>自动推断密钥并解密数据。</para>
    /// Automatically infers the key and decrypts data.
    /// </summary>
    byte[]? AutoDecrypt(byte[] data);

    /// <summary>
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// Queries whether the specified key identifier is supported.
    /// </summary>
    bool IsSupported(string keyId);

    /// <summary>
    /// <para>获取此格式的所有已知密钥标识符列表。</para>
    /// Gets the list of all known key identifiers for this format.
    /// </summary>
    IReadOnlyList<string> GetKnownKeys();

    /// <summary>
    /// <para>添加自定义密钥。</para>
    /// Adds a custom key.
    /// </summary>
    void AddCustomKey(object keyEntry);

    /// <summary>
    /// <para>导出发现的密钥。</para>
    /// Exports discovered keys.
    /// </summary>
    void ExportDiscoveredKeys(string filePath);

    /// <summary>
    /// <para>清除所有密钥数据。</para>
    /// Clears all key data.
    /// </summary>
    void Clear();
}