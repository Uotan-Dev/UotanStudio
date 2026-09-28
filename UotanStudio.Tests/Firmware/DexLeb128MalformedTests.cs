using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.IO;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Malformed-input and boundary conformance tests for the 32-bit LEB128 decoders.
/// <para>32 位 LEB128 解码器的畸形输入与边界一致性测试。</para>
/// </summary>
public class DexLeb128MalformedTests
{

    [Fact]
    public void Uleb128_SixContinuationBytes_IsRejected()
    {
        var data = new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 };
        var offset = 0;
        Assert.Throws<FormatException>(() => Leb128.ReadUleb128(data, ref offset));
        Assert.Equal(0, offset);
    }

    [Fact]
    public void Sleb128_SixContinuationBytes_IsRejected()
    {
        var data = new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 };
        var offset = 0;
        Assert.Throws<FormatException>(() => Leb128.ReadSleb128(data, ref offset));
        Assert.Equal(0, offset);
    }

    [Fact]
    public void Uleb128_FiveContinuationsNoTerminator_IsRejected()
    {
        var data = new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80 };
        var offset = 0;
        Assert.Throws<FormatException>(() => Leb128.ReadUleb128(data, ref offset));
        Assert.Equal(0, offset);
    }

    [Fact]
    public void Sleb128_FiveContinuationsNoTerminator_IsRejected()
    {
        var data = new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80 };
        var offset = 0;
        Assert.Throws<FormatException>(() => Leb128.ReadSleb128(data, ref offset));
        Assert.Equal(0, offset);
    }

    [Fact]
    public void Uleb128_TruncatedAfterOneByte_IsRejected()
    {
        var data = new byte[] { 0x80 };
        var offset = 0;
        Assert.Throws<FormatException>(() => Leb128.ReadUleb128(data, ref offset));
        Assert.Equal(0, offset);
    }

    [Fact]
    public void Sleb128_TruncatedAfterOneByte_IsRejected()
    {
        var data = new byte[] { 0x80 };
        var offset = 0;
        Assert.Throws<FormatException>(() => Leb128.ReadSleb128(data, ref offset));
        Assert.Equal(0, offset);
    }

    [Fact]
    public void Uleb128_TruncatedMidway_IsRejected()
    {
        var data = new byte[] { 0x80, 0x80 };
        var offset = 0;
        Assert.Throws<FormatException>(() => Leb128.ReadUleb128(data, ref offset));
        Assert.Equal(0, offset);
    }

    [Fact]
    public void Uleb128_EmptyInput_IsRejected()
    {
        var data = Array.Empty<byte>();
        var offset = 0;
        Assert.Throws<FormatException>(() => Leb128.ReadUleb128(data, ref offset));
        Assert.Equal(0, offset);
    }

    [Fact]
    public void Sleb128_EmptyInput_IsRejected()
    {
        var data = Array.Empty<byte>();
        var offset = 0;
        Assert.Throws<FormatException>(() => Leb128.ReadSleb128(data, ref offset));
        Assert.Equal(0, offset);
    }

    [Fact]
    public void Uleb128_OffsetAtEnd_IsRejected()
    {
        var data = new byte[] { 0x00 };
        var offset = 1;
        Assert.Throws<FormatException>(() => Leb128.ReadUleb128(data, ref offset));
        Assert.Equal(1, offset);
    }

    [Fact]
    public void Uleb128_NegativeOffset_IsRejected()
    {
        var data = new byte[] { 0x00 };
        var offset = -1;
        Assert.Throws<ArgumentOutOfRangeException>(() => Leb128.ReadUleb128(data, ref offset));
    }

    [Fact]
    public void Sleb128_NegativeOffset_IsRejected()
    {
        var data = new byte[] { 0x00 };
        var offset = -1;
        Assert.Throws<ArgumentOutOfRangeException>(() => Leb128.ReadSleb128(data, ref offset));
    }

    [Fact]
    public void Uleb128_OffsetBeyondLength_IsRejected()
    {
        var data = new byte[] { 0x00, 0x01 };
        var offset = 5;
        Assert.Throws<FormatException>(() => Leb128.ReadUleb128(data, ref offset));
        Assert.Equal(5, offset);
    }

    [Fact]
    public void Uleb128_Zero_Succeeds()
    {
        var data = new byte[] { 0x00 };
        var offset = 0;
        Assert.Equal(0u, Leb128.ReadUleb128(data, ref offset));
        Assert.Equal(1, offset);
    }

    [Fact]
    public void Uleb128_MaxValue_Succeeds()
    {
        var data = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F };
        var offset = 0;
        Assert.Equal(0xFFFFFFFFu, Leb128.ReadUleb128(data, ref offset));
        Assert.Equal(5, offset);
    }

    [Fact]
    public void Sleb128_MinValue_Succeeds()
    {
        var data = new byte[] { 0x80, 0x80, 0x80, 0x80, 0x78 };
        var offset = 0;
        Assert.Equal(int.MinValue, Leb128.ReadSleb128(data, ref offset));
        Assert.Equal(5, offset);
    }

    [Fact]
    public void Sleb128_MaxValue_Succeeds()
    {
        var data = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x07 };
        var offset = 0;
        Assert.Equal(int.MaxValue, Leb128.ReadSleb128(data, ref offset));
        Assert.Equal(5, offset);
    }

    [Fact]
    public void Sleb128_NegativeOne_Succeeds()
    {
        var data = new byte[] { 0x7F };
        var offset = 0;
        Assert.Equal(-1, Leb128.ReadSleb128(data, ref offset));
        Assert.Equal(1, offset);
    }

    [Fact]
    public void Uleb128_ConsecutiveValues_DoNotOverConsume()
    {
        var data = new byte[] { 0x80, 0x01, 0x7F };
        var offset = 0;
        Assert.Equal(128u, Leb128.ReadUleb128(data, ref offset));
        Assert.Equal(2, offset);
        Assert.Equal(127u, Leb128.ReadUleb128(data, ref offset));
        Assert.Equal(3, offset);
    }

    [Fact]
    public void Sleb128_ConsecutiveValues_DoNotOverConsume()
    {
        var data = new byte[] { 0x7F, 0x00 };
        var offset = 0;
        Assert.Equal(-1, Leb128.ReadSleb128(data, ref offset));
        Assert.Equal(1, offset);
        Assert.Equal(0, Leb128.ReadSleb128(data, ref offset));
        Assert.Equal(2, offset);
    }
}