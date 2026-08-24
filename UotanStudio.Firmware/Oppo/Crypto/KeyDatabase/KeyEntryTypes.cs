namespace UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;

/// <summary>
/// <para>QC 密钥条目，包含密钥三元组和适用机型描述。</para>
/// QC key entry containing the key triple and applicable device description.
/// </summary>
/// <param name="KeyId"><para>密钥标识符（如 "V1.5.13"）。</para>The key identifier (e.g., "V1.5.13").</param>
/// <param name="Mc"><para>混淆密钥（16 字节 hex 字符串）。</para>The obfuscation key (16-byte hex string).</param>
/// <param name="UserKey"><para>AES Key 源（16 字节 hex 字符串）。</para>The AES key source (16-byte hex string).</param>
/// <param name="Ivec"><para>IV 源（16 字节 hex 字符串）。</para>The IV source (16-byte hex string).</param>
/// <param name="Description"><para>适用机型描述。</para>The description of applicable device models.</param>
public sealed record QcKeyEntry(
    string KeyId,
    string Mc,
    string UserKey,
    string Ivec,
    string Description);

/// <summary>
/// <para>MTK 密钥条目，包含三元素或二元素密钥配置。</para>
/// MTK key entry containing either a 3-element or 2-element key configuration.
/// </summary>
/// <param name="KeyId"><para>密钥标识符（如 "MTK0"）。</para>The key identifier (e.g., "MTK0").</param>
/// <param name="Mc"><para>混淆密钥（16 字节 hex 字符串），三元素条目使用；二元素条目为 null。</para>The obfuscation key (16-byte hex string), used by 3-element entries; null for 2-element entries.</param>
/// <param name="EncAesKey"><para>加密的 AES Key 源（16 字节 hex 字符串），三元素条目使用；二元素条目为 null。</para>The encrypted AES key source (16-byte hex string), used by 3-element entries; null for 2-element entries.</param>
/// <param name="EncAesIv"><para>加密的 AES IV 源（16 字节 hex 字符串），三元素条目使用；二元素条目为 null。</para>The encrypted AES IV source (16-byte hex string), used by 3-element entries; null for 2-element entries.</param>
/// <param name="DirectAesKey"><para>直接 AES Key（hex 字符串），二元素条目使用；三元素条目为 null。</para>The direct AES key (hex string), used by 2-element entries; null for 3-element entries.</param>
/// <param name="DirectAesIv"><para>直接 AES IV（hex 字符串），二元素条目使用；三元素条目为 null。</para>The direct AES IV (hex string), used by 2-element entries; null for 3-element entries.</param>
/// <param name="Description"><para>适用机型描述。</para>The description of applicable device models.</param>
public sealed record MtkKeyEntry(
    string KeyId,
    string? Mc,
    string? EncAesKey,
    string? EncAesIv,
    string? DirectAesKey,
    string? DirectAesIv,
    string Description)
{
    /// <summary>
    /// <para>判断此条目是否为三元素密钥配置（Mc + EncAesKey + EncAesIv）。</para>
    /// Determines whether this entry is a 3-element key configuration (Mc + EncAesKey + EncAesIv).
    /// </summary>
    public bool Is3Element => Mc is not null && EncAesKey is not null && EncAesIv is not null;

    /// <summary>
    /// <para>判断此条目是否为二元素密钥配置（DirectAesKey + DirectAesIv）。</para>
    /// Determines whether this entry is a 2-element key configuration (DirectAesKey + DirectAesIv).
    /// </summary>
    public bool Is2Element => DirectAesKey is not null && DirectAesIv is not null;
}

/// <summary>
/// <para>OPS 密钥条目，包含自定义密钥调度所需的 MBox 常量。</para>
/// OPS key entry containing the MBox constants required for the custom key schedule.
/// </summary>
/// <param name="KeyId"><para>密钥标识符（如 "mbox5"）。</para>The key identifier (e.g., "mbox5").</param>
/// <param name="MBox"><para>62 字节的轮密钥扩展常量。</para>The 62-byte round key expansion constants.</param>
/// <param name="Description"><para>适用机型描述。</para>The description of applicable device models.</param>
public sealed record OpsKeyEntry(
    string KeyId,
    byte[] MBox,
    string Description);

/// <summary>
/// <para>OZip 密钥条目，包含 AES-128 密钥的十六进制字符串和适用机型描述。</para>
/// OZip key entry containing the AES-128 key hex string and applicable device description.
/// </summary>
public sealed record OzipKeyEntry
{
    /// <summary>
    /// <para>获取密钥的十六进制字符串表示。</para>
    /// Gets the hexadecimal string representation of the key.
    /// </summary>
    public string KeyHex { get; init; }

    /// <summary>
    /// <para>获取密钥的字节数组。</para>
    /// Gets the key as a byte array.
    /// </summary>
    public byte[] KeyBytes { get; init; }

    /// <summary>
    /// <para>获取密钥的描述信息（适用机型等）。</para>
    /// Gets the key description (applicable device models, etc.).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// <para>使用十六进制字符串和可选描述初始化 <see cref="OzipKeyEntry"/> 的新实例。</para>
    /// Initializes a new instance of the <see cref="OzipKeyEntry"/> with a hex string and optional description.
    /// </summary>
    /// <param name="keyHex"><para>密钥的十六进制字符串表示（32 个字符，16 字节）。</para>The hexadecimal string representation of the key (32 characters, 16 bytes).</param>
    /// <param name="description"><para>密钥的描述信息。</para>The description of the key.</param>
    public OzipKeyEntry(string keyHex, string? description = null)
    {
        KeyHex = keyHex ?? throw new ArgumentNullException(nameof(keyHex));
        KeyBytes = HexDecode(keyHex);
        Description = description;
    }

    private static byte[] HexDecode(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            throw new ArgumentException("Hex string must have an even number of characters.", nameof(hex));
        }

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var high = HexCharToInt(hex[i * 2]);
            var low = HexCharToInt(hex[(i * 2) + 1]);
            bytes[i] = (byte)((high << 4) | low);
        }

        return bytes;
    }

    private static int HexCharToInt(char c)
    {
        return c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => throw new ArgumentException($"Invalid hexadecimal character: '{c}'.")
        };
    }
}