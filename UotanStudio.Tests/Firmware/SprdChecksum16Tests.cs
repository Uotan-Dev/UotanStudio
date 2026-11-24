using System.Buffers.Binary;
using UotanStudio.Hash.Sums;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Golden vectors and official-oracle cross-checks of the UNISOC FDL ones-complement checksum. The oracle is a
/// direct transcription of the flash tool's <c>CBMPkg::Assemble</c>/<c>frm_chk</c> path, including its high-byte
/// placement of an odd trailing byte and the final wire swap, so the assertions pin vendor semantics instead of
/// the implementation's own algebra.
/// <para>紫光展锐 FDL 一补数校验的黄金向量与官方 oracle 交叉校验。oracle 直接照搬刷机工具的
/// <c>CBMPkg::Assemble</c>/<c>frm_chk</c> 路径，包括奇数尾字节的高字节放置与最终线上交换，因此断言固定的是
/// 厂商语义，而不是实现自身的代数。</para>
/// </summary>
public class SprdChecksum16Tests
{
    private static readonly byte[] LongPayload = [.. Enumerable.Range(0, 4096).Select(index => (byte)(index * 29 + 5))];

    [Fact]
    public void Compute_CheckValuesOfEveryLength_UseTheVendorWordOrder()
    {
        Assert.Equal((ushort)0xFFFF, SprdChecksum16.Compute(ReadOnlySpan<byte>.Empty));
        Assert.Equal((ushort)0xF62A, SprdChecksum16.Compute("123456789"u8));
        Assert.Equal((ushort)0x2F2B, SprdChecksum16.Compute("12345678"u8));
    }

    [Theory]
    [InlineData("00000000", 0xFFFF)]
    [InlineData("00800000", 0xFF7F)]
    [InlineData("000900040001c200", 0x3DF1)]
    [InlineData("00ff00ff", 0xFE01)]
    [InlineData("0100ffff", 0xFEFF)]
    [InlineData("003800", 0xFFC7)]
    [InlineData("0056", 0xFFA9)]
    [InlineData("0081000141", 0xBE7D)]
    public void Compute_CapturedVendorFrameBodies_ProduceTheWireValue(string body, ushort expected)
    {
        Assert.Equal(expected, SprdChecksum16.Compute(Convert.FromHexString(body)));
    }

    [Theory]
    [InlineData("80", 0x7FFF)]
    [InlineData("400040", 0x7FFF)]
    [InlineData("0040780000", 0x87BF)]
    [InlineData("00408000000000", 0x7FBF)]
    [InlineData("380000004000000000", 0x87FF)]
    [InlineData("0040000040000000000000400000000000", 0xBF7F)]
    public void Compute_OfficialToolOddTailRule_ProducesTheWireValue(string body, ushort expected)
    {
        Assert.Equal(expected, SprdChecksum16.Compute(Convert.FromHexString(body)));
    }

    [Fact]
    public void Compute_CapturedVersionReply_ProducesTheWireValue()
    {
        var region = new byte[4 + 34];
        BinaryPrimitives.WriteUInt16BigEndian(region, 0x0081);
        BinaryPrimitives.WriteUInt16BigEndian(region.AsSpan(2), 34);
        "Spreadtrum Boot Block version 1.2\0"u8.CopyTo(region.AsSpan(4));

        Assert.Equal((ushort)0xA9E9, SprdChecksum16.Compute(region));
        Assert.Equal((ushort)0xA9E9, OfficialToolChecksum(region));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(1000)]
    [InlineData(4096)]
    public void Compute_MatchesOfficialToolOverEveryPrefixLength(int length)
    {
        var data = LongPayload.AsSpan(0, length);

        Assert.Equal(OfficialToolChecksum(data), SprdChecksum16.Compute(data));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(17)]
    [InlineData(35)]
    [InlineData(63)]
    [InlineData(255)]
    [InlineData(4096)]
    public void TransmittedField_MatchesTheOfficialRecomputationOverTheRegion(int length)
    {
        var region = LongPayload.AsSpan(0, length);
        var frame = new byte[region.Length + 2];
        region.CopyTo(frame);
        var checksum = SprdChecksum16.Compute(region);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(region.Length), checksum);

        Assert.Equal(OfficialToolChecksum(region), BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(region.Length)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(4096)]
    public void TransmittedFrame_EvenRegion_FoldsToTheZeroComplement(int length)
    {
        var region = LongPayload.AsSpan(0, length);
        var frame = new byte[region.Length + 2];
        region.CopyTo(frame);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(region.Length), SprdChecksum16.Compute(region));

        Assert.Equal((ushort)0xFFFF, DeviceFrameAccumulator(frame));
    }

    private static ushort OfficialToolChecksum(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        var offset = 0;
        for (; data.Length - offset > 1; offset += 2)
        {
            crc += (uint)((data[offset] << 8) | data[offset + 1]);
        }

        if (offset < data.Length)
        {
            crc += (uint)(data[offset] << 8);
        }

        crc = (crc >> 16) + (crc & 0xFFFF);
        crc += crc >> 16;
        return (ushort)~crc;
    }

    private static ushort DeviceFrameAccumulator(ReadOnlySpan<byte> frame)
    {
        uint sum = 0;
        for (var offset = 0; offset + 1 < frame.Length; offset += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(frame[offset..]);
        }

        if ((frame.Length & 1) != 0)
        {
            sum += frame[^1];
        }

        while (sum > ushort.MaxValue)
        {
            sum = (sum & ushort.MaxValue) + (sum >> 16);
        }

        return (ushort)sum;
    }
}