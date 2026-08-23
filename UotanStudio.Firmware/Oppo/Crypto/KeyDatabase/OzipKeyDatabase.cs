namespace UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;

/// <summary>
/// <para>提供 OZip 加密固件的内置 AES-128 密钥数据库。</para>
/// Provides the built-in AES-128 key database for OZip encrypted firmware.
/// </summary>
/// <remarks>
/// <para>
/// 此数据库包含从 Python 参考实现 (bkerler/oppo_ozip_decrypt) 提取的已知密钥，
/// 涵盖 Oppo、Realme、OnePlus 等品牌的多种设备型号。
/// </para>
/// This database contains known keys extracted from the Python reference implementation
/// (bkerler/oppo_ozip_decrypt), covering various device models from Oppo, Realme, and OnePlus.
/// </remarks>
public static class OzipKeyDatabase
{
    private static readonly List<OzipKeyEntry> Entries = BuildKeyDatabase();

    /// <summary>
    /// <para>获取所有已知 OZip 密钥条目。</para>
    /// Gets all known OZip key entries.
    /// </summary>
    /// <returns><para>所有密钥条目的只读列表。</para>A read-only list of all key entries.</returns>
    public static IReadOnlyList<OzipKeyEntry> GetAllKeys() => Entries;

    /// <summary>
    /// <para>根据索引获取密钥条目。</para>
    /// Gets a key entry by its index.
    /// </summary>
    /// <param name="index"><para>密钥条目的索引（从 0 开始）。</para>The zero-based index of the key entry.</param>
    /// <returns><para>指定索引处的密钥条目。</para>The key entry at the specified index.</returns>
    public static OzipKeyEntry FindByIndex(int index)
    {
        return index < 0 || index >= Entries.Count
            ? throw new ArgumentOutOfRangeException(nameof(index),
                $"Index must be between 0 and {Entries.Count - 1}.")
            : Entries[index];
    }

    /// <summary>
    /// <para>获取密钥数据库中的密钥总数。</para>
    /// Gets the total number of keys in the database.
    /// </summary>
    public static int Count => Entries.Count;

    private static List<OzipKeyEntry> BuildKeyDatabase()
    {
        return
        [
            new("D6EECF0AE5ACD4E0E9FE522DE7CE381E", "mnkey"),
            new("D6ECCF0AE5ACD4E0E92E522DE7C1381E", "mkey"),
            new("D6DCCF0AD5ACD4E0292E522DB7C1381E", "realkey, R9s CPH1607 MSM8953, Plus, R11, RMX1921 Realme XT"),
            new("D7DCCE1AD4AFDCE2393E5161CBDC4321", "testkey"),
            new("D7DBCE2AD4ADDCE1393E5521CBDC4321", "utilkey"),
            new("D7DBCE1AD4AFDCE1393E5121CBDC4321", "R11s CPH1719 MSM8976, Plus"),
            new("D4D2CD61D4AFDCE13B5E01221BD14D20", "FindX CPH1871 SDM845"),
            new("261CC7131D7C1481294E532DB752381E", "FindX"),
            new("1CA21E12271335AE33AB81B2A7B14622", "Realme 2 Pro SDM660/MSM8976"),
            new("D4D2CE11D4AFDCE13B3E0121CBD14D20", "K1 SDM660/MSM8976"),
            new("1C4C1EA3A12531AE491B21BB31613C11", "Realme 3 Pro SDM710, X, 5 Pro, Q, RMX1921 Realme XT"),
            new("1C4C1EA3A12531AE4A1B21BB31C13C21", "Reno 10x zoom PCCM00 SDM855, CPH1921EX Reno 5G"),
            new("1C4A11A3A12513AE441B23BB31513121", "Reno 2 PCKM00 SDM730G"),
            new("1C4A11A3A12589AE441A23BB31517733", "Realme X2 SDM730G"),
            new("1C4A11A3A22513AE541B53BB31513121", "Realme 5 SDM665"),
            new("2442CE821A4F352E33AE81B22BC1462E", "R17 Pro SDM710"),
            new("14C2CD6214CFDC2733AE81B22BC1462C", "CPH1803 Oppo A3s SDM450/MSM8953"),
            new("1E38C1B72D522E29E0D4ACD50ACFDCD6", "OZip key 18"),
            new("12341EAAC4C123CE193556A1BBCC232D", "OZip key 19"),
            new("2143DCCB21513E39E1DCAFD41ACEDBD7", "OZip key 20"),
            new("2D23CCBBA1563519CE23C1C4AA1E3412", "A77 CPH1715 MT6750T"),
            new("172B3E14E46F3CE13E2B5121CBDC4321", "Realme 1 MTK P60"),
            new("ACAA1E12A71431CE4A1B21BBA1C1C6A2", "Realme U1 RMX1831 MTK P70"),
            new("ACAC1E13A72531AE4A1B22BB31C1CC22", "Realme 3 RMX1825EX P70"),
            new("1C4411A3A12533AE441B21BB31613C11", "A1k CPH1923 MTK P22"),
            new("1C4416A8A42717AE441523B336513121", "Reno 3 PCRM00 MTK 1000L, CPH2059 OPPO A92, CPH2067 OPPO A72"),
            new("55EEAA33112133AE441B23BB31513121", "RenoAce SDM855Plus"),
            new("ACAC1E13A12531AE4A1B21BB31C13C21", "Reno, K3"),
            new("ACAC1E13A72431AE4A1B22BBA1C1C6A2", "A9"),
            new("12CAC11211AAC3AEA2658690122C1E81", "A1, A83t"),
            new("1CA21E12271435AE331B81BBA7C14612", "CPH1909 Oppo A5s MT6765"),
            new("D1DACF24351CE428A9CE32ED87323216", "Realme 1 (reserved)"),
            new("A1CC75115CAECB890E4A563CA1AC67C8", "A73 (reserved)"),
            new("2132321EA2CA86621A11241ABA512722", "Realme 3 (reserved)"),
            new("22A21E821743E5EE33AE81B227B1462E", "F3 Plus CPH1613 - MSM8976"),
        ];
    }
}