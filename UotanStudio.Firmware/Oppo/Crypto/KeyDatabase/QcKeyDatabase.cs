namespace UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;

public static class QcKeyDatabase
{
    private static readonly QcKeyEntry[] KeyEntries =
    [
        new QcKeyEntry(
            KeyId: "V1.4.17",
            Mc: "27827963787265EF89D126B69A495A21",
            UserKey: "82C50203285A2CE7D8C3E198383CE94C",
            Ivec: "422DD5399181E223813CD8ECDF2E4D72",
            Description: "R9s/A57t"),

        new QcKeyEntry(
            KeyId: "V1.6.17",
            Mc: "E11AA7BB558A436A8375FD15DDD4651F",
            UserKey: "77DDF6A0696841F6B74782C097835169",
            Ivec: "A739742384A44E8BA45207AD5C3700EA",
            Description: "a3s"),

        new QcKeyEntry(
            KeyId: "V1.5.13",
            Mc: "67657963787565E837D226B69A495D21",
            UserKey: "F6C50203515A2CE7D8C3E1F938B7E94C",
            Ivec: "42F2D5399137E2B2813CD8ECDF2F4D72",
            Description: "Legacy"),

        new QcKeyEntry(
            KeyId: "V1.6.6",
            Mc: "3C2D518D9BF2E4279DC758CD535147C3",
            UserKey: "87C74A29709AC1BF2382276C4E8DF232",
            Ivec: "598D92E967265E9BCABE2469FE4A915E",
            Description: "R15 Pro/FindX/R17/Reno"),

        new QcKeyEntry(
            KeyId: "V1.7.2",
            Mc: "8FB8FB261930260BE945B841AEFA9FD4",
            UserKey: "E529E82B28F5A2F8831D860AE39E425D",
            Ivec: "8A09DA60ED36F125D64709973372C1CF",
            Description: "Realme X/5/5 Pro"),

        new QcKeyEntry(
            KeyId: "V2.0.3",
            Mc: "E8AE288C0192C54BF10C5707E9C4705B",
            UserKey: "D64FC385DCD52A3C9B5FBA8650F92EDA",
            Ivec: "79051FD8D8B6297E2E4559E997F63B7F",
            Description: "OW19W8AP"),
    ];

    public static IReadOnlyList<QcKeyEntry> Entries => KeyEntries;

    /// <summary>
    /// Finds a QC key entry by its key identifier.
    /// <para>根据密钥标识符查找 QC 密钥条目。</para>
    /// </summary>
    public static QcKeyEntry? FindByKeyId(string keyId)
    {
        foreach (var entry in KeyEntries)
        {
            if (string.Equals(entry.KeyId, keyId, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}
