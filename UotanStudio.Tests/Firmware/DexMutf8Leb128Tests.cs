using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.IO;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// MUTF-8 and LEB128 codec tests: verify modified UTF-8 edge cases (U+0000, supplementary characters)
/// and the uleb128/sleb128 boundary values used by the DEX string and index tables.
/// <para>MUTF-8 与 LEB128 编解码测试：验证修改版 UTF-8 边界（U+0000、增补字符）以及 DEX 字符串与索引表
/// 使用的 uleb128/sleb128 边界值。</para>
/// </summary>
public class DexMutf8Leb128Tests
{
    [Fact]
    public void Mutf8_EncodesNulAsC080()
    {
        var bytes = Mutf8.Encode("\u0000");
        Assert.Equal(2, bytes.Length);
        Assert.Equal(0xC0, bytes[0]);
        Assert.Equal(0x80, bytes[1]);
        Assert.Equal("\u0000", Mutf8.Decode(bytes, bytes.Length));
    }

    [Fact]
    public void Mutf8_RoundTripsAsciiAndCjk()
    {
        const string value = "Hello, DEX 世界 🚀";
        var bytes = Mutf8.Encode(value);
        Assert.Equal(value, Mutf8.Decode(bytes, bytes.Length));
    }

    [Fact]
    public void Mutf8_SurrogatePairRoundTrips()
    {
        // 🚀 = U+1F680，在 .NET 中为代理对，MUTF-8 中按两个 3 字节序列编码
        // 🚀 = U+1F680; in .NET it is a surrogate pair, encoded as two 3-byte sequences in MUTF-8.
        const string rocket = "🚀";
        var bytes = Mutf8.Encode(rocket);
        Assert.Equal(6, bytes.Length);
        Assert.Equal(rocket, Mutf8.Decode(bytes, bytes.Length));
    }

    [Fact]
    public void Mutf8_EncodedLengthMatchesBytes()
    {
        Assert.Equal(Mutf8.Encode("a\u0000中🚀").Length, Mutf8.EncodedLength("a\u0000中🚀"));
    }

    [Fact]
    public void Uleb128_RoundTripsBoundaryValues()
    {
        uint[] values = [0, 1, 0x7F, 0x80, 0x3FFF, 0x4000, 0xFFFFFF, 0x1000000, 0x7FFFFFFF, 0xFFFFFFFF];
        foreach (var value in values)
        {
            var buffer = new byte[8];
            var end = Leb128.WriteUleb128(buffer, 0, value);
            var offset = 0;
            var decoded = Leb128.ReadUleb128(buffer, ref offset);
            Assert.Equal(value, decoded);
            Assert.Equal(end, offset);
        }
    }

    [Fact]
    public void Sleb128_RoundTripsBoundaryValues()
    {
        int[] values = [0, 1, -1, 63, 64, -64, -65, 0x1FFF, -0x2000, 0x3FFFFFFF, int.MinValue, int.MaxValue];
        foreach (var value in values)
        {
            var buffer = new byte[8];
            var end = Leb128.WriteSleb128(buffer, 0, value);
            var offset = 0;
            var decoded = Leb128.ReadSleb128(buffer, ref offset);
            Assert.Equal(value, decoded);
            Assert.Equal(end, offset);
        }
    }
}