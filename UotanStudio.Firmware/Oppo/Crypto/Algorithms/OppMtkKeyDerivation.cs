using System.Text;
using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;

namespace UotanStudio.Firmware.Oppo.Crypto.Algorithms;

internal static class OppMtkKeyDerivation
{
    /// <summary>
    /// Derives AES key and IV using the 3-element key configuration (Mc + EncAesKey + EncAesIv).
    /// <para>使用三元素密钥配置（Mc + EncAesKey + EncAesIv）派生 AES 密钥和 IV。</para>
    /// </summary>
    internal static (byte[] AesKey, byte[] AesIv) DeriveKey3(MtkKeyEntry entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        if (!entry.Is3Element)
        {
            throw new InvalidOperationException(
                $"MTK key entry '{entry.KeyId}' is not a 3-element configuration.");
        }


        var mcBytes = CryptoHelper.HexDecode(entry.Mc!);
        var encAesKeyBytes = CryptoHelper.HexDecode(entry.EncAesKey!);
        MtkShuffle2(mcBytes, encAesKeyBytes);
        var aesKey = CryptoHelper.Md5HexFirst16(encAesKeyBytes);


        var mcBytes2 = CryptoHelper.HexDecode(entry.Mc!);
        var encAesIvBytes = CryptoHelper.HexDecode(entry.EncAesIv!);
        MtkShuffle2(mcBytes2, encAesIvBytes);
        var aesIv = CryptoHelper.Md5HexFirst16(encAesIvBytes);

        return (aesKey, aesIv);
    }

    /// <summary>
    /// Derives AES key and IV using the 2-element key configuration (DirectAesKey + DirectAesIv).
    /// <para>使用二元素密钥配置（DirectAesKey + DirectAesIv）派生 AES 密钥和 IV。</para>
    /// </summary>
    internal static (byte[] AesKey, byte[] AesIv) DeriveKey2(MtkKeyEntry entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        if (!entry.Is2Element)
        {
            throw new InvalidOperationException(
                $"MTK key entry '{entry.KeyId}' is not a 2-element configuration.");
        }


        var aesKey = Encoding.UTF8.GetBytes(entry.DirectAesKey!);
        var aesIv = Encoding.UTF8.GetBytes(entry.DirectAesIv!);

        return (aesKey, aesIv);
    }

    /// <summary>
    /// Performs MTK Shuffle on data: nibble swap (swap high and low 4 bits) on each byte, then XOR with key.
    /// <para>对数据执行 MTK Shuffle 操作：先对每个字节进行半字节交换（高低 4 位互换），再与密钥异或。</para>
    /// </summary>
    internal static void MtkShuffle(ReadOnlyMemory<byte> key, Span<byte> data)
    {
        var keySpan = key.Span;
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            var nibbleSwapped = (byte)(((b & 0xF0) >> 4) | ((b & 0x0F) << 4));
            data[i] = (byte)(nibbleSwapped ^ keySpan[i % keySpan.Length]);
        }
    }

    /// <summary>
    /// Performs MTK Shuffle2 on data: XOR each byte with key, then nibble swap (swap high and low 4 bits).
    /// <para>对数据执行 MTK Shuffle2 操作：先将每个字节与密钥异或，再进行半字节交换（高低 4 位互换）。</para>
    /// </summary>
    internal static void MtkShuffle2(ReadOnlyMemory<byte> key, Span<byte> data)
    {
        var keySpan = key.Span;
        for (var i = 0; i < data.Length; i++)
        {
            var x = (byte)(data[i] ^ keySpan[i % keySpan.Length]);
            data[i] = (byte)(((x & 0xF0) >> 4) | ((x & 0x0F) << 4));
        }
    }
}
