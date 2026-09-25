namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto.KeyDatabase;

/// <summary>
/// Key set for a Qualcomm-based OFP package: every material field is stored as a hex string and the
/// derivation turns it into a 16-byte AES key and IV.
/// <para>基于高通平台 OFP 包的密钥集合：各材料字段均以十六进制字符串存储，派生过程将其转换为 16 字节的 AES 密钥与 IV。</para>
/// </summary>
/// <param name="KeyId">Identifier the QC provider looks the entry up by, e.g. <c>V1.6.17</c>.
/// <para>QC 提供者检索该条目所用的标识符，例如 <c>V1.6.17</c>。</para></param>
/// <param name="Mc">Hex-encoded merchant code; its decoded bytes are the buffer that gets de-obfuscated
/// against the key and IV material.
/// <para>十六进制编码的商户码；其解码字节是被去混淆处理的缓冲，分别与密钥和 IV 材料作用。</para></param>
/// <param name="UserKey">Hex-encoded obfuscated key material; the <c>Mc</c> bytes are XORed with it and
/// nibble-swapped, and the first 16 characters of the MD5 hex digest of that result become the AES key.
/// <para>十六进制编码的混淆密钥材料；将 <c>Mc</c> 字节与其异或并做半字节交换，取结果 MD5 十六进制摘要的前 16 个字符作为 AES 密钥。</para></param>
/// <param name="Ivec">Hex-encoded obfuscated IV material; the same procedure with <c>Mc</c> yields the AES IV.
/// <para>十六进制编码的混淆 IV 材料；以同样方式与 <c>Mc</c> 作用得到 AES IV。</para></param>
/// <param name="Description">Free-text device note shown to the user; the derivation ignores it.
/// <para>向用户展示的自由文本机型说明；派生过程会忽略它。</para></param>
public sealed record QcKeyEntry(
    string KeyId,
    string Mc,
    string UserKey,
    string Ivec,
    string Description);

/// <summary>
/// Key set for a MediaTek-based OFP package. An entry carries either the 3-element shape
/// (<c>Mc</c> plus wrapped key and IV) or the 2-element shape (direct key and IV text), and the
/// corresponding <see cref="Is3Element"/> / <see cref="Is2Element"/> flag selects the derivation.
/// <para>基于联发科平台 OFP 包的密钥集合。条目要么是 3 元素形态（<c>Mc</c> 加包装密钥与 IV），
/// 要么是 2 元素形态（直接密钥与 IV 文本），由对应的 <see cref="Is3Element"/> / <see cref="Is2Element"/> 标记决定派生路径。</para>
/// </summary>
/// <param name="KeyId">Identifier the MTK provider looks the entry up by, e.g. <c>MTK0</c>.
/// <para>MTK 提供者检索该条目所用的标识符，例如 <c>MTK0</c>。</para></param>
/// <param name="Mc">Hex-encoded merchant code used as the shuffle mask for the wrapped fields;
/// <see langword="null"/> on a 2-element entry.
/// <para>作为包装字段混淆掩码的十六进制编码商户码；2 元素条目为 <see langword="null"/>。</para></param>
/// <param name="EncAesKey">Hex-encoded wrapped key; its bytes are XORed with <c>Mc</c>, nibble-swapped,
/// and the first 16 characters of the MD5 hex digest become the AES key. <see langword="null"/> on a
/// 2-element entry.
/// <para>十六进制编码的包装密钥；其字节与 <c>Mc</c> 异或并做半字节交换，取 MD5 十六进制摘要的前 16 个字符作为 AES 密钥。
/// 2 元素条目为 <see langword="null"/>。</para></param>
/// <param name="EncAesIv">Hex-encoded wrapped IV processed the same way to produce the AES IV;
/// <see langword="null"/> on a 2-element entry.
/// <para>以同样方式处理得到 AES IV 的十六进制编码包装 IV；2 元素条目为 <see langword="null"/>。</para></param>
/// <param name="DirectAesKey">AES key text used verbatim as UTF-8 bytes; <see langword="null"/> on a
/// 3-element entry.
/// <para>按 UTF-8 字节原样使用的 AES 密钥文本；3 元素条目为 <see langword="null"/>。</para></param>
/// <param name="DirectAesIv">AES IV text used verbatim as UTF-8 bytes; <see langword="null"/> on a
/// 3-element entry.
/// <para>按 UTF-8 字节原样使用的 AES IV 文本；3 元素条目为 <see langword="null"/>。</para></param>
/// <param name="Description">Free-text device note shown to the user; the derivation ignores it.
/// <para>向用户展示的自由文本机型说明；派生过程会忽略它。</para></param>
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
    /// Marks the entry as carrying the wrapped 3-element material.
    /// <para>标记该条目携带 3 元素的包装密钥材料。</para></summary>
    /// <value>
    /// <see langword="true"/> when <c>Mc</c>, <c>EncAesKey</c> and <c>EncAesIv</c> are all non-null, which
    /// is the shape the 3-element derivation requires.
    /// <para>当 <c>Mc</c>、<c>EncAesKey</c> 与 <c>EncAesIv</c> 均非 null 时为 <see langword="true"/>，这是 3 元素派生所要求的形态。</para></value>
    public bool Is3Element => Mc is not null && EncAesKey is not null && EncAesIv is not null;

    /// <summary>
    /// Marks the entry as carrying direct key and IV text.
    /// <para>标记该条目携带直接的密钥与 IV 文本。</para></summary>
    /// <value>
    /// <see langword="true"/> when both <c>DirectAesKey</c> and <c>DirectAesIv</c> are non-null; an entry
    /// with neither shape makes the derivation throw.
    /// <para>当 <c>DirectAesKey</c> 与 <c>DirectAesIv</c> 均非 null 时为 <see langword="true"/>；两种形态都不满足的条目会使派生过程抛出异常。</para></value>
    public bool Is2Element => DirectAesKey is not null && DirectAesIv is not null;
}

/// <summary>
/// Key set for an OPS package payload: the mailbox blob the OPS cipher consumes together with the
/// shared round-key core.
/// <para>OPS 包负载的密钥集合：OPS 算法与共享轮密钥核心一同消费的 mailbox 数据块。</para>
/// </summary>
/// <param name="KeyId">Key identifier, e.g. <c>mbox5</c>; identifiers starting with <c>mbox</c> are routed
/// to the OPS provider.
/// <para>密钥标识符，例如 <c>mbox5</c>；以 <c>mbox</c> 开头的标识符会被路由到 OPS 提供者。</para></param>
/// <param name="MBox">Raw mailbox bytes; the built-in entries are 62 bytes long and a custom entry's length
/// is not validated before the cipher reads it. The array is held by reference, so it must not be mutated
/// after the entry is created.
/// <para>原始 mailbox 字节；内置条目为 62 字节，自定义条目的长度在进入算法读取前不做校验。数组按引用持有，因此条目创建后不得再修改。</para></param>
/// <param name="Description">Free-text build or device note shown to the user.
/// <para>面向用户展示的自由文本版本或机型说明。</para></param>
public sealed record OpsKeyEntry(
    string KeyId,
    byte[] MBox,
    string Description);

/// <summary>
/// AES key for an OZIP archive, held both as hex text and as the decoded key bytes.
/// <para>OZIP 归档的 AES 密钥，同时保存十六进制文本与解码后的密钥字节。</para>
/// </summary>
public sealed record OzipKeyEntry
{
    /// <summary>
    /// The hex text the entry was created from.
    /// <para>创建该条目时使用的十六进制文本。</para></summary>
    /// <value>
    /// Stored verbatim, including its original letter case; by construction it has an even length and
    /// contains only hex digits. Built-in entries use 32 characters.
    /// <para>按原样存储（保留原有大小写）；其长度必为偶数且仅含十六进制字符。内置条目使用 32 个字符。</para></value>
    public string KeyHex { get; init; }

    /// <summary>
    /// The decoded key material used by the OZIP decryption paths.
    /// <para>OZIP 解密路径使用的解码密钥材料。</para></summary>
    /// <value>
    /// Exactly half the character count of <see cref="KeyHex"/> in bytes, decoded once during construction
    /// and never re-derived afterwards.
    /// <para>字节数恰为 <see cref="KeyHex"/> 字符数的一半，在构造时解码一次，构造完成后不重新推导。</para></value>
    public byte[] KeyBytes { get; init; }

    /// <summary>
    /// Note describing which devices the key covers.
    /// <para>说明该密钥覆盖机型的备注。</para></summary>
    /// <value>
    /// Free text as supplied to the constructor, or <see langword="null"/> when the caller omitted it.
    /// <para>按构造调用原样传入的自由文本；调用方省略时为 <see langword="null"/>。</para></value>
    public string? Description { get; init; }

    /// <summary>
    /// Initializes a new instance of the OzipKeyEntry with a hex string and optional description.
    /// The hex text is decoded eagerly, so an unusable key fails at construction.
    /// <para>使用十六进制字符串和可选描述初始化 OzipKeyEntry 的新实例。十六进制文本在构造时即完成解码，
    /// 因此不可用的密钥会在构造阶段失败。</para>
    /// </summary>
    /// <param name="keyHex">Hex key text; must have an even length and contain only <c>0</c>-<c>9</c>,
    /// <c>a</c>-<c>f</c> or <c>A</c>-<c>F</c>.
    /// <para>十六进制密钥文本；长度必须为偶数，且仅包含 <c>0</c>-<c>9</c>、<c>a</c>-<c>f</c> 或 <c>A</c>-<c>F</c>。</para></param>
    /// <param name="description">Optional device note recorded with the key.
    /// <para>随密钥记录的可选题型说明。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="keyHex"/> is <see langword="null"/>.
    /// <para><paramref name="keyHex"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="keyHex"/> has an odd length or contains a
    /// character outside the hexadecimal alphabet.
    /// <para><paramref name="keyHex"/> 长度为奇数，或包含十六进制字符集之外的字符。</para></exception>
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