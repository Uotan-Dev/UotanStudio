using System.Globalization;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.OtaDelta.Dat;
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

    [Fact]
    public void CanHandle_BrotliDataFileWithSibling_IsDat()
    {
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", BuildPattern(6 * DatWriter.BlockSize, 9));
        var datPath = Path.Combine(temp.Path, "system.new.dat");
        var listPath = Path.Combine(temp.Path, "system.transfer.list");
        DatWriter.Write(imagePath, datPath, listPath, new DatWriteOptions { CompressWithBrotli = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var brPath = datPath + ".br";
        Assert.True(File.Exists(brPath));

        Assert.True(new DatFormatHandler().CanHandle(brPath));
    }

    [Fact]
    public void CanHandle_StrayDataSuffixesWithoutSibling_NotDat()
    {
        using var temp = new TempDirectory();
        var strayBr = temp.WriteBytes("archive.new.dat.br", BuildPattern(1024, 5));
        var strayDat = temp.WriteBytes("notes.new.dat", BuildPattern(1024, 5));
        var handler = new DatFormatHandler();

        Assert.False(handler.CanHandle(strayBr));
        Assert.False(handler.CanHandle(strayDat));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unpack_TransferList_ReconstructsSourceImage(bool brotli)
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var image = BuildPattern(24 * DatWriter.BlockSize, 5);
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "system.new.dat");
        var listPath = Path.Combine(temp.Path, "system.transfer.list");
        DatWriter.Write(imagePath, datPath, listPath, new DatWriteOptions { CompressWithBrotli = brotli },
            cancellationToken: TestContext.Current.CancellationToken);

        var outputDir = Path.Combine(temp.Path, "out");
        var handler = FormatDetector.Detect(listPath);
        Assert.Equal(DatFormatName, handler!.FormatName);

        var result = await handler!.UnpackAsync(listPath, outputDir, new UnpackOptions(), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        var produced = Path.Combine(outputDir, "system.img");
        Assert.True(File.Exists(produced));
        Assert.Equal(result.Metadata["OutputImage"], Path.GetFullPath(produced));
        Assert.Equal(image, File.ReadAllBytes(produced));
    }

    [Fact]
    public async Task Unpack_DataFileOpenedDirectly_ReconstructsImage()
    {
        using var temp = new TempDirectory();
        var image = BuildPattern(8 * DatWriter.BlockSize, 3);
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "vendor.new.dat");
        var listPath = Path.Combine(temp.Path, "vendor.transfer.list");
        DatWriter.Write(imagePath, datPath, listPath);

        var outputDir = Path.Combine(temp.Path, "out");
        var result = await new DatFormatHandler().UnpackAsync(datPath, outputDir, null, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(image, File.ReadAllBytes(Path.Combine(outputDir, "vendor.img")));
    }

    [Fact]
    public async Task Unpack_IncrementalTransferList_FailsWithoutSourceImage()
    {
        using var temp = new TempDirectory();
        var listPath = temp.Write("system.transfer.list", "2\n4\n1\n2\nstash abc123 2,0,2\nnew 2,2,4\n");
        temp.WriteBytes("system.new.dat", []);
        var outputDir = Path.Combine(temp.Path, "out");

        var result = await new DatFormatHandler().UnpackAsync(listPath, outputDir, null, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(outputDir, "system.img")));
    }

    [Fact]
    public async Task Pack_RawImageToDirectory_WritesPairAndRoundTripsThroughUnpack()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var image = BuildPattern(32 * DatWriter.BlockSize, 5);
        var imagePath = temp.WriteBytes("vendor.img", image);
        var outputDir = Path.Combine(temp.Path, "pair");
        Directory.CreateDirectory(outputDir);

        var packResult = await new DatFormatHandler().PackAsync(imagePath, outputDir, TestContext.Current.CancellationToken);
        Assert.True(packResult.Success, packResult.Summary);
        Assert.True(File.Exists(Path.Combine(outputDir, "vendor.transfer.list")));
        Assert.True(File.Exists(Path.Combine(outputDir, "vendor.new.dat")));

        var unpackDir = Path.Combine(temp.Path, "restored");
        var unpackResult = await new DatFormatHandler().UnpackAsync(
            Path.Combine(outputDir, "vendor.transfer.list"), unpackDir, new UnpackOptions(),
            TestContext.Current.CancellationToken);
        Assert.True(unpackResult.Success, unpackResult.Summary);
        Assert.Equal(image, File.ReadAllBytes(Path.Combine(unpackDir, "vendor.img")));
    }

    [Fact]
    public async Task Pack_ExplicitTransferListPath_DerivesDataSiblingAndRoundTrips()
    {
        using var temp = new TempDirectory();
        var image = BuildPattern(16 * DatWriter.BlockSize, 3);
        var imagePath = temp.WriteBytes("product.img", image);
        var listPath = Path.Combine(temp.Path, "out", "product.transfer.list");
        Directory.CreateDirectory(Path.GetDirectoryName(listPath)!);

        var packResult = await new DatFormatHandler().PackAsync(imagePath, listPath, TestContext.Current.CancellationToken);
        Assert.True(packResult.Success, packResult.Summary);
        Assert.Equal(listPath, packResult.Metadata["TransferList"]);
        Assert.Equal(listPath[..^".transfer.list".Length] + ".new.dat", packResult.Metadata["DataFile"]);
        Assert.True(File.Exists(packResult.Metadata["DataFile"]));

        var unpackDir = Path.Combine(temp.Path, "restored");
        var handler = new DatFormatHandler();
        Assert.True((await handler.UnpackAsync(listPath, unpackDir, new UnpackOptions(), TestContext.Current.CancellationToken)).Success);
        Assert.Equal(image, File.ReadAllBytes(Path.Combine(unpackDir, "product.img")));
    }

    [Fact]
    public async Task Pack_BrotliOverride_EmitsNewDatBrAndRoundTrips()
    {
        using var temp = new TempDirectory();
        var image = BuildPattern(64 * DatWriter.BlockSize, 9);
        var imagePath = temp.WriteBytes("system.img", image);
        var outputDir = Path.Combine(temp.Path, "pair");
        Directory.CreateDirectory(outputDir);

        using (RepackMetadata.UsePackingOverrides(new Dictionary<string, string> { ["DatBrotli"] = "true" }))
        {
            var packResult = await new DatFormatHandler().PackAsync(imagePath, outputDir, TestContext.Current.CancellationToken);
            Assert.True(packResult.Success, packResult.Summary);
            Assert.Equal("true", packResult.Metadata["BrotliCompressed"]);
            Assert.True(File.Exists(Path.Combine(outputDir, "system.new.dat.br")));
            Assert.False(File.Exists(Path.Combine(outputDir, "system.new.dat")));
        }

        var unpackDir = Path.Combine(temp.Path, "restored");
        var unpack = await new DatFormatHandler().UnpackAsync(
            Path.Combine(outputDir, "system.transfer.list"), unpackDir, new UnpackOptions(),
            TestContext.Current.CancellationToken);
        Assert.True(unpack.Success, unpack.Summary);
        Assert.Equal(image, File.ReadAllBytes(Path.Combine(unpackDir, "system.img")));
    }

    [Fact]
    public async Task Pack_SparseInput_ConvertsToRawAndRoundTrips()
    {
        using var temp = new TempDirectory();
        var raw = BuildPattern(24 * DatWriter.BlockSize, 4);
        var rawPath = temp.WriteBytes("cache.img", raw);
        var sparsePath = Path.Combine(temp.Path, "cache.simg");
        UotanStudio.Firmware.BlockImage.Sparse.SparseImageConverter
            .ConvertRawToSparse(rawPath, sparsePath);

        var outputDir = Path.Combine(temp.Path, "pair");
        Directory.CreateDirectory(outputDir);
        var packed = await new DatFormatHandler().PackAsync(sparsePath, outputDir, TestContext.Current.CancellationToken);
        Assert.True(packed.Success, packed.Summary);
        Assert.True(File.Exists(Path.Combine(outputDir, "cache.transfer.list")));
        Assert.True(File.Exists(Path.Combine(outputDir, "cache.new.dat")));

        var unpackDir = Path.Combine(temp.Path, "restored");
        Assert.True((await new DatFormatHandler().UnpackAsync(
            Path.Combine(outputDir, "cache.transfer.list"), unpackDir, new UnpackOptions(),
            TestContext.Current.CancellationToken)).Success);
        Assert.Equal(raw, File.ReadAllBytes(Path.Combine(unpackDir, "cache.img")));
    }

    [Fact]
    public async Task Pack_FailureToReadSource_LeavesExistingPairUntouched()
    {
        using var temp = new TempDirectory();
        var seed = BuildPattern(8 * DatWriter.BlockSize, 5);
        var seedPath = temp.WriteBytes("seed.img", seed);
        var dat = Path.Combine(temp.Path, "system.new.dat");
        var list = Path.Combine(temp.Path, "system.transfer.list");
        DatWriter.Write(seedPath, dat, list);
        var originalDat = File.ReadAllBytes(dat);
        var originalList = File.ReadAllBytes(list);

        var result = await new DatFormatHandler().PackAsync(
            Path.Combine(temp.Path, "does-not-exist.img"), temp.Path, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(originalDat, File.ReadAllBytes(dat));
        Assert.Equal(originalList, File.ReadAllBytes(list));
    }

    [Fact]
    public async Task Pack_OverwritesExistingPairAtomicallyAndRoundTrips()
    {
        using var temp = new TempDirectory();
        var old = BuildPattern(4 * DatWriter.BlockSize, 1);
        var oldPath = temp.WriteBytes("old.img", old);
        var dat = Path.Combine(temp.Path, "pair", "system.new.dat");
        var list = Path.Combine(temp.Path, "pair", "system.transfer.list");
        Directory.CreateDirectory(Path.GetDirectoryName(dat)!);
        DatWriter.Write(oldPath, dat, list);

        var replacement = BuildPattern(20 * DatWriter.BlockSize, 7);
        var replacementPath = temp.WriteBytes("system.img", replacement);
        var packed = await new DatFormatHandler().PackAsync(replacementPath, list, TestContext.Current.CancellationToken);
        Assert.True(packed.Success, packed.Summary);

        var unpackDir = Path.Combine(temp.Path, "restored");
        Assert.True((await new DatFormatHandler().UnpackAsync(list, unpackDir, new UnpackOptions(),
            TestContext.Current.CancellationToken)).Success);
        Assert.Equal(replacement, File.ReadAllBytes(Path.Combine(unpackDir, "system.img")));
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