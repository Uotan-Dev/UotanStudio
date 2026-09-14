using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto;

public interface IOppCryptoProvider
{
    /// <summary>
    /// Decrypts data using the specified key.
    /// <para>使用指定密钥解密数据。</para>
    /// </summary>
    byte[] Decrypt(byte[] data, string? keyId = null);

    /// <summary>
    /// Encrypts data using the specified key.
    /// <para>使用指定密钥加密数据。</para>
    /// </summary>
    byte[] Encrypt(byte[] data, string? keyId = null);

    /// <summary>
    /// Decrypts stream data using the specified key.
    /// <para>使用指定密钥解密流数据。</para>
    /// </summary>
    void Decrypt(Stream input, Stream output, string? keyId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Encrypts stream data using the specified key.
    /// <para>使用指定密钥加密流数据。</para>
    /// </summary>
    void Encrypt(Stream input, Stream output, string? keyId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Automatically infers the key and decrypts data.
    /// <para>自动推断密钥并解密数据。</para>
    /// </summary>
    byte[] AutoDecrypt(byte[] data);

    /// <summary>
    /// Automatically infers the key and decrypts stream data.
    /// <para>自动推断密钥并解密流数据。</para>
    /// </summary>
    void AutoDecrypt(Stream input, Stream output, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries whether the specified key identifier is supported.
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// </summary>
    bool IsSupported(string keyId);

    /// <summary>
    /// Adds a custom key.
    /// <para>添加自定义密钥。</para>
    /// </summary>
    void AddCustomKey(object keyEntry);

    /// <summary>
    /// Exports the key discovered by auto-detection to a file.
    /// <para>将自动探测发现的密钥导出到文件。</para>
    /// </summary>
    void ExportDiscoveredKey(string filePath);

    /// <summary>
    /// Gets the list of all known key identifiers for the specified format.
    /// <para>获取指定格式的所有已知密钥标识符列表。</para>
    /// </summary>
    IReadOnlyList<string> GetKnownKeys(OppFormat format);
}