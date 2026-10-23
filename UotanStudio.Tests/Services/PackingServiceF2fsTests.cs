using System.Buffers.Binary;
using UotanStudio.Firmware.Application.Equivalence;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.F2fs.Models;
using UotanStudio.Projects;
using UotanStudio.Services.Ui.Firmware;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PackingServiceF2fsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-packing-f2fs-" + Guid.NewGuid().ToString("N"));

    public PackingServiceF2fsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ProjectSettings_F2fs_RoundTripsWithoutFallingBackToErofs()
    {
        var settings = new ProjectSettingViewModel();

        settings.ApplyFromDto(new ProjectSettingsDto { ImageType = "F2FS", ImageFormat = "RAW" });
        var saved = settings.ToDto();

        Assert.True(settings.TypeF2FS);
        Assert.False(settings.TypeEXT4);
        Assert.False(settings.TypeEROFS);
        Assert.Equal("F2FS", saved.ImageType);
        Assert.Null(saved.F2fsCompressionEnabled);
    }

    [Fact]
    public async Task ProjectSettings_F2fsCompression_SaveAndReopen_PreservesExplicitOverride()
    {
        var path = Path.Combine(_root, "compression.uotanfw");
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto { ImageType = "F2FS", ImageFormat = "SPARSE" });
        settings.F2fsCompressionEnabled = true;
        settings.SelectedF2fsCompressionAlgorithm = "Zstd";
        settings.F2fsCompressionLevel = 3;

        var saved = await SolutionPersistenceService.SaveFirmwareProjectAsync(path, new FirmwareProjectSnapshot
        {
            ProjectName = "compression",
            Settings = settings.ToDto()
        }, TestContext.Current.CancellationToken);
        var reopened = await SolutionPersistenceService.LoadFirmwareProjectAsync(path, TestContext.Current.CancellationToken);
        var restored = new ProjectSettingViewModel();
        restored.ApplyFromDto(Assert.IsType<ProjectSettingsDto>(reopened!.Settings));

        Assert.True(saved);
        Assert.True(restored.TypeF2FS);
        Assert.True(restored.FormatSPARSE);
        Assert.True(restored.F2fsCompressionEnabled);
        Assert.Equal("Zstd", restored.SelectedF2fsCompressionAlgorithm);
        Assert.Equal(3, restored.F2fsCompressionLevel);
        Assert.True(restored.HasF2fsCompressionOverride);
    }

    [Fact]
    public void BuildImage_F2fsSelection_ProducesParseableImage()
    {
        var source = Path.Combine(_root, "source");
        var output = Path.Combine(_root, "system.img");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "build.prop"), "ro.product=test\n");
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto { ImageType = "F2FS", ImageFormat = "RAW" });

        var result = PackingService.BuildImage(source, output, settings);

        Assert.True(result.Success, result.Summary);
        using var parser = new F2fsParser(output);
        Assert.Contains(parser.ListFiles(), entry => entry.Path == "build.prop");
    }

    [Fact]
    public void BuildImage_FreshDirectoryBuild_ReportsEveryEquivalenceDimensionAsNotEvaluated()
    {
        var source = Path.Combine(_root, "equivalence-source");
        var output = Path.Combine(_root, "equivalence.img");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "build.prop"), "ro.product=test\n");
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto { ImageType = "F2FS", ImageFormat = "RAW" });

        var result = PackingService.BuildImage(source, output, settings);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(
            [
                FirmwareEquivalenceState.NotEvaluated,
                FirmwareEquivalenceState.NotEvaluated,
                FirmwareEquivalenceState.NotEvaluated,
                FirmwareEquivalenceState.NotEvaluated,
            ],
            [
                result.Equivalence.ByteEquivalence,
                result.Equivalence.StructureEquivalence,
                result.Equivalence.ContentEquivalence,
                result.Equivalence.CapacityEquivalence,
            ]);
    }

    [Theory]
    [InlineData("RAW")]
    [InlineData("SPARSE")]
    public void BuildImage_F2fsCompression_ProducesCompressedFileAndPreservesContent(string imageFormat)
    {
        var source = Path.Combine(_root, "compressed-" + imageFormat);
        var output = Path.Combine(_root, imageFormat + ".img");
        Directory.CreateDirectory(source);
        var content = Enumerable.Repeat((byte)'A', 256 * 1024).ToArray();
        File.WriteAllBytes(Path.Combine(source, "compressible.bin"), content);
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto
        {
            ImageType = "F2FS",
            ImageFormat = imageFormat,
            F2fsCompressionEnabled = true,
            F2fsCompressionAlgorithm = "Zstd",
            F2fsCompressionLevel = 3
        });

        var result = PackingService.BuildImage(source, output, settings);

        Assert.True(result.Success, result.Summary);
        var inspected = output;
        if (imageFormat == "SPARSE")
        {
            using var sparseInput = File.OpenRead(output);
            Span<byte> magic = stackalloc byte[4];
            sparseInput.ReadExactly(magic);
            Assert.Equal(0xED26FF3Au, BinaryPrimitives.ReadUInt32LittleEndian(magic));
            inspected = Path.Combine(_root, "decoded-" + imageFormat + ".img");
            SparseImageConverter.ConvertSparseToRaw([output], inspected);
        }
        using var parser = new F2fsParser(inspected);
        var entry = Assert.Single(parser.ListFiles(), item => item.Path == "compressible.bin");
        Assert.NotEqual(0u, entry.Flags & F2fsInode.ComprFlag);
        Assert.NotEqual(0u, parser.Superblock.Feature & 0x2000u);
        using var extracted = new MemoryStream();
        parser.ReadFile(entry.Path, extracted);
        Assert.Equal(content, extracted.ToArray());
    }

    [Fact]
    public async Task BuildImage_F2fsInvalidPersistedAlgorithm_RejectsInsteadOfFallingBack()
    {
        var source = Path.Combine(_root, "invalid");
        var output = Path.Combine(_root, "invalid.img");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file"), "content");
        var project = Path.Combine(_root, "invalid.uotanfw");
        Assert.True(await SolutionPersistenceService.SaveFirmwareProjectAsync(project, new FirmwareProjectSnapshot
        {
            ProjectName = "invalid",
            Settings = new ProjectSettingsDto
            {
                ImageType = "F2FS",
                F2fsCompressionEnabled = true,
                F2fsCompressionAlgorithm = "999"
            }
        }, TestContext.Current.CancellationToken));
        var reopened = await SolutionPersistenceService.LoadFirmwareProjectAsync(project, TestContext.Current.CancellationToken);
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(Assert.IsType<ProjectSettingsDto>(reopened!.Settings));

        var result = PackingService.BuildImage(source, output, settings);

        Assert.False(result.Success);
        Assert.Contains("Unsupported F2FS compression algorithm", result.Summary);
        Assert.Equal("999", settings.SelectedF2fsCompressionAlgorithm);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task BuildImage_SameFormatWithoutOverride_InheritsSourceCompression()
    {
        var sourceTree = Path.Combine(_root, "source-tree");
        var sourceImage = Path.Combine(_root, "source.img");
        var extracted = Path.Combine(_root, "extracted");
        var output = Path.Combine(_root, "rebuilt.img");
        Directory.CreateDirectory(sourceTree);
        var content = Enumerable.Repeat((byte)'B', 256 * 1024).ToArray();
        File.WriteAllBytes(Path.Combine(sourceTree, "system.bin"), content);
        F2fsBuilder.Build(sourceTree, sourceImage, new F2fsOptions
        {
            EnableCompression = true,
            CompressionAlgorithm = CompressionAlgorithm.Zstd,
            CompressionLevel = 3
        });
        var unpacked = await new F2fsFormatHandler().UnpackAsync(
            sourceImage,
            extracted,
            cancellationToken: TestContext.Current.CancellationToken);
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto { ImageType = "F2FS", ImageFormat = "RAW" });

        var result = PackingService.BuildImage(extracted, output, settings);

        Assert.True(unpacked.Success, unpacked.Summary);
        Assert.True(result.Success, result.Summary);
        using var parser = new F2fsParser(output);
        var entry = Assert.Single(parser.ListFiles(), item => item.Path == "system.bin");
        Assert.NotEqual(0u, entry.Flags & F2fsInode.ComprFlag);
        Assert.Equal([CompressionAlgorithm.Zstd], parser.ReadCompressionAlgorithms());
        using var restored = new MemoryStream();
        parser.ReadFile(entry.Path, restored);
        Assert.Equal(content, restored.ToArray());
    }

    [Fact]
    public async Task BuildImage_SameFormatExplicitOverride_DisablesSourceCompression()
    {
        var sourceTree = Path.Combine(_root, "override-source-tree");
        var sourceImage = Path.Combine(_root, "override-source.img");
        var extracted = Path.Combine(_root, "override-extracted");
        var output = Path.Combine(_root, "override-rebuilt.img");
        var sourceUuid = Guid.Parse("f1054ed3-c440-4e96-8053-54b03ac79a18");
        var selinux = "u:object_r:system_file:s0\0"u8.ToArray();
        Directory.CreateDirectory(sourceTree);
        File.WriteAllBytes(Path.Combine(sourceTree, "system.bin"), Enumerable.Repeat((byte)'C', 256 * 1024).ToArray());
        F2fsBuilder.Build(sourceTree, sourceImage, new F2fsOptions
        {
            EnableCompression = true,
            CompressionAlgorithm = CompressionAlgorithm.Zstd,
            CompressionLevel = 3,
            Uuid = sourceUuid,
            FileXattrs = new Dictionary<string, Dictionary<string, byte[]>>
            {
                ["system.bin"] = new() { ["security.selinux"] = selinux }
            }
        });
        var unpacked = await new F2fsFormatHandler().UnpackAsync(
            sourceImage,
            extracted,
            cancellationToken: TestContext.Current.CancellationToken);
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto
        {
            ImageType = "F2FS",
            ImageFormat = "SPARSE",
            SparseOutput = true,
            F2fsCompressionEnabled = false,
            F2fsCompressionAlgorithm = "Lz4",
            F2fsCompressionLevel = 0
        });

        var result = PackingService.BuildImage(extracted, output, settings);

        Assert.True(unpacked.Success, unpacked.Summary);
        Assert.True(result.Success, result.Summary);
        using (var sparseInput = File.OpenRead(output))
        {
            Span<byte> magic = stackalloc byte[4];
            sparseInput.ReadExactly(magic);
            Assert.Equal(0xED26FF3Au, BinaryPrimitives.ReadUInt32LittleEndian(magic));
        }
        var decoded = Path.Combine(_root, "override-decoded.img");
        SparseImageConverter.ConvertSparseToRaw([output], decoded);
        using var parser = new F2fsParser(decoded);
        var entry = Assert.Single(parser.ListFiles(), item => item.Path == "system.bin");
        Assert.Equal(0u, entry.Flags & F2fsInode.ComprFlag);
        Assert.Empty(parser.ReadCompressionAlgorithms());
        Assert.Equal(sourceUuid.ToByteArray(), parser.SourceUuid);
        Assert.Equal(selinux, entry.Xattrs!["security.selinux"]);
    }

    [Fact]
    public async Task BuildImage_ErofsToF2fs_UsesExplicitCompressionOverride()
    {
        var sourceTree = Path.Combine(_root, "cross-source-tree");
        var sourceImage = Path.Combine(_root, "cross-source.erofs");
        var extracted = Path.Combine(_root, "cross-extracted");
        var output = Path.Combine(_root, "cross-output.img");
        Directory.CreateDirectory(sourceTree);
        var content = Enumerable.Repeat((byte)'D', 256 * 1024).ToArray();
        File.WriteAllBytes(Path.Combine(sourceTree, "product.bin"), content);
        new ErofsBuilder().Build(sourceTree, sourceImage, new ErofsOptions());
        var unpacked = await new ErofsFormatHandler().UnpackAsync(
            sourceImage,
            extracted,
            cancellationToken: TestContext.Current.CancellationToken);
        var settings = new ProjectSettingViewModel();
        settings.ApplyFromDto(new ProjectSettingsDto
        {
            ImageType = "F2FS",
            ImageFormat = "RAW",
            F2fsCompressionEnabled = true,
            F2fsCompressionAlgorithm = "Zstd",
            F2fsCompressionLevel = 3
        });

        var result = PackingService.BuildImage(extracted, output, settings);

        Assert.True(unpacked.Success, unpacked.Summary);
        Assert.True(result.Success, result.Summary);
        using var parser = new F2fsParser(output);
        var entry = Assert.Single(parser.ListFiles(), item => item.Path == "product.bin");
        Assert.NotEqual(0u, entry.Flags & F2fsInode.ComprFlag);
        Assert.Equal([CompressionAlgorithm.Zstd], parser.ReadCompressionAlgorithms());
        using var restored = new MemoryStream();
        parser.ReadFile(entry.Path, restored);
        Assert.Equal(content, restored.ToArray());
    }

    [Fact]
    public void SparsePublication_ConversionFailure_PreservesExistingOutputAndCleansTemporaryFile()
    {
        var raw = Path.Combine(_root, "fault.raw.img");
        var output = Path.Combine(_root, "fault.sparse.img");
        File.WriteAllBytes(raw, new byte[8192]);
        File.WriteAllText(output, "existing");

        Assert.Throws<IOException>(() => SparseImageConverter.ConvertRawToSparseAtomic(
            raw,
            output,
            4096,
            (_, sparse, _, _, _) =>
            {
                sparse.Write("partial"u8);
                throw new IOException("Injected sparse conversion failure.");
            },
            TestContext.Current.CancellationToken));

        Assert.Equal("existing", File.ReadAllText(output));
        Assert.Empty(Directory.EnumerateFiles(_root, "fault.sparse.img.tmp-*"));
    }

    [Fact]
    public void SparsePublication_StructuralValidationFailure_PreservesExistingOutputAndCleansTemporaryFile()
    {
        var raw = Path.Combine(_root, "invalid.raw.img");
        var output = Path.Combine(_root, "invalid.sparse.img");
        File.WriteAllBytes(raw, new byte[8192]);
        File.WriteAllText(output, "existing");

        Assert.ThrowsAny<Exception>(() => SparseImageConverter.ConvertRawToSparseAtomic(
            raw,
            output,
            4096,
            (_, sparse, _, _, _) => sparse.Write("not-a-sparse-image"u8),
            TestContext.Current.CancellationToken));

        Assert.Equal("existing", File.ReadAllText(output));
        Assert.Empty(Directory.EnumerateFiles(_root, "invalid.sparse.img.tmp-*"));
    }

    [Fact]
    public void Build_PreCanceled_PreservesExistingOutputAndDoesNotCreateTemporaryFile()
    {
        var source = Path.Combine(_root, "canceled-source");
        var output = Path.Combine(_root, "canceled.img");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file"), "content");
        File.WriteAllText(output, "existing");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => F2fsBuilder.Build(source, output, new F2fsOptions
        {
            CancellationToken = cancellation.Token,
        }));

        Assert.Equal("existing", File.ReadAllText(output));
        Assert.Empty(Directory.EnumerateFiles(_root, ".canceled.img.*.tmp"));
    }

    [Fact]
    public async Task PackAsync_PreCanceled_PreservesExistingOutputAndCleansTemporaryFiles()
    {
        var source = Path.Combine(_root, "handler-canceled-source");
        var output = Path.Combine(_root, "handler-canceled.sparse.img");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file"), "content");
        File.WriteAllText(output, "existing");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new F2fsFormatHandler().PackAsync(source, output, cancellation.Token));

        Assert.Equal("existing", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(_root, ".handler-canceled.sparse.img.*.raw.tmp"));
        Assert.Empty(Directory.EnumerateFiles(_root, "handler-canceled.sparse.img.tmp-*"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}