namespace UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;

public sealed record QcKeyEntry(
    string KeyId,
    string Mc,
    string UserKey,
    string Ivec,
    string Description);

public sealed record MtkKeyEntry(
    string KeyId,
    string? Mc,
    string? EncAesKey,
    string? EncAesIv,
    string? DirectAesKey,
    string? DirectAesIv,
    string Description)
{
    public bool Is3Element => Mc is not null && EncAesKey is not null && EncAesIv is not null;

    public bool Is2Element => DirectAesKey is not null && DirectAesIv is not null;
}

public sealed record OpsKeyEntry(
    string KeyId,
    byte[] MBox,
    string Description);

public sealed record OzipKeyEntry
{
    public string KeyHex { get; init; }

    public byte[] KeyBytes { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// Initializes a new instance of the OzipKeyEntry with a hex string and optional description.
    /// <para>使用十六进制字符串和可选描述初始化 OzipKeyEntry 的新实例。</para>
    /// </summary>
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
