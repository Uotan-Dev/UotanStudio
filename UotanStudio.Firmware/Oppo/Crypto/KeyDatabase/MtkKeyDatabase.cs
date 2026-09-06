namespace UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;

public static class MtkKeyDatabase
{
    public static readonly byte[] MtkHeaderShuffleKey = [0x67, 0x65, 0x79, 0x69, 0x78, 0x75, 0x65];

    private static readonly MtkKeyEntry[] KeyEntries =
    [
        new MtkKeyEntry(
            KeyId: "MTK0",
            Mc: "67657963787565E837D226B69A495D21",
            EncAesKey: "F6C50203515A2CE7D8C3E1F938B7E94C",
            EncAesIv: "42F2D5399137E2B2813CD8ECDF2F4D72",
            DirectAesKey: null,
            DirectAesIv: null,
            Description: "A77/F1S FW"),

        new MtkKeyEntry(
            KeyId: "MTK1",
            Mc: "9E4F32639D21357D37D226B69A495D21",
            EncAesKey: "A3D8D358E42F5A9E931DD3917D9A3218",
            EncAesIv: "386935399137416B67416BECF22F519A",
            DirectAesKey: null,
            DirectAesIv: null,
            Description: "A77/F1S CDT"),

        new MtkKeyEntry(
            KeyId: "MTK2",
            Mc: "892D57E92A4D8A975E3C216B7C9DE189",
            EncAesKey: "D26DF2D9913785B145D18C7219B89F26",
            EncAesIv: "516989E4A1BFC78B365C6BC57D944391",
            DirectAesKey: null,
            DirectAesIv: null,
            Description: ""),

        new MtkKeyEntry(
            KeyId: "MTK3",
            Mc: "27827963787265EF89D126B69A495A21",
            EncAesKey: "82C50203285A2CE7D8C3E198383CE94C",
            EncAesIv: "422DD5399181E223813CD8ECDF2E4D72",
            DirectAesKey: null,
            DirectAesIv: null,
            Description: ""),

        new MtkKeyEntry(
            KeyId: "MTK4",
            Mc: "3C4A618D9BF2E4279DC758CD535147C3",
            EncAesKey: "87C74A29709AC1BF2382276C4E8DF232",
            EncAesIv: "598D92E967265E9BCABE2469FE4A915E",
            DirectAesKey: null,
            DirectAesIv: null,
            Description: ""),

        new MtkKeyEntry(
            KeyId: "MTK5",
            Mc: "1C3288822BF824259DC852C1733127D3",
            EncAesKey: "E7918D22799181CF2312176C9E2DF298",
            EncAesIv: "3247F889A7B6DECBCA3E28693E4AAAFE",
            DirectAesKey: null,
            DirectAesIv: null,
            Description: "A83/Realme 3"),

        new MtkKeyEntry(
            KeyId: "MTK6",
            Mc: "1E4F32239D65A57D37D2266D9A775D43",
            EncAesKey: "A332D3C3E42F5A3E931DD991729A321D",
            EncAesIv: "3F2A35399A373377674155ECF28FD19A",
            DirectAesKey: null,
            DirectAesIv: null,
            Description: ""),

        new MtkKeyEntry(
            KeyId: "MTK7",
            Mc: "122D57E92A518AFF5E3C786B7C34E189",
            EncAesKey: "DD6DF2D9543785674522717219989FB0",
            EncAesIv: "12698965A132C76136CC88C5DD94EE91",
            DirectAesKey: null,
            DirectAesIv: null,
            Description: ""),

        new MtkKeyEntry(
            KeyId: "MTK8",
            Mc: null,
            EncAesKey: null,
            EncAesIv: null,
            DirectAesKey: "ab3f76d7989207f2",
            DirectAesIv: "2bf515b3a9737835",
            Description: "Direct AES Key/IV"),
    ];

    public static IReadOnlyList<MtkKeyEntry> Entries => KeyEntries;

    /// <summary>
    /// Finds an MTK key entry by its key identifier.
    /// <para>根据密钥标识符查找 MTK 密钥条目。</para>
    /// </summary>
    public static MtkKeyEntry? FindByKeyId(string keyId)
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
