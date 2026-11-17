using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Tegra;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class TegraNv3pProtocolTests
{
    [Theory]
    [InlineData(TegraNv3pCommand.GetPlatformInfo, 0x01u)]
    [InlineData(TegraNv3pCommand.GetBct, 0x02u)]
    [InlineData(TegraNv3pCommand.DownloadBct, 0x04u)]
    [InlineData(TegraNv3pCommand.DownloadBootloader, 0x06u)]
    [InlineData(TegraNv3pCommand.Status, 0x0au)]
    public void Nv3pCommand_WireValue_MatchesTheVendorHeader(TegraNv3pCommand command, uint expected)
    {
        Assert.Equal(expected, (uint)command);
    }

    [Theory]
    [InlineData(TegraNv3pPacketType.Command, 0x1u)]
    [InlineData(TegraNv3pPacketType.Data, 0x2u)]
    [InlineData(TegraNv3pPacketType.Encrypted, 0x3u)]
    [InlineData(TegraNv3pPacketType.Ack, 0x4u)]
    [InlineData(TegraNv3pPacketType.Nack, 0x5u)]
    public void Nv3pPacketType_WireValue_MatchesTheVendorHeader(TegraNv3pPacketType packetType, uint expected)
    {
        Assert.Equal(expected, (uint)packetType);
    }

    [Fact]
    public void PacketSizes_MatchTheVendorHeader()
    {
        Assert.Equal(12, TegraNv3pConstants.BasicPacketSize);
        Assert.Equal(8, TegraNv3pConstants.CommandPacketSize);
        Assert.Equal(4, TegraNv3pConstants.DataPacketSize);
        Assert.Equal(4, TegraNv3pConstants.EncryptedPacketSize);
        Assert.Equal(4, TegraNv3pConstants.FooterPacketSize);
        Assert.Equal(0, TegraNv3pConstants.AckPacketSize);
        Assert.Equal(4, TegraNv3pConstants.NackPacketSize);
        Assert.Equal(32, TegraNv3pConstants.StatusStringLength);
        Assert.Equal(1u, TegraNv3pConstants.ProtocolVersion);
    }

    [Fact]
    public void ComputeChecksum_SumOfBytes_IsTheByteWiseTotal()
    {
        var buffer = new byte[] { 0x01, 0x02, 0x03, 0xFF };

        var checksum = TegraNv3pConstants.ComputeChecksum(buffer);

        Assert.Equal(0x0000_0105u, checksum);
        Assert.Equal(0u, TegraNv3pConstants.ComputeChecksum(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ComputeChecksum_HeaderPlusDeclaredChecksum_ReachesTheZeroTotalTheDeviceVerifies()
    {
        var body = new byte[] { 0x10, 0x20, 0x30 };
        var declared = TegraNv3pConstants.ComputeChecksum(body);
        var packet = new byte[body.Length + 4];
        body.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(body.Length), declared);

        var total = TegraNv3pConstants.ComputeChecksum(packet);
        var declaredBytes = TegraNv3pConstants.ComputeChecksum(packet.AsSpan(body.Length, 4));

        Assert.Equal(declared + declaredBytes, total);
        Assert.NotEqual(0u, declared);
    }

    [Fact]
    public void PlatformInfo_Generation_IsDerivedFromTheLowChipIdByte()
    {
        var info = CreatePlatformInfo(chipId: 0x35, operatingMode: 0x3);

        Assert.Equal(TegraChip.Tegra114, info.Generation);
        Assert.True(info.IsFlashable);
    }

    [Theory]
    [InlineData(0x1u, true)]
    [InlineData(0x3u, true)]
    [InlineData(0x4u, true)]
    [InlineData(0x5u, true)]
    [InlineData(0x6u, true)]
    [InlineData(0x2u, false)]
    [InlineData(0x0u, false)]
    [InlineData(0x7u, false)]
    public void IsFlashableOperatingMode_OnlyTheFiveDownloadModes_AreAccepted(uint mode, bool expected)
    {
        Assert.Equal(expected, TegraConstants.IsFlashableOperatingMode(mode));
    }

    [Fact]
    public void ProtocolException_DeviceStatus_IsPreservedVerbatim()
    {
        var exception = new TegraProtocolException("bad data", 0xDEAD_BEEFu, "nand failure");

        Assert.Equal(0xDEAD_BEEFu, exception.StatusCode);
        Assert.Equal("nand failure", exception.DeviceMessage);
        Assert.Contains("0xDEADBEEF", exception.Message);
        Assert.Contains("nand failure", exception.Message);
        Assert.IsAssignableFrom<IOException>(exception);
    }

    [Fact]
    public void ProtocolException_WithoutStatus_KeepsThePlainMessage()
    {
        var exception = new TegraProtocolException("link refused");

        Assert.Null(exception.StatusCode);
        Assert.Null(exception.DeviceMessage);
        Assert.Equal("link refused", exception.Message);
    }

    private static TegraPlatformInfo CreatePlatformInfo(ushort chipId, uint operatingMode) => new()
    {
        UniqueIdLow = 0x1111_1111_1111_1111ul,
        UniqueIdHigh = 0x2222_2222_2222_2222ul,
        Chip = new TegraChipId(chipId, 1, 2),
        Sku = 0x87,
        Version = 0x0035_0001u,
        BootDevice = 0x2,
        OperatingMode = operatingMode,
        DeviceConfigurationStrap = 0,
        DeviceConfigurationFuse = 0,
        SdramConfigurationStrap = 0,
        Board = new TegraBoardId(0x0000_0177u, 0x0000_0011u, 0x0000_0002u, 0x0000_0001u)
    };
}