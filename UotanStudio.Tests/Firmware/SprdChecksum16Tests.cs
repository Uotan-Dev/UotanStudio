using System.Buffers.Binary;
using UotanStudio.Hash.Sums;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Golden vectors and device-model round trips of the UNISOC FDL ones-complement word checksum, cross-checked
/// against a bit-exact port of the vendor transport routine in this class. The captured bodies come from a vendor
/// tool wire log, so they pin the endianness of the summed words instead of only the algebra.
/// <para>紫光展锐 FDL 一补数求和校验的黄金向量与设备端模型往返，与本类内逐位照搬的厂商传输层例程互相校验。抓包
/// 报文取自厂商工具的线上日志，因此能真正钉住被求和字的字节序，而不只是代数关系。</para>
/// </summary>
public class SprdChecksum16Tests
{
    private static readonly byte[] LongPayload = [.. Enumerable.Range(0, 4096).Select(index => (byte)(index * 29 + 5))];

    [Fact]
    public void Compute_CheckValuesOfEveryLength_UseTheVendorWordOrder()
    {
        Assert.Equal((ushort)0xFFFF, SprdChecksum16.Compute(ReadOnlySpan<byte>.Empty));
        Assert.Equal((ushort)0x2EF2, SprdChecksum16.Compute("123456789"u8));
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
    public void Compute_CapturedVendorFrameBodies_ProduceTheWireValue(string body, ushort expected)
    {
        Assert.Equal(expected, SprdChecksum16.Compute(Convert.FromHexString(body)));
    }

    [Theory]
    [InlineData("80", 0xFF7F)]
    [InlineData("400040", 0xBFBF)]
    [InlineData("0040780000", 0x87BF)]
    [InlineData("00408000000000", 0x7FBF)]
    [InlineData("380000004000000000", 0x87FF)]
    [InlineData("0040000040000000000000400000000000", 0xBF7F)]
    public void Compute_VendorRoutineOutputs_ReproduceAtOddAndEvenLengths(string body, ushort expected)
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
    public void Compute_MatchesVendorReferenceOverEveryPrefixLength(int length)
    {
        var data = LongPayload.AsSpan(0, length);

        Assert.Equal(VendorReferenceChecksum(data), SprdChecksum16.Compute(data));
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
    public void TransmittedField_MatchesTheDeviceRecomputationOverTheRegion(int length)
    {
        var region = LongPayload.AsSpan(0, length);
        var frame = new byte[region.Length + 2];
        region.CopyTo(frame);
        var checksum = SprdChecksum16.Compute(region);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(region.Length), checksum);

        Assert.Equal(DeviceRegionComplement(region), BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(region.Length)));
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

    private static ushort VendorReferenceChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var offset = 0;
        for (; offset + 2 <= data.Length; offset += 2)
        {
            sum += (uint)((data[offset] << 8) | data[offset + 1]);
        }

        if (offset < data.Length)
        {
            sum += data[offset];
        }

        sum = (sum >> 16) + (sum & 0xFFFF);
        sum += sum >> 16;
        return (ushort)~sum;
    }

    private static ushort DeviceRegionComplement(ReadOnlySpan<byte> region)
    {
        uint sum = 0;
        for (var offset = 0; offset + 1 < region.Length; offset += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(region[offset..]);
        }

        if ((region.Length & 1) != 0)
        {
            sum += region[^1];
        }

        while (sum > ushort.MaxValue)
        {
            sum = (sum & ushort.MaxValue) + (sum >> 16);
        }

        return (ushort)~sum;
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