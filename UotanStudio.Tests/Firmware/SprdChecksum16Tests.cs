using System.Buffers.Binary;
using UotanStudio.Hash.Sums;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Golden vectors and device-model round trips of the UNISOC FDL ones-complement word checksum, cross-checked
/// against a bit-exact port of the vendor C reference in this class.
/// <para>紫光展锐 FDL 一补数求和校验的黄金向量与设备端模型往返，与本类内逐位照搬的厂商 C 参考实现互相校验。</para>
/// </summary>
public class SprdChecksum16Tests
{
    private static readonly byte[] LongPayload = [.. Enumerable.Range(0, 4096).Select(index => (byte)(index * 29 + 5))];

    [Fact]
    public void Compute_BlackBoxCheckValues_MatchFdLSemantics()
    {
        Assert.Equal((ushort)0x2AF6, SprdChecksum16.Compute("123456789"u8));
        Assert.Equal((ushort)0x2B2F, SprdChecksum16.Compute("12345678"u8));
        Assert.Equal((ushort)0xFFFF, SprdChecksum16.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ComputeWireForms_FollowDirectionalSwapRules()
    {
        Assert.Equal((ushort)0x2AF6, SprdChecksum16.ComputeTransmit("123456789"u8));
        Assert.Equal((ushort)0x2F2B, SprdChecksum16.ComputeTransmit("12345678"u8));
        Assert.Equal((ushort)0xF62A, SprdChecksum16.ComputeReceive("123456789"u8));
        Assert.Equal((ushort)0x2F2B, SprdChecksum16.ComputeReceive("12345678"u8));
        Assert.Equal((ushort)0xFFFF, SprdChecksum16.ComputeTransmit(ReadOnlySpan<byte>.Empty));
        Assert.Equal((ushort)0xFFFF, SprdChecksum16.ComputeReceive(ReadOnlySpan<byte>.Empty));
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

        Assert.Equal(ReferenceChecksum(data), SprdChecksum16.Compute(data));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(17)]
    [InlineData(40)]
    [InlineData(64)]
    public void TransmitFrame_SatisfiesDeviceZeroSumVerification(int length)
    {
        var region = LongPayload.AsSpan(0, length);
        var checksum = SprdChecksum16.ComputeTransmit(region);
        var frame = new byte[region.Length + 2];
        region.CopyTo(frame);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(region.Length), checksum);

        Assert.Equal((ushort)0x0000, DeviceFastChecksum(frame));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(35)]
    [InlineData(64)]
    public void ReceiveFrame_MatchesDeviceSwappedWireForm(int length)
    {
        var region = LongPayload.AsSpan(0, length);
        var deviceValue = ReferenceChecksum(region);
        var wire = new byte[region.Length + 2];
        region.CopyTo(wire);
        BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(region.Length), Reverse(deviceValue));
        var received = BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(region.Length));

        Assert.Equal(received, SprdChecksum16.ComputeReceive(region));
    }

    private static ushort Reverse(ushort value) => (ushort)((value << 8) | (value >> 8));

    private static ushort ReferenceChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var offset = 0;
        for (; offset + 2 <= data.Length; offset += 2)
        {
            sum += (uint)(data[offset] | (data[offset + 1] << 8));
        }

        if (offset < data.Length)
        {
            sum += data[offset];
        }

        sum = (sum >> 16) + (sum & 0xFFFF);
        sum += sum >> 16;
        return (ushort)~sum;
    }

    private static ushort DeviceFastChecksum(ReadOnlySpan<byte> frame)
    {
        uint sum = 0;
        var offset = 0;
        while (offset + 4 <= frame.Length)
        {
            sum += BinaryPrimitives.ReadUInt16LittleEndian(frame[offset..]);
            sum += BinaryPrimitives.ReadUInt16LittleEndian(frame[(offset + 2)..]);
            offset += 4;
        }

        if (((frame.Length - offset) & 2) != 0 && offset + 2 <= frame.Length)
        {
            sum += BinaryPrimitives.ReadUInt16LittleEndian(frame[offset..]);
            offset += 2;
        }

        if (offset < frame.Length)
        {
            sum += frame[offset];
        }

        sum = (sum >> 16) + (sum & 0xFFFF);
        sum += sum >> 16;
        return (ushort)~sum;
    }
}