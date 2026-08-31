using UotanStudio.Comm.Transports.Hsuart;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports;

/// <summary>
/// HSUART packet layer tests: <see cref="HsuartPacket"/> encode/decode, <see cref="Crc16"/>
/// and <see cref="CobsCodec"/> (the StreamingProtocolTests already cover HsuartFrameParser).
/// <para>HSUART 数据包层测试：<see cref="HsuartPacket"/> 编解码、<see cref="Crc16"/> 与
/// <see cref="CobsCodec"/>（HsuartFrameParser 已由 StreamingProtocolTests 覆盖）。</para>
/// </summary>
public class HsuartPacketTests
{
    [Fact]
    public void HsuartPacket_ProtocolPacket_RoundTrips()
    {
        var payload = new byte[] { 0x00, 0x01, 0x02, 0xFE, 0xFF, 0x10 };
        var encoded = Encode(HsuartPacketId.ProtocolPacket, payload);

        var decoded = new byte[HsuartPacket.MaxPacketSize];
        var n = HsuartPacket.Decode(encoded, out var id, decoded, out var error);

        Assert.Equal(HsuartPacketError.Success, error);
        Assert.Equal(HsuartPacketId.ProtocolPacket, id);
        Assert.Equal(payload, decoded[..n]);
    }

    [Fact]
    public void HsuartPacket_ProtocolPacket_RequiresPayload()
    {
        var encoded = new byte[HsuartPacket.MaxPacketSize + 16];
        var n = HsuartPacket.Encode(HsuartPacketId.ProtocolPacket, [], encoded, out var error);
        Assert.Equal(0, n);
        Assert.Equal(HsuartPacketError.InvalidParameter, error);
    }

    [Fact]
    public void HsuartPacket_EndOfTransfer_EmptyPayload_RoundTrips()
    {
        var encoded = Encode(HsuartPacketId.EndOfTransferPacket, []);

        var decoded = new byte[HsuartPacket.MaxPacketSize];
        var n = HsuartPacket.Decode(encoded, out var id, decoded, out var error);

        Assert.Equal(HsuartPacketError.Success, error);
        Assert.Equal(HsuartPacketId.EndOfTransferPacket, id);
        Assert.Equal(0, n);
    }

    [Theory]
    [InlineData(HsuartPacketId.AckPacket)]
    [InlineData(HsuartPacketId.NakPacket)]
    public void HsuartPacket_AckNak_EncodesAsSingleRawByte(HsuartPacketId id)
    {
        var encoded = new byte[2];
        var n = HsuartPacket.Encode(id, [], encoded, out var error);

        Assert.Equal(HsuartPacketError.Success, error);
        Assert.Equal(1, n);
        Assert.Equal((byte)id, encoded[0]);
    }

    [Theory]
    [InlineData(HsuartPacketId.AckPacket)]
    [InlineData(HsuartPacketId.NakPacket)]
    public void HsuartPacket_AckNak_DecodesSingleRawByte(HsuartPacketId id)
    {
        var decoded = new byte[4];
        var n = HsuartPacket.Decode([(byte)id], out var packetId, decoded, out var error);

        Assert.Equal(HsuartPacketError.Success, error);
        Assert.Equal(id, packetId);
        Assert.Equal(0, n);
    }

    [Fact]
    public void HsuartPacket_VersionPacket_RoundTrips()
    {
        var encoded = Encode(HsuartPacketId.VersionPacket, []);

        var decoded = new byte[HsuartPacket.MaxPacketSize];
        var n = HsuartPacket.Decode(encoded, out var id, decoded, out var error);

        Assert.Equal(HsuartPacketError.Success, error);
        Assert.Equal(HsuartPacketId.VersionPacket, id);
        Assert.Equal(0, n); // control packet carries no payload
    }

    [Fact]
    public void HsuartPacket_ReadyToRead_RoundTrips()
    {
        var encoded = Encode(HsuartPacketId.ReadyToReadPacket, []);

        var decoded = new byte[HsuartPacket.MaxPacketSize];
        var n = HsuartPacket.Decode(encoded, out var id, decoded, out var error);

        Assert.Equal(HsuartPacketError.Success, error);
        Assert.Equal(HsuartPacketId.ReadyToReadPacket, id);
        Assert.Equal(0, n);
    }

    [Fact]
    public void HsuartPacket_CorruptedPayload_ReportsPacketCorruption()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var encoded = Encode(HsuartPacketId.ProtocolPacket, payload);
        encoded[encoded.Length - 2] ^= 0xFF; // corrupt the CRC low byte

        var decoded = new byte[HsuartPacket.MaxPacketSize];
        var n = HsuartPacket.Decode(encoded, out _, decoded, out var error);

        Assert.Equal(HsuartPacketError.PacketCorruption, error);
        Assert.Equal(0, n);
    }

    [Fact]
    public void HsuartPacket_EmptyInput_ReportsInvalidParameter()
    {
        var decoded = new byte[4];
        var n = HsuartPacket.Decode([], out _, decoded, out var error);

        Assert.Equal(HsuartPacketError.InvalidParameter, error);
        Assert.Equal(0, n);
    }

    [Fact]
    public void HsuartPacket_LargePayload_RoundTrips()
    {
        // The payload must survive a full COBS frame at the maximum packet size.
        // 负载需在最大包大小的完整 COBS 帧中往返无损。
        var payload = Enumerable.Range(0, HsuartPacket.MaxPacketSize - 64)
            .Select(i => (byte)((i * 7) & 0xFF))
            .ToArray();
        var encoded = Encode(HsuartPacketId.ProtocolPacket, payload);

        var decoded = new byte[HsuartPacket.MaxPacketSize];
        var n = HsuartPacket.Decode(encoded, out var id, decoded, out var error);

        Assert.Equal(HsuartPacketError.Success, error);
        Assert.Equal(HsuartPacketId.ProtocolPacket, id);
        Assert.Equal(payload, decoded[..n]);
    }

    // ============== Crc16 ==============

    [Fact]
    public void Crc16_KnownVector_MatchesReference()
    {
        // Standard CRC-16/CCITT reflected check value for "123456789" is 0xD64E.
        // "123456789" 的标准反向 CRC-16/CCITT 校验值为 0xD64E。
        Assert.Equal(0xD64E, Crc16.Calculate("123456789"u8));
    }

    [Fact]
    public void Crc16_FourZeroFFBytes_MatchesReferenceResidue()
    {
        Assert.Equal(Crc16.Crc16Ok, Crc16.Calculate([0xFF, 0xFF, 0xFF, 0xFF]));
    }

    [Fact]
    public void Crc16_Verify_MatchesCalculate()
    {
        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x11 };
        var crc = Crc16.Calculate(data);
        Assert.True(Crc16.Verify(data, crc));
        Assert.False(Crc16.Verify(data, (ushort)(crc ^ 0xFFFF)));
    }

    // ============== CobsCodec ==============

    [Fact]
    public void Cobs_StuffUnstuff_RoundTrips()
    {
        var payload = new byte[]
        {
            0x00, 0x00, 1, 2, 0x00, 3, 4, 5, 6, 7, 0x00,
            0x11, 0x00, 0x00, 0xFF, 0xFE
        };
        var stuffed = new byte[payload.Length + payload.Length / 254 + 8];
        var n = CobsCodec.StuffBytes(payload, stuffed, out var error);
        Assert.Equal(CobsError.Success, error);

        // The final delimiter is 0x00.
        // 末字节为 0x00 分隔符。
        Assert.Equal(0x00, stuffed[n - 1]);

        var unstuffed = new byte[payload.Length + 8];
        var m = CobsCodec.UnstuffBytes(stuffed[..n], unstuffed, out var unstuffError);
        Assert.Equal(CobsError.Success, unstuffError);
        Assert.Equal(payload, unstuffed[..m]);
    }

    [Fact]
    public void Cobs_Unstuff_InvalidStuffingFails()
    {
        var unstuffed = new byte[16];
        // code=0x05 claims a 4-byte block but only 2 bytes follow.
        // code=0x05 声称后面有 4 字节块，但实际仅 2 字节。
        var n = CobsCodec.UnstuffBytes([0x05, 0xAA, 0xBB], unstuffed, out var error);
        Assert.Equal(CobsError.InvalidStuffing, error);
        Assert.Equal(0, n);
    }

    [Fact]
    public void Cobs_EmptySource_InvalidLength()
    {
        var dst = new byte[8];
        var n = CobsCodec.StuffBytes([], dst, out var error);
        Assert.Equal(CobsError.InvalidLength, error);
        Assert.Equal(0, n);
    }

    private static byte[] Encode(HsuartPacketId id, byte[] payload)
    {
        var buffer = new byte[HsuartPacket.MaxPacketSize + 16];
        var n = HsuartPacket.Encode(id, payload, buffer, out var error);
        Assert.Equal(HsuartPacketError.Success, error);
        return buffer[..n];
    }
}
