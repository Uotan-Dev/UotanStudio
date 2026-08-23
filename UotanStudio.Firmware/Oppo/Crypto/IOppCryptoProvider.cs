using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Crypto;

/// <summary>
/// <para>提供 Oppo 固件文件的加解密操作接口。</para>
/// Provides encryption and decryption operations for Oppo firmware files.
/// </summary>
public interface IOppCryptoProvider
{
    /// <summary>
    /// <para>使用指定密钥解密数据。</para>
    /// Decrypts data using the specified key.
    /// </summary>
    /// <param name="data"><para>要解密的数据。</para>The data to decrypt.</param>
    /// <param name="keyId"><para>密钥标识符，为 null 时使用默认密钥。</para>Key identifier, null for default key.</param>
    /// <returns><para>解密后的数据。</para>The decrypted data.</returns>
    byte[] Decrypt(byte[] data, string? keyId = null);

    /// <summary>
    /// <para>使用指定密钥加密数据。</para>
    /// Encrypts data using the specified key.
    /// </summary>
    /// <param name="data"><para>要加密的数据。</para>The data to encrypt.</param>
    /// <param name="keyId"><para>密钥标识符，为 null 时使用默认密钥。</para>Key identifier, null for default key.</param>
    /// <returns><para>加密后的数据。</para>The encrypted data.</returns>
    byte[] Encrypt(byte[] data, string? keyId = null);

    /// <summary>
    /// <para>使用指定密钥解密流数据。</para>
    /// Decrypts stream data using the specified key.
    /// </summary>
    /// <param name="input"><para>输入密文流。</para>The input ciphertext stream.</param>
    /// <param name="output"><para>输出明文流。</para>The output plaintext stream.</param>
    /// <param name="keyId"><para>密钥标识符，为 null 时使用默认密钥。</para>Key identifier, null for default key.</param>
    /// <param name="cancellationToken"><para>取消操作的令牌。</para>The token to cancel the operation.</param>
    void Decrypt(Stream input, Stream output, string? keyId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// <para>使用指定密钥加密流数据。</para>
    /// Encrypts stream data using the specified key.
    /// </summary>
    /// <param name="input"><para>输入明文流。</para>The input plaintext stream.</param>
    /// <param name="output"><para>输出密文流。</para>The output ciphertext stream.</param>
    /// <param name="keyId"><para>密钥标识符，为 null 时使用默认密钥。</para>Key identifier, null for default key.</param>
    /// <param name="cancellationToken"><para>取消操作的令牌。</para>The token to cancel the operation.</param>
    void Encrypt(Stream input, Stream output, string? keyId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// <para>自动推断密钥并解密数据。</para>
    /// Automatically infers the key and decrypts data.
    /// </summary>
    /// <param name="data"><para>要解密的数据。</para>The data to decrypt.</param>
    /// <returns><para>解密后的数据。</para>The decrypted data.</returns>
    byte[] AutoDecrypt(byte[] data);

    /// <summary>
    /// <para>自动推断密钥并解密流数据。</para>
    /// Automatically infers the key and decrypts stream data.
    /// </summary>
    /// <param name="input"><para>输入密文流。</para>The input ciphertext stream.</param>
    /// <param name="output"><para>输出明文流。</para>The output plaintext stream.</param>
    /// <param name="cancellationToken"><para>取消操作的令牌。</para>The token to cancel the operation.</param>
    void AutoDecrypt(Stream input, Stream output, CancellationToken cancellationToken = default);

    /// <summary>
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// Queries whether the specified key identifier is supported.
    /// </summary>
    /// <param name="keyId"><para>要查询的密钥标识符。</para>The key identifier to query.</param>
    /// <returns><para>若支持返回 true；否则返回 false。</para>True if supported; otherwise, false.</returns>
    bool IsSupported(string keyId);

    /// <summary>
    /// <para>添加自定义密钥。</para>
    /// Adds a custom key.
    /// </summary>
    /// <param name="keyEntry"><para>密钥条目（QcKeyEntry、MtkKeyEntry、OpsKeyEntry 或 OzipKeyEntry）。</para>Key entry (QcKeyEntry, MtkKeyEntry, OpsKeyEntry, or OzipKeyEntry).</param>
    void AddCustomKey(object keyEntry);

    /// <summary>
    /// <para>将自动探测发现的密钥导出到文件。</para>
    /// Exports the key discovered by auto-detection to a file.
    /// </summary>
    /// <param name="filePath"><para>导出文件路径。</para>Export file path.</param>
    void ExportDiscoveredKey(string filePath);

    /// <summary>
    /// <para>获取指定格式的所有已知密钥标识符列表。</para>
    /// Gets the list of all known key identifiers for the specified format.
    /// </summary>
    /// <param name="format"><para>固件格式。</para>Firmware format.</param>
    /// <returns><para>密钥标识符列表。</para>List of key identifiers.</returns>
    IReadOnlyList<string> GetKnownKeys(OppFormat format);
}