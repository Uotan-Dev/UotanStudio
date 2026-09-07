namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

public record NtpiVersionKey
{

    /// <summary>
    /// Initializes a new instance of NtpiVersionKey with the specified key and initialization vector.
    /// <para>使用指定的密钥和初始化向量初始化 NtpiVersionKey 的新实例。</para>
    /// </summary>
    public NtpiVersionKey(byte[] key, byte[] iv)
    {
        Key = (byte[])(key ?? throw new ArgumentNullException(nameof(key))).Clone();
        Iv = (byte[])(iv ?? throw new ArgumentNullException(nameof(iv))).Clone();
    }

    public byte[] Key => (byte[])field.Clone();

    public byte[] Iv => (byte[])field.Clone();
}

public static class NtpiVersionRegistry
{
    private static readonly Dictionary<(ulong Major, ulong Minor, ulong Patch), Dictionary<int, NtpiVersionKey>> VersionKeyMap = new()
    {
        {
            (1, 3, 0), new Dictionary<int, NtpiVersionKey>
            {
                { 1, new NtpiVersionKey(HexToBytes("08ed9260dec3807aac3ec00e765186cf4b9c677601ba844f8ec3e8c2fe1e11cb"), HexToBytes("0797205f6b02c0232cd2798795ba588d")) },
                { 2, new NtpiVersionKey(HexToBytes("7cec0ee7e63a703197afa8e09ce40f9b10a5fded6e5f04cb4ba7a435ed600288"), HexToBytes("01c5aaae7c4001592ea6a2310364a9a1")) },
                { 3, new NtpiVersionKey(HexToBytes("76fa1a8d6663aae8b964470c384508f7f974d21af2535cd3549c7c51ed68b0e6"), HexToBytes("de930fcc2c37009400e21dfa9f7d1363")) },
                { 4, new NtpiVersionKey(HexToBytes("1c37c2a0b579512481e8529532909c7c1be72f9bb5e1a4610328a5e2b67c10f4"), HexToBytes("ab15d90ce88a83680a4074d5bb96d94c")) },
                { 5, new NtpiVersionKey(HexToBytes("4ae22e3ae6ff0b65d06fa18df4f99ae59e6a90cb92ca03de65b64fc0fac958ce"), HexToBytes("eaaa17604ad7dae5773639c217978da5")) },
            }
        }
    };

    private static readonly Dictionary<(ulong Major, ulong Minor), Dictionary<int, NtpiVersionKey>> MajorMinorIndex = BuildMajorMinorIndex();

    private static Dictionary<(ulong Major, ulong Minor), Dictionary<int, NtpiVersionKey>> BuildMajorMinorIndex()
    {
        var index = new Dictionary<(ulong Major, ulong Minor), Dictionary<int, NtpiVersionKey>>();
        foreach (var entry in VersionKeyMap)
        {
            index.TryAdd((entry.Key.Major, entry.Key.Minor), entry.Value);
        }
        return index;
    }

    /// <summary>
    /// Retrieves the encryption key dictionary for the specified version.
    /// <para>根据版本号获取对应的加密密钥字典。</para>
    /// </summary>
    public static Dictionary<int, NtpiVersionKey>? GetKeysForVersion(ulong major, ulong minor, ulong patch)
    {
        return VersionKeyMap.TryGetValue((major, minor, patch), out var exact)
            ? exact
            : MajorMinorIndex.TryGetValue((major, minor), out var partial) ? partial : null;
    }

    /// <summary>
    /// Registers or overrides the key dictionary for the specified version.
    /// <para>注册或覆盖指定版本的密钥字典。</para>
    /// </summary>
    public static void RegisterVersion(ulong major, ulong minor, ulong patch, Dictionary<int, NtpiVersionKey> keys)
    {
        VersionKeyMap[(major, minor, patch)] = keys;
        MajorMinorIndex[(major, minor)] = keys;
    }

    private static byte[] HexToBytes(string hex)
    {
        var result = new byte[hex.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return result;
    }
}
