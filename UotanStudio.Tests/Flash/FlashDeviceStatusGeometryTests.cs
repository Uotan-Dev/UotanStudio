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

    [Fact]
    public void AppendRkStorageRows_MediaWithoutGeometry_AreLeftOutOfTheTableAndSelector()
    {
        var media = new (string Key, int Ordinal, IReadOnlyList<string> Geometry)[]
        {
            (FlashDeviceStatusRowKeys.Storage, 1, ["存储容量：238552 MiB（488554496 个扇区 × 512 B）"]),
            (FlashDeviceStatusRowKeys.StorageProbe, 2, []),
            (FlashDeviceStatusRowKeys.StorageProbe, 4, ["存储容量：16 MiB（32768 个扇区 × 512 B）"]),
        };
        var rows = new List<FlashDeviceStatusEntry>();

        var storages = FlashViewModel.AppendRkStorageRows(rows, media);

        Assert.Equal(new[] { 1, 4 }, storages);
        Assert.Equal(new[] { "EMMC", string.Empty, "SPINOR", string.Empty }, rows.Select(row => row.Name));
        Assert.Equal(new[] { string.Empty, media[0].Geometry[0], string.Empty, media[2].Geometry[0] },
            rows.Select(row => row.Value));
    }
}