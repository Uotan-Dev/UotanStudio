using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Bootrom;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class RkdlFlashGeometryDecodeTests
{
    [Fact]
    public void Decode_PackedFlashInfoStructure_MapsEveryReportedField()
    {
        var response = new byte[RkdlConstants.FlashInfoStructureLength];
        BinaryPrimitives.WriteUInt32LittleEndian(response, 0x0001_0000);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(4), 128);
        response[6] = 4;
        response[7] = 40;
        response[8] = 7;
        response[9] = 1;
        response[10] = 0x05;

        var info = RkFlashInfo.Decode(response);

        Assert.Equal(0x0001_0000L, info.SectorCount);
        Assert.Equal(128, info.BlockSizeSectors);
        Assert.Equal(4, info.PageSizeSectors);
        Assert.Equal(40, info.EccBits);
        Assert.Equal(7, info.AccessTime);
        Assert.Equal(1, info.ManufacturerCode);
        Assert.Equal("TOSHIBA", info.ManufacturerName);
        Assert.Equal(0x05, info.FlashCs);
        Assert.Equal(new[] { 0, 2 }, info.FlashChips);
        Assert.Equal(512L, info.BlockCount);
        Assert.Equal(128L * RkdlConstants.SectorSize, info.BlockSizeBytes);
        Assert.Equal(0x0001_0000L * RkdlConstants.SectorSize, info.Size);
        Assert.Equal(response, info.RawResponse);
    }

    [Fact]
    public void Decode_ResponseShorterThanStructure_Throws()
    {
        var response = new byte[RkdlConstants.FlashInfoStructureLength - 1];

        var error = Assert.Throws<RkdlException>(() => RkFlashInfo.Decode(response));

        Assert.Equal(RkdlError.FlashInfoFailed, error.ErrorCode);
    }

    [Fact]
    public void Decode_StructureWithoutBlockGeometry_ReportsZeroBlocks()
    {
        var response = new byte[RkdlConstants.FlashInfoStructureLength];
        BinaryPrimitives.WriteUInt32LittleEndian(response, 0x0001_0000);

        var info = RkFlashInfo.Decode(response);

        Assert.Equal(0, info.BlockSizeSectors);
        Assert.Equal(0L, info.BlockCount);
        Assert.Equal("SAMSUNG", info.ManufacturerName);
        Assert.Empty(info.FlashChips);
    }

    [Fact]
    public void Decode_CapabilityWord_MapsDirectLbaAndFirst4MAccessBits()
    {
        var capability = RkCapability.Decode(new byte[] { 0x05, 0, 0, 0, 0, 0, 0, 0 });

        Assert.True(capability.DirectLba);
        Assert.True(capability.First4MAccess);
        Assert.Equal(new byte[] { 0x05, 0, 0, 0, 0, 0, 0, 0 }, capability.Raw);
    }

    [Fact]
    public void Decode_CapabilityWordWithoutFlags_ReportsNoFlags()
    {
        var capability = RkCapability.Decode(new byte[8]);

        Assert.False(capability.DirectLba);
        Assert.False(capability.First4MAccess);
    }

    [Fact]
    public void Decode_CapabilityShortOfEightBytes_Throws()
    {
        var error = Assert.Throws<RkdlException>(() => RkCapability.Decode(new byte[7]));

        Assert.Equal(RkdlError.ReadFailed, error.ErrorCode);
    }

    [Fact]
    public void EmmcFlashId_ReadAsLittleEndianBytes_SpellsEmmc()
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, RkdlConstants.EmmcFlashId);

        Assert.Equal("EMMC", System.Text.Encoding.ASCII.GetString(bytes));
    }
}