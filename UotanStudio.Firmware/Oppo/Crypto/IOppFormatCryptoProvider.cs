using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Crypto;

public interface IOppFormatCryptoProvider
{
    OppFormat SupportedFormat { get; }

    /// <summary>
    /// Decrypts data using the specified key.
    /// <para>使用指定密钥解密数据。</para>
    /// </summary>
    byte[] Decrypt(byte[] data, string keyId);

    /// <summary>
    /// Encrypts data using the specified key.
    /// <para>使用指定密钥加密数据。</para>
    /// </summary>
    byte[] Encrypt(byte[] data, string keyId);

    /// <summary>
    /// Decrypts stream data using the specified key.
    /// <para>使用指定密钥解密流数据。</para>
    /// </summary>
    void Decrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken);

    /// <summary>
    /// Encrypts stream data using the specified key.
    /// <para>使用指定密钥加密流数据。</para>
    /// </summary>
    void Encrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken);

    /// <summary>
    /// Automatically infers the key and decrypts data.
    /// <para>自动推断密钥并解密数据。</para>
    /// </summary>
    byte[]? AutoDecrypt(byte[] data);

    /// <summary>
    /// Queries whether the specified key identifier is supported.
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// </summary>
    bool IsSupported(string keyId);

    /// <summary>
    /// Gets the list of all known key identifiers for this format.
    /// <para>获取此格式的所有已知密钥标识符列表。</para>
    /// </summary>
    IReadOnlyList<string> GetKnownKeys();

    /// <summary>
    /// Adds a custom key.
    /// <para>添加自定义密钥。</para>
    /// </summary>
    void AddCustomKey(object keyEntry);

    /// <summary>
    /// Exports discovered keys.
    /// <para>导出发现的密钥。</para>
    /// </summary>
    void ExportDiscoveredKeys(string filePath);

    /// <summary>
    /// Clears all key data.
    /// <para>清除所有密钥数据。</para>
    /// </summary>
    void Clear();
}
