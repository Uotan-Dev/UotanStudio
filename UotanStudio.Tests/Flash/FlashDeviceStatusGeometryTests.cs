using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Services;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

public sealed class FlashDeviceStatusGeometryTests
{
    private static RkFlashInfo DecodeFlashInfo(uint sectors, ushort blockSectors, byte pageSectors, byte manufacturer)
    {
        var response = new byte[11];
        BinaryPrimitives.WriteUInt32LittleEndian(response, sectors);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(4), blockSectors);
        response[6] = pageSectors;
        response[9] = manufacturer;
        response[10] = 1;
        return RkFlashInfo.Decode(response);
    }

    [Fact]
    public void DescribeRkFlashGeometry_SpiNor_ReportsEachValueOnItsOwnLine()
    {
        var info = DecodeFlashInfo(32768, 128, 4, 0);
        var language = LanguageService.Instance;

        var lines = FlashViewModel.DescribeRkFlashGeometry(info, language);

        Assert.Equal(4, lines.Count);
        Assert.Equal(language.GetString("Flash_RkStorageSize", 16L, 32768L, 512), lines[0]);
        Assert.Equal(language.GetString("Flash_RkEraseBlockSize", 64L), lines[1]);
        Assert.Equal(language.GetString("Flash_RkPageSizeBytes", 2048L), lines[2]);
        Assert.Equal(language.GetString("Flash_RkFlashVendor", "SAMSUNG"), lines[3]);
    }

    [Fact]
    public void DescribeRkFlashGeometry_MediumWithoutBlockGeometry_ReportsTheCapacityAlone()
    {
        var info = DecodeFlashInfo(65536, 0, 0, 4);
        var language = LanguageService.Instance;

        var lines = FlashViewModel.DescribeRkFlashGeometry(info, language);

        Assert.Single(lines);
        Assert.Equal(language.GetString("Flash_RkStorageSize", 32L, 65536L, 512), lines[0]);
    }
}