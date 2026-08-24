namespace UotanStudio.Firmware.OnePlusOps;

/// <summary>
/// Provides a static table of all known OPS decryption keys used by OnePlus firmware files.
/// Keys are sourced from the OnePlus OPS decryption reference implementation.
/// </summary>
/// <para>提供OnePlus固件文件使用的所有已知OPS解密密钥的静态表。
/// 密钥来源于OnePlus OPS解密参考实现。</para>
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
    /// Gets the number of available decryption keys in the table
    /// </summary>
    /// <para>获取表中可用解密密钥的数量</para>
    public static int KeyCount => Keys.Length;

    /// <summary>
    /// Returns all known OPS decryption keys as a read-only list
    /// </summary>
    /// <para>以只读列表形式返回所有已知的OPS解密密钥</para>
    /// <returns>A read-only list containing all known decryption key byte arrays</returns>
    public static IReadOnlyList<byte[]> GetAllKeys() => Array.AsReadOnly(Keys);

    /// <summary>
    /// Returns the decryption key at the specified index
    /// </summary>
    /// <para>返回指定索引处的解密密钥</para>
    /// <param name="index">The zero-based index of the key to retrieve</param>
    /// <returns>A byte array containing the decryption key</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when index is less than 0 or greater than or equal to KeyCount</exception>
    public static byte[] GetKey(int index)
    {
        return index < 0 || index >= Keys.Length
            ? throw new ArgumentOutOfRangeException(nameof(index), $"Key index must be between 0 and {Keys.Length - 1}.")
            : Keys[index];
    }

    /// <summary>
    /// Gets the descriptive name of the key at the specified index
    /// </summary>
    /// <para>获取指定索引处密钥的描述名称</para>
    /// <param name="index">The index of the key</param>
    /// <returns>A descriptive name for the key</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when index is less than 0 or greater than or equal to KeyCount</exception>
    public static string GetKeyName(int index)
    {
        return index < 0 || index >= KeyNames.Length
            ? throw new ArgumentOutOfRangeException(nameof(index), $"Key index must be between 0 and {KeyNames.Length - 1}.")
            : KeyNames[index];
    }
}