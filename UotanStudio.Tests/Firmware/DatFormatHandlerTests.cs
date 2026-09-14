using System.Globalization;
using UotanStudio.Firmware;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// DAT format handler tests: a real DAT pair produced by <see cref="DatWriter"/> must be recognized by
/// <see cref="FormatDetector"/> (after <see cref="FirmwareFormatRegistry.RegisterAll"/>) and expose
/// non-empty transfer-list metadata, whether the transfer list or the <c>.new.dat</c> file is opened;
/// unrelated <c>.dat</c>/<c>.list</c> files must not be misrouted.
/// <para>DAT 格式处理器测试：经 <see cref="DatWriter"/> 产出的真实 DAT 镜像对在
/// <see cref="FirmwareFormatRegistry.RegisterAll"/> 后须能被 <see cref="FormatDetector"/> 识别，并在打开
/// transfer list 或 <c>.new.dat</c> 时暴露非空的 transfer list 元数据；无关的 <c>.dat</c>/<c>.list</c> 文件
/// 不得被误路由。</para>
/// </summary>
public class DatFormatHandlerTests
{
    private const string DatFormatName = "Android DAT (transfer list)";

    [Fact]
    public void Detect_TransferListFromDatWriter_ReturnsDatHandler()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", BuildPattern(2 * DatWriter.BlockSize, 7));
        var datPath = Path.Combine(temp.Path, "system.new.dat");
        var listPath = Path.Combine(temp.Path, "system.transfer.list");
        DatWriter.Write(imagePath, datPath, listPath);

        var handler = FormatDetector.Detect(listPath);

        Assert.NotNull(handler);
        Assert.Equal(DatFormatName, handler!.FormatName);
    }

    [Fact]
    public async Task ReadMetadata_TransferList_ReturnsNonEmptyMetadata()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", BuildPattern(2 * DatWriter.BlockSize, 7));
        var datPath = Path.Combine(temp.Path, "system.new.dat");
        var listPath = Path.Combine(temp.Path, "system.transfer.list");
        DatWriter.Write(imagePath, datPath, listPath);

        var handler = FormatDetector.Detect(listPath);
        Assert.NotNull(handler);
        Assert.Equal(DatFormatName, handler!.FormatName);

        var result = await handler.ReadMetadataAsync(listPath, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.NotEmpty(result.Metadata);
        Assert.Equal(DatFormatName, result.Metadata["Format"]);
        Assert.Equal("4", result.Metadata["Version"]);
        Assert.Equal(DatWriter.BlockSize.ToString(CultureInfo.InvariantCulture), result.Metadata["BlockSize"]);
        Assert.Equal("system.transfer.list", result.Metadata["TransferList"]);
        Assert.Equal("system.new.dat", result.Metadata["DataFile"]);
        Assert.Equal("raw", result.Metadata["DataFileType"]);
    }

    [Fact]
    public async Task ReadMetadata_DataFile_DetectsPairAndParsesCompanion()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", BuildPattern(3 * DatWriter.BlockSize, 3));
        var datPath = Path.Combine(temp.Path, "system.new.dat");
        var listPath = Path.Combine(temp.Path, "system.transfer.list");
        DatWriter.Write(imagePath, datPath, listPath);

        var handler = FormatDetector.Detect(datPath);

        Assert.NotNull(handler);
        Assert.Equal(DatFormatName, handler!.FormatName);

        var result = await handler.ReadMetadataAsync(datPath, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.NotEmpty(result.Metadata);
        Assert.Equal("system.new.dat", result.Metadata["DataFile"]);
        Assert.Equal("raw", result.Metadata["DataFileType"]);
        Assert.Equal("system.transfer.list", result.Metadata["TransferList"]);
    }

    [Fact]
    public void Detect_GenericDatWithoutSibling_NotDat()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("random.dat", BuildPattern(1024, 5));

        var handler = FormatDetector.Detect(path);

        Assert.NotEqual(DatFormatName, handler?.FormatName);
    }

    [Fact]
    public void Detect_RandomListText_NotDat()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var path = temp.Write("shopping.list", "eggs\nmilk\nbread\n");

        var handler = FormatDetector.Detect(path);

        Assert.NotEqual(DatFormatName, handler?.FormatName);
    }

    private static byte[] BuildPattern(int size, int factor)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++)
        {
            data[i] = (byte)((i * factor + 3) & 0xFF);
        }

        return data;
    }
}