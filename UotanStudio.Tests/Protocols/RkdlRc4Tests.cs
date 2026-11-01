using UotanStudio.Comm.Protocols.Bootrom;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Cross-checks the vendor RC4 stream against a transcription of <c>P_RC4</c> written in this class, so the
/// non-standard key schedule (a 16-byte key repeated by masking the index with <c>0x0f</c>) and the per-sector state
/// reset are exercised rather than a single hard-coded ciphertext.
/// <para>用本类中抄写的 <c>P_RC4</c> 交叉校验厂商 RC4 流，从而验证非标准密钥编排（用 <c>0x0f</c> 掩码索引使 16 字节密钥
/// 循环）与按扇区重置状态，而不是只钉一个硬编码密文。</para>
/// </summary>
public class RkdlRc4Tests
{
    private static readonly byte[] Key =
    [
        124, 78, 3, 4, 85, 5, 9, 7, 45, 44, 123, 56, 23, 13, 23, 17,
    ];

    [Fact]
    public void EncryptSectors_ThreeSectors_MatchesTheVendorReferenceImplementation()
    {
        var data = Pattern(512 * 3);
        var expected = (byte[])data.Clone();
        for (var sector = 0; sector < 3; sector++)
        {
            ReferenceRc4(expected.AsSpan(sector * 512, 512));
        }

        RkdlRc4.EncryptSectors(data);

        Assert.Equal(expected, data);
    }

    [Fact]
    public void EncryptSectors_TwoSectors_ResetsTheStreamAtEverySectorBoundary()
    {
        var combined = Pattern(512 * 2);
        var expected = (byte[])combined.Clone();
        RkdlRc4.Encrypt(expected.AsSpan(0, 512));
        RkdlRc4.Encrypt(expected.AsSpan(512, 512));

        RkdlRc4.EncryptSectors(combined);

        Assert.Equal(expected, combined);
    }

    [Fact]
    public void EncryptSectors_TrailingPartialSector_LeavesTheRemainderUntouched()
    {
        var data = Pattern(600);
        var trailing = data.AsSpan(512).ToArray();
        var expectedHead = data.AsSpan(0, 512).ToArray();
        ReferenceRc4(expectedHead);

        RkdlRc4.EncryptSectors(data);

        Assert.Equal(trailing, data[512..]);
        Assert.Equal(expectedHead, data[..512]);
    }

    [Fact]
    public void Encrypt_AppliedTwice_RestoresThePlaintext()
    {
        var plaintext = Pattern(512);
        var data = (byte[])plaintext.Clone();

        RkdlRc4.Encrypt(data);
        RkdlRc4.Encrypt(data);

        Assert.Equal(plaintext, data);
    }

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index++)
        {
            data[index] = (byte)((index * 31) + 7);
        }

        return data;
    }

    private static void ReferenceRc4(Span<byte> buffer)
    {
        Span<byte> state = stackalloc byte[256];
        Span<byte> schedule = stackalloc byte[256];

        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            state[i] = (byte)i;
            j &= 0x0f;
            schedule[i] = Key[j];
            j++;
        }

        j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + state[i] + schedule[i]) % 256;
            (state[i], state[j]) = (state[j], state[i]);
        }

        int x = 0, y = 0;
        for (var index = 0; index < buffer.Length; index++)
        {
            x = (x + 1) % 256;
            y = (y + state[x]) % 256;
            (state[x], state[y]) = (state[y], state[x]);
            var t = (state[x] + (state[y] % 256)) % 256;
            buffer[index] ^= state[t];
        }
    }
}