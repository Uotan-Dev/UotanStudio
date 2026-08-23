using UotanStudio.Firmware.NTPi.Models;

namespace UotanStudio.Firmware.NTPi.Crypto;

/// <summary>
/// <para>AES 加密解密服务接口。</para>
/// Interface for AES encryption and decryption services.
/// </summary>
public interface ICryptoProvider
{
    /// <summary>
    /// <para>使用 AES-256-CBC 模式解密数据。</para>
    /// Decrypts data using AES-256-CBC mode.
    /// </summary>
    /// <param name="data"><para>要解密的密文数据。</para>Ciphertext data to decrypt.</param>
    /// <param name="key"><para>解密密钥。</para>Decryption key.</param>
    /// <param name="iv"><para>初始化向量。</para>Initialization vector.</param>
    /// <returns><para>解密后的明文数据。</para>Decrypted plaintext data.</returns>
    byte[] Decrypt(byte[] data, byte[] key, byte[] iv);

    /// <summary>
    /// <para>使用 AES-256-CBC 模式加密数据。</para>
    /// Encrypts data using AES-256-CBC mode.
    /// </summary>
    /// <param name="data"><para>要加密的明文数据。</para>Plaintext data to encrypt.</param>
    /// <param name="key"><para>加密密钥。</para>Encryption key.</param>
    /// <param name="iv"><para>初始化向量。</para>Initialization vector.</param>
    /// <returns><para>加密后的密文数据。</para>Encrypted ciphertext data.</returns>
    byte[] Encrypt(byte[] data, byte[] key, byte[] iv);
}

/// <summary>
/// <para>密钥提供者接口。</para>
/// Interface for key providers.
/// </summary>
public interface IKeyProvider
{
    /// <summary>
    /// <para>获取指定区域的加密密钥和初始化向量。</para>
    /// Gets the encryption key and initialization vector for the specified region.
    /// </summary>
    /// <param name="regionType"><para>区域类型。</para>Region type.</param>
    /// <returns><para>密钥和初始化向量,如果未找到则返回 null。</para>Key and IV, or null if not found.</returns>
    (byte[] Key, byte[] Iv)? GetKey(RegionType regionType);
}

/// <summary>
/// <para>基于 NTPI 固件版本号的密钥提供者实现。</para>
/// Key provider implementation based on NTPI firmware version numbers.
/// </summary>
public sealed class VersionKeyProvider : IKeyProvider
{
    private readonly Dictionary<int, NtpiVersionKey> _versionKeys;

    /// <summary>
    /// <para>使用指定的版本号初始化 VersionKeyProvider 的新实例。</para>
    /// Initializes a new instance of VersionKeyProvider with the specified version numbers.
    /// </summary>
    /// <exception cref="Exceptions.UnsupportedVersionException">指定版本未在注册表中找到对应的密钥。</exception>
    public VersionKeyProvider(ulong major, ulong minor, ulong patch)
    {
        var keys = NtpiVersionRegistry.GetKeysForVersion(major, minor, patch)
            ?? throw new Exceptions.UnsupportedVersionException(major, minor, patch);
        _versionKeys = keys;
    }

    /// <inheritdoc />
    public (byte[] Key, byte[] Iv)? GetKey(RegionType regionType)
    {
        return _versionKeys.TryGetValue((int)regionType, out var versionKey)
            ? (versionKey.Key, versionKey.Iv)
            : null;
    }
}