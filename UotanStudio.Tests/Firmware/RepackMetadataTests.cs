using System.Text.Json.Nodes;
using UotanStudio.Firmware.BoundedContexts.Archive;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Dtb;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Dtbo;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Rockchip;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;
using UotanStudio.Services.Firmware;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class RepackMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsWindows()
        ? Path.GetPathRoot(Environment.SystemDirectory)! : Path.GetTempPath(), "uotanstudio-test-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private string Source => Path.Combine(_root, "source");
    private string Extracted => Path.Combine(_root, "extracted");
    private string Image => Path.Combine(_root, "original.img");
    private string Output => Path.Combine(_root, "rebuilt.img");

    public RepackMetadataTests()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "hello.txt"), "firmware metadata test");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task PackingOverrides_RebuildFilesystemOverPreservedMetadata(string format)
    {
        IWritableFormatHandler handler = format == "EXT4" ? new Ext4FormatHandler() : new ErofsFormatHandler();
        Assert.True((await handler.PackAsync(Source, Image, Cancellation)).Success);
        Assert.True((await handler.UnpackAsync(Image, Extracted, cancellationToken: Cancellation)).Success);
        var metadata = Assert.IsType<RepackMetadata>(RepackMetadata.Read(Extracted));
        Assert.True(metadata.CanPackDirectory);

        var baseline = await RegisteredFirmwareFormats.PackDirectoryAsync(metadata.FormatName, Extracted, Output, Cancellation);
        Assert.True(baseline.Success, baseline.Summary);
        var baselineInfo = await handler.ReadMetadataAsync(Output, Cancellation);
        Assert.True(baselineInfo.Success, baselineInfo.Summary);
        Assert.Contains(ReadImagePaths(Output, format), path => path.EndsWith("hello.txt", StringComparison.Ordinal));

        IReadOnlyDictionary<string, string> overrides = format == "EXT4"
            ? new Dictionary<string, string> { ["EnableSparse"] = "1" }
            : new Dictionary<string, string> { ["ErofsAlgorithm"] = "lz4hc", ["ErofsCompressionLevel"] = "9" };
        using (RepackMetadata.UsePackingOverrides(overrides))
        {
            var result = await RegisteredFirmwareFormats.PackDirectoryAsync(metadata.FormatName, Extracted, Output, Cancellation);
            Assert.True(result.Success, result.Summary);
            var overridden = await handler.ReadMetadataAsync(Output, Cancellation);
            Assert.True(overridden.Success, overridden.Summary);
            Assert.Contains(ReadImagePaths(Output, format), path => path.EndsWith("hello.txt", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    [InlineData("F2FS")]
    public async Task FileSystemDirectories_RecordFormatAndExcludeMetadataFromRepack(string format)
    {
        IWritableFormatHandler handler = format switch
        {
            "EXT4" => new Ext4FormatHandler(),
            "EROFS" => new ErofsFormatHandler(),
            _ => new F2fsFormatHandler()
        };
        var packed = await handler.PackAsync(Source, Image, Cancellation);
        Assert.True(packed.Success, packed.Summary);
        var unpacked = await handler.UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(unpacked.Success, unpacked.Summary);
        var metadata = Assert.IsType<RepackMetadata>(RepackMetadata.Read(Extracted));
        Assert.Equal(format, metadata.FormatName);
        Assert.Equal("original.img", metadata.SourceFileName);
        Assert.Equal(new FileInfo(Image).Length, metadata.SourceLength);
        Assert.Equal("4096", metadata.PackingOptions["BlockSize"]);
        Assert.True(metadata.CanPackDirectory);
        Assert.Equal(format, metadata.Configuration["fileSystem"]!.GetValue<string>());
        var superblock = metadata.Configuration["superblock"]!.AsObject();
        using (var source = File.OpenRead(Image))
        {
            var bytes = new byte[4096];
            source.ReadExactly(bytes);
            var uuidOffset = format switch { "EXT4" => 1128, "EROFS" => 1072, _ => 1132 };
            Assert.Equal(Convert.ToHexStringLower(bytes.AsSpan(uuidOffset, 16)), superblock["uuid"]!.GetValue<string>());
        }
        Assert.NotNull(superblock[format == "F2FS" ? "feature" : "featureIncompat"]);
        Assert.True(PackingService.BuildImage(Extracted, Output).Success);
        var info = await handler.ReadMetadataAsync(Output, Cancellation);
        Assert.True(info.Success, info.Summary);
        var paths = ReadImagePaths(Output, format);
        Assert.DoesNotContain(paths, path => path.Contains(RepackMetadata.FileName));
        Assert.Contains(paths, path => path.EndsWith("hello.txt", StringComparison.Ordinal));
    }

    private static string[] ReadImagePaths(string path, string format)
    {
        if (format == "EXT4")
        {
            using var input = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            input.ReadExactly(magic);
            input.Position = 0;
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(magic) == 0xED26FF3A)
            {
                using var sparse = UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO.SparseReader.FromStream(input);
                using var logical = new UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO.SparseReadStream(sparse);
                using var sparseParser = new UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser.Ext4Parser(logical);
                return sparseParser.ListEntriesWithRoot().Select(entry => entry.Path).ToArray();
            }
            using var parser = new UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser.Ext4Parser(input);
            return parser.ListEntriesWithRoot().Select(entry => entry.Path).ToArray();
        }
        if (format == "EROFS")
        {
            using var parser = new ErofsParser(path);
            return parser.EnumerateEntries().Select(entry => entry.Path).ToArray();
        }
        using var f2fs = new UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser.F2fsParser(path);
        return f2fs.ListEntriesWithRoot().Select(entry => entry.Path).ToArray();
    }

    [Fact]
    public async Task ErofsMetadata_PreservesVolumeName_AndAllowsEditingOptions()
    {
        new ErofsBuilder().Build(Source, Image, new ErofsOptions { VolumeName = "vendor" });
        var result = await new ErofsFormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(result.Success, result.Summary);
        var metadata = Assert.IsType<RepackMetadata>(RepackMetadata.Read(Extracted));
        Assert.Equal("vendor", metadata.PackingOptions["VolumeName"]);
        EditMetadata(json => json["packingOptions"]!["VolumeName"] = "modified");
        Assert.True(PackingService.BuildImage(Extracted, Output).Success);
        using var parser = new ErofsParser(Output);
        Assert.Equal("modified", parser.VolumeName);
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    [InlineData("F2FS")]
    public async Task SparseFileSystemMetadata_MatchesOriginalSuperblock(string format)
    {
        IWritableFormatHandler handler = format switch
        {
            "EXT4" => new Ext4FormatHandler(),
            "EROFS" => new ErofsFormatHandler(),
            _ => new F2fsFormatHandler()
        };
        Assert.True((await handler.PackAsync(Source, Image, Cancellation)).Success);
        Assert.True((await handler.UnpackAsync(Image, Extracted, cancellationToken: Cancellation)).Success);
        var original = RepackMetadata.Read(Extracted)!.Configuration;
        var sparsePath = Path.Combine(_root, "sparse.img");
        var sparseOutput = Path.Combine(_root, "sparse-output");
        UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Conversion.SparseImageConverter.ConvertRawToSparse(Image, sparsePath);
        var result = await new UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.SparseFormatHandler()
            .UnpackAsync(sparsePath, sparseOutput, cancellationToken: Cancellation);
        Assert.True(result.Success, result.Summary);
        var wrapped = RepackMetadata.Read(sparseOutput)!;
        Assert.Equal("Android Sparse Image (simg)", wrapped.FormatName);
        Assert.True(JsonNode.DeepEquals(original, wrapped.Configuration));
    }

    [Fact]
    public async Task CpioMetadata_IsNotAddedToArchive()
    {
        var handler = new CpioFormatHandler();
        var packed = await handler.PackAsync(Source, Image, Cancellation);
        Assert.True(packed.Success, packed.Summary);
        var unpack = await handler.UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(unpack.Success, unpack.Summary);
        Assert.True(RepackMetadata.Read(Extracted)!.CanPackDirectory);
        Assert.True(PackingService.BuildImage(Extracted, Output).Success);
        var info = await handler.ReadMetadataAsync(Output, Cancellation);
        Assert.Equal(["hello.txt"], info.Entries);
    }

    [Fact]
    public async Task DtboMetadata_EmbedsOriginalConfiguration()
    {
        File.WriteAllBytes(Image, DtboFormat.Pack(DtbFormat.Write(new DeviceTree())));
        var result = await new DtFormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(result.Success, result.Summary);
        var metadata = Assert.IsType<RepackMetadata>(RepackMetadata.Read(Extracted));
        Assert.NotNull(metadata.Configuration["header"]);
        Assert.Single(metadata.Configuration["entries"]!.AsArray());
        Assert.False(File.Exists(Path.Combine(Extracted, "dtbo.json")));
        Assert.Equal("1", metadata.SourceProperties["EntryCount"]);
        Assert.True(PackingService.BuildImage(Extracted, Output).Success);
        Assert.Equal(File.ReadAllBytes(Image), File.ReadAllBytes(Output));
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    [InlineData("F2FS")]
    public async Task ApplicationFileSystemExtraction_WritesMetadata(string format)
    {
        IWritableFormatHandler handler = format switch
        {
            "EXT4" => new Ext4FormatHandler(),
            "EROFS" => new ErofsFormatHandler(),
            _ => new F2fsFormatHandler()
        };
        Assert.True((await handler.PackAsync(Source, Image, Cancellation)).Success);
        var original = await handler.ReadMetadataAsync(Image, Cancellation);
        var result = await FirmwareService.Instance.UnpackFileSystemImageAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(result.Success, result.Summary);
        var metadata = Assert.IsType<RepackMetadata>(RepackMetadata.Read(Extracted));
        Assert.Equal(format, metadata.FormatName);
        Assert.Equal(format, metadata.Configuration["fileSystem"]!.GetValue<string>());
        Assert.NotEmpty(metadata.Configuration["superblock"]!.AsObject());
        foreach (var property in original.Metadata)
            Assert.Equal(property.Value, metadata.SourceProperties[property.Key]);
    }

    [Fact]
    public async Task FailedExtraction_DoesNotCreateMetadata()
    {
        File.WriteAllText(Image, "invalid image");
        var result = await new ErofsFormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(Extracted, RepackMetadata.FileName)));
    }

    [Fact]
    public async Task NoOverwrite_ProtectsMetadataAndContents()
    {
        new ErofsBuilder().Build(Source, Image);
        var handler = new ErofsFormatHandler();
        Assert.True((await handler.UnpackAsync(Image, Extracted, cancellationToken: Cancellation)).Success);
        var before = File.ReadAllBytes(Path.Combine(Extracted, RepackMetadata.FileName));
        File.WriteAllText(Path.Combine(Extracted, "hello.txt"), "keep");
        var result = await handler.UnpackAsync(Image, Extracted, new UnpackOptions { Overwrite = false }, Cancellation);
        Assert.False(result.Success);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Extracted, RepackMetadata.FileName)));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Extracted, "hello.txt")));
    }

    [Fact]
    public async Task InvalidPackingOptions_DoNotTruncateOutput()
    {
        Ext4Builder.Build(Source, Image);
        Assert.True((await new Ext4FormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation)).Success);
        EditMetadata(json => json["packingOptions"]!["BlockSize"] = "invalid");
        File.WriteAllText(Output, "keep");
        Assert.False(PackingService.BuildImage(Extracted, Output).Success);
        Assert.Equal("keep", File.ReadAllText(Output));
    }

    [Fact]
    public void InvalidMetadata_IsNotSilentlyPackedAsExt4()
    {
        Directory.CreateDirectory(Extracted);
        File.WriteAllText(Path.Combine(Extracted, RepackMetadata.FileName), "{invalid");
        File.WriteAllText(Output, "keep");
        Assert.False(PackingService.BuildImage(Extracted, Output).Success);
        Assert.Equal("keep", File.ReadAllText(Output));
    }

    [Fact]
    public async Task ReservedFilenameCollision_PreservesTheOriginalPayloadFile()
    {
        File.WriteAllText(Path.Combine(Source, RepackMetadata.FileName), "ordinary payload file");
        new ErofsBuilder().Build(Source, Image);
        var result = await new ErofsFormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(result.Success, result.Summary);
        var payload = Assert.Single(Directory.GetFiles(Extracted, "uotan-name-*"));
        Assert.Equal("ordinary payload file", File.ReadAllText(payload));
        Assert.NotNull(RepackMetadata.Read(Extracted));
        Assert.True((await new ErofsFormatHandler().PackAsync(Extracted, Output, Cancellation)).Success);
        using var parser = new ErofsParser(Output);
        var found = parser.EnumerateFiles().Any(entry
            => System.Text.Encoding.UTF8.GetString(parser.ReadFileBytes(entry.Path)) == "ordinary payload file");
        Assert.True(found);
    }

    private void EditMetadata(Action<JsonObject> edit)
    {
        var path = Path.Combine(Extracted, RepackMetadata.FileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(json);
        File.WriteAllText(path, json.ToJsonString());
    }
}