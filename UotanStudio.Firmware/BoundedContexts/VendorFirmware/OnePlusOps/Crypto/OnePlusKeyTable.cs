namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps.Crypto;

/// <summary>
/// Table of known OnePlus OPS decryption artefacts.  Not every entry is an AES
/// key: index 0 is a 16-byte AES key, while indices 1–3 are 62-byte MBox data
/// blobs used by a different (non-AES) decryption path.  Callers that need an
/// AES key should use <see cref="GetAesKey"/> / <see cref="AesKeyCount"/>; callers
/// that need the raw MBox data should use <see cref="GetMBoxData"/> /
/// <see cref="MBoxDataCount"/>.  The legacy <see cref="GetKey"/> /
/// <see cref="GetAllKeys"/> accessors return all entries unchanged for
/// compatibility with the SBox-based stream cipher path.
/// <para>已知 OnePlus OPS 解密 artefact 表。并非每条都是 AES 密钥：索引 0 为 16 字节
/// AES 密钥，索引 1–3 为 62 字节 MBox 数据（由另一非 AES 解密路径使用）。需要 AES 密钥
/// 的调用方应使用 <see cref="GetAesKey"/> / <see cref="AesKeyCount"/>；需要原始 MBox
/// 数据的调用方应使用 <see cref="GetMBoxData"/> / <see cref="MBoxDataCount"/>。</para>
/// </summary>
public static class OnePlusKeyTable
{
    private static readonly byte[][] Keys =
    [
        [
            0xD1, 0xB5, 0xE3, 0x9E, 0x5E, 0xEA, 0x04, 0x96,
            0x67, 0x1D, 0xD5, 0xAB, 0xD2, 0xAF, 0xCB, 0xAF
        ],
        [
            0x60, 0x8A, 0x3F, 0x2D, 0x68, 0x6B, 0xD4, 0x23,
            0x51, 0x0C, 0xD0, 0x95, 0xBB, 0x40, 0xE9, 0x76,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x0A, 0x00
        ],
        [
            0xAA, 0x69, 0x82, 0x9E, 0x5D, 0xDE, 0xB1, 0x3D,
            0x30, 0xBB, 0x81, 0xA3, 0x46, 0x65, 0xA3, 0xE1,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x0A, 0x00
        ],
        [
            0xC4, 0x5D, 0x05, 0x71, 0x99, 0xDD, 0xBB, 0xEE,
            0x29, 0xA1, 0x6D, 0xC7, 0xAD, 0xBF, 0xA4, 0x3F,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x0A, 0x00
        ]
    ];

    private static readonly string[] KeyNames =
    [
        "MainKey",
        "MBox5 (guacamoles_31_O.09_190820)",
        "MBox6 (instantnoodlev_15_O.07_201103)",
        "MBox4 (guacamolet_21_O.08_190502)"
    ];

    /// <summary>
    /// Index of the single AES key entry (16 bytes, suitable for AES-ECB).
    /// <para>唯一 AES 密钥条目（16 字节，可用于 AES-ECB）的索引。</para>
    /// </summary>
    public const int AesKeyIndex = 0;

    /// <summary>
    /// Number of AES-suitable key entries in the table (currently 1: index 0).
    /// <para>表中适合 AES 的密钥条目数（当前为 1：索引 0）。</para>
    /// </summary>
    public static int AesKeyCount => 1;

    /// <summary>
    /// Starting index of the MBox data entries (indices 1–3).
    /// <para>MBox 数据条目的起始索引（索引 1–3）。</para>
    /// </summary>
    public const int MBoxDataStartIndex = 1;

    /// <summary>
    /// Number of MBox data entries in the table (currently 3: indices 1–3).
    /// <para>表中 MBox 数据条目数（当前为 3：索引 1–3）。</para>
    /// </summary>
    public static int MBoxDataCount => 3;

    public static int KeyCount => Keys.Length;

    /// <summary>
    /// Returns all known OPS decryption artefacts as a read-only list.
    /// NOTE: not all entries are AES keys — see the class summary.
    /// <para>以只读列表形式返回所有已知的 OPS 解密 artefact。
    /// 注意：并非每条都是 AES 密钥——见类摘要。</para>
    /// </summary>
    public static IReadOnlyList<byte[]> GetAllKeys() => Array.AsReadOnly(Keys);

    /// <summary>
    /// Returns the raw artefact at the specified index (AES key or MBox data).
    /// NOTE: callers that need an AES key should use <see cref="GetAesKey"/>
    /// instead, to avoid passing MBox data to AES.
    /// <para>返回指定索引处的原始 artefact（AES 密钥或 MBox 数据）。
    /// 注意：需要 AES 密钥的调用方应改用 <see cref="GetAesKey"/>，
    /// 避免将 MBox 数据传给 AES。</para>
    /// </summary>
    public static byte[] GetKey(int index)
    {
        return index < 0 || index >= Keys.Length
            ? throw new ArgumentOutOfRangeException(nameof(index), $"Key index must be between 0 and {Keys.Length - 1}.")
            : Keys[index];
    }

    /// <summary>
    /// Gets the descriptive name of the artefact at the specified index.
    /// <para>获取指定索引处 artefact 的描述名称。</para>
    /// </summary>
    public static string GetKeyName(int index)
    {
        return index < 0 || index >= KeyNames.Length
            ? throw new ArgumentOutOfRangeException(nameof(index), $"Key index must be between 0 and {KeyNames.Length - 1}.")
            : KeyNames[index];
    }

    /// <summary>
    /// Returns the AES key at the specified AES-key index (0 .. <see cref="AesKeyCount"/> - 1).
    /// <para>返回指定 AES 密钥索引（0 .. <see cref="AesKeyCount"/> - 1）处的 AES 密钥。</para>
    /// </summary>
    /// <exception cref="NotSupportedException">The index refers to an MBox data entry,
    /// not an AES key.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the table bounds.</exception>
    public static byte[] GetAesKey(int index)
    {
        if (index < 0 || index >= Keys.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"Key index must be between 0 and {Keys.Length - 1}.");
        }

        if (index != AesKeyIndex)
        {
            throw new NotSupportedException(
                $"Table entry {index} ('{KeyNames[index]}') is MBox data, not an AES key. " +
                "MBox data cannot be used with the AES-ECB interface.");
        }

        return Keys[index];
    }

    /// <summary>
    /// Returns the MBox data blob at the specified MBox-data index
    /// (0 .. <see cref="MBoxDataCount"/> - 1, relative to <see cref="MBoxDataStartIndex"/>).
    /// <para>返回指定 MBox 数据索引（0 .. <see cref="MBoxDataCount"/> - 1，
    /// 相对于 <see cref="MBoxDataStartIndex"/>）处的 MBox 数据 blob。</para>
    /// </summary>
    /// <exception cref="NotSupportedException">The index refers to an AES key entry,
    /// not MBox data.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the MBox data range.</exception>
    public static byte[] GetMBoxData(int index)
    {
        if (index < 0 || index >= MBoxDataCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"MBox data index must be between 0 and {MBoxDataCount - 1}.");
        }

        var absoluteIndex = MBoxDataStartIndex + index;
        return Keys[absoluteIndex];
    }
}