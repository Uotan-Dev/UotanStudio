using System.IO.Compression;
using UotanStudio.Comm.Protocols.Fastboot.Images;
using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.VendorFirmware.HarmonyOS;
using UotanStudio.Services.Firmware;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Container structure analysis and targeted entry extraction tests for the new-project flow:
/// empty-directory validation, default-name probing, super/payload/update.bin/zip structure enumeration,
/// and per-selection extraction round-trips.
/// <para>新建项目流程的容器结构分析与定向提取测试：空目录校验、默认名探测、
/// super/payload/update.bin/zip 结构枚举以及按勾选提取的回环验证。</para>
/// </summary>
public sealed class ContainerStructureTests
{
    private static readonly SuperImageBuilder SuperBuilder = new();

    [Fact]
    public async Task SparseSuper_UsesLogicalPartitionsAndExtractsWithoutCopyingOriginal()
    {
        using var temp = new TempDirectory();
        var raw = Path.Combine(temp.Path, "source.raw");
        var image = SuperBuilder.Build([new SuperBuildPartition("vendor_a", "default", 8192),
            new SuperBuildPartition("vendor_b", "default", 4096)], 8 * 1024 * 1024, 65536, 2, 4096);
        File.WriteAllBytes(raw, image);
        var metadata = MetadataReader.ReadFromImageFile(raw);
        var extent = metadata.Extents[(int)metadata.Partitions[0].FirstExtentIndex];
        var expected = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
        expected.CopyTo(image, checked((int)extent.TargetData * 512));
        File.WriteAllBytes(raw, image);
        var sparse = Path.Combine(temp.Path, "renamed.img");
        UotanStudio.Firmware.BlockImage.Sparse.SparseImageConverter.ConvertRawToSparse(raw, sparse);

        UotanStudio.Firmware.Application.Formats.FirmwareFormatRegistry.RegisterAll();
        Assert.IsType<LpFormatHandler>(UotanStudio.Firmware.Core.FormatDetector.Detect(sparse));
        Assert.Equal("super", ImageInputService.DetectFormat(sparse));
        Assert.Equal(ContainerKind.Super, ContainerStructureAnalyzer.Analyze(sparse).Kind);
        var sourceSettings = SourceContainerMetadataService.Capture(sparse, ContainerKind.Super);
        Assert.Equal("SPARSE", sourceSettings.Storage);
        Assert.Equal("AB", sourceSettings.DeviceType);
        Assert.Equal((uint)2, sourceSettings.MetadataSlots);

        var output = Path.Combine(temp.Path, "project", "images");
        var extracted = await ContainerEntryExtractor.ExtractAsync(sparse, ["vendor_a"],
            cancellationToken: TestContext.Current.CancellationToken, outputDirectory: output);
        Assert.Equal(expected, File.ReadAllBytes(Assert.Single(extracted.OutputPaths)));
        Assert.Single(Directory.GetFiles(output));
        Assert.False(File.Exists(Path.Combine(output, "renamed.img")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "vendor_a.img")));
    }

    [Fact]
    public void SuperNameWithoutLpMetadata_IsNotSuper()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("super.img", new byte[8192]);
        Assert.False(new LpFormatHandler().CanHandle(path));
        Assert.Equal("raw", ImageInputService.DetectFormat(path));
    }

    [Fact]
    public async Task CopyCancellation_DoesNotPublishPartialFileAndReportsStart()
    {
        using var temp = new TempDirectory();
        var source = temp.WriteBytes("source.img", new byte[1024 * 1024]);
        var destination = Path.Combine(temp.Path, "destination.img");
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var reports = new List<double>();
        var progress = new InlineProgress(value => { reports.Add(value); cancelled.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UotanStudio.Projects.ProjectFiles.CopyAsync(source, destination,
                cancellationToken: cancelled.Token, progress: progress));
        Assert.Equal([0d], reports);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.copy-*"));
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [Fact]
    public async Task TruncatedSuper_DoesNotPublishPartialPartition()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("super.img", SuperBuilder.Build(
            [new SuperBuildPartition("vendor", "default", 2 * 1024 * 1024)],
            8 * 1024 * 1024, 65536, 2, 4096));
        using (var file = File.OpenWrite(path)) file.SetLength(1024 * 1024);
        var output = Path.Combine(temp.Path, "output");
        await Assert.ThrowsAsync<InvalidDataException>(() => ContainerEntryExtractor.ExtractAsync(path, ["vendor"],
            cancellationToken: TestContext.Current.CancellationToken, outputDirectory: output));
        Assert.Empty(Directory.GetFiles(output));
    }

    [Fact]
    public void IsDirectoryEmptyOrMissing_MissingOrEmpty_True()
    {
        using var temp = new TempDirectory();
        Assert.True(NewProjectDirectoryHelper.IsDirectoryEmptyOrMissing(Path.Combine(temp.Path, "not-created")));
        Assert.True(NewProjectDirectoryHelper.IsDirectoryEmptyOrMissing(temp.Path));
    }

    [Fact]
    public void IsDirectoryEmptyOrMissing_NonEmptyOrBlank_False()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "file.txt"), "x");
        Assert.False(NewProjectDirectoryHelper.IsDirectoryEmptyOrMissing(temp.Path));
        Assert.False(NewProjectDirectoryHelper.IsDirectoryEmptyOrMissing(" "));
        Assert.False(NewProjectDirectoryHelper.IsDirectoryEmptyOrMissing(null));
    }

    [Fact]
    public void GetNextDefaultName_SkipsExistingDirectoriesAndReservedNames()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "NewProject1"));
        Assert.Equal("NewProject2", NewProjectDirectoryHelper.GetNextDefaultName(temp.Path));
        Assert.Equal("NewProject3", NewProjectDirectoryHelper.GetNextDefaultName(temp.Path, ["NewProject2"]));
        Assert.Equal("NewProject1", NewProjectDirectoryHelper.GetNextDefaultName(Path.Combine(temp.Path, "fresh")));
        Assert.Equal("NewProject2", NewProjectDirectoryHelper.GetNextDefaultName(
            Path.Combine(temp.Path, "fresh2"), ["NewProject1"]));
    }

    [Fact]
    public void Analyze_SuperImage_ListsLogicalPartitions()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "super.img");
        var image = SuperBuilder.Build(
        [
            new SuperBuildPartition("system", "default", 0x200000),
            new SuperBuildPartition("vendor", "default", 0x100000)
        ], 16 * 1024 * 1024, 64 * 1024, 2, 4096);
        File.WriteAllBytes(path, image);

        var structure = ContainerStructureAnalyzer.Analyze(path);
        Assert.Equal(ContainerKind.Super, structure.Kind);
        Assert.Equal(2, structure.Entries.Count);
        Assert.Equal("system", structure.Entries[0].Name);
        Assert.Equal(0x200000, structure.Entries[0].SizeBytes);
        Assert.Equal("vendor", structure.Entries[1].Name);
        Assert.Equal(0x100000, structure.Entries[1].SizeBytes);
        Assert.All(structure.Entries, entry => Assert.False(entry.FromPayload));
    }

    [Fact]
    public async Task Analyze_Payload_ListsManifestPartitions()
    {
        using var temp = new TempDirectory();
        var source = temp.WriteBytes("boot.img", new byte[4096]);
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new UotanStudio.Firmware.OtaDelta.Payload.Builder.PayloadBuilder())
        {
            await builder.AddPartition("boot", source).BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        var structure = ContainerStructureAnalyzer.Analyze(payload);
        Assert.Equal(ContainerKind.Payload, structure.Kind);
        var entry = Assert.Single(structure.Entries);
        Assert.Equal("boot", entry.Name);
        var settings = SourceContainerMetadataService.Capture(payload, ContainerKind.Payload);
        Assert.Equal((uint)4096, settings.BlockSize);
        Assert.Equal(["boot"], settings.PartitionNames);
        Assert.Equal([4096UL], settings.PartitionSizes);
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(settings,
            SourceContainerJsonContext.Default.DeliverySourceContainerSettings);
        var restored = System.Text.Json.JsonSerializer.Deserialize(json,
            SourceContainerJsonContext.Default.DeliverySourceContainerSettings);
        Assert.NotNull(restored);
        Assert.Equal(settings.PartitionNames, restored.PartitionNames);
        Assert.Equal(settings.PartitionSizes, restored.PartitionSizes);
        Assert.True(entry.FromPayload);
        Assert.Equal(4096, entry.SizeBytes);
    }

    [Fact]
    public async Task Capture_ZipPayload_ReadsOtaBuildIdentity()
    {
        using var temp = new TempDirectory();
        var source = temp.WriteBytes("boot.img", new byte[4096]);
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new UotanStudio.Firmware.OtaDelta.Payload.Builder.PayloadBuilder())
        {
            await builder.AddPartition("boot", source).BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        var zipPath = Path.Combine(temp.Path, "ota.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(payload, "payload.bin");
            var metadata = archive.CreateEntry("META-INF/com/android/metadata");
            using var stream = metadata.Open();
            stream.Write("ota-type=AB\npost-build=Brand/Device/Device:14/UP1A/123:user/release-keys\npost-build-incremental=123\npost-sdk-level=34\n"u8);
        }

        var settings = SourceContainerMetadataService.Capture(zipPath, ContainerKind.Zip);
        Assert.Equal("Brand/Device/Device:14/UP1A/123:user/release-keys", settings.BuildFingerprint);
        Assert.Equal("123", settings.Incremental);
        Assert.Equal((uint)34, settings.SdkLevel);
        Assert.Equal("", SourceContainerMetadataService.Capture(payload, ContainerKind.Payload).BuildFingerprint);
    }

    [Fact]
    public void LegacySourceSettings_MissingOptionalStrings_DeserializeToEmpty()
    {
        using var temp = new TempDirectory();
        var path = SourceContainerMetadataService.GetPath(temp.Path, "super");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "{\"SourceName\":\"super.img\",\"Kind\":\"super\",\"GroupNames\":[],\"GroupSizes\":[],\"PartitionNames\":[],\"PartitionSizes\":[],\"OriginalStructures\":[]}");
        var settings = SourceContainerMetadataService.Read(temp.Path, "super");
        Assert.NotNull(settings);
        Assert.Equal("", settings!.DeviceName);
        Assert.Equal("", settings.BuildFingerprint);
        Assert.Equal("", settings.Incremental);
        Assert.Equal("", settings.SecurityPatchLevel);
        Assert.Equal("RAW", settings.Storage);
        Assert.Equal("Unknown", settings.DeviceType);
    }

    [Fact]
    public void Analyze_ZipWithoutPayload_EnumeratesImageMembersOnly()
    {
        using var temp = new TempDirectory();
        var zipPath = Path.Combine(temp.Path, "rom.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var image = archive.CreateEntry("firmware/system.img");
            using (var stream = image.Open())
            {
                stream.Write(new byte[1024]);
            }

            var text = archive.CreateEntry("META-INF/info.txt");
            using (var textStream = text.Open())
            {
                textStream.Write("hello"u8.ToArray());
            }
        }

        var structure = ContainerStructureAnalyzer.Analyze(zipPath);
        Assert.Equal(ContainerKind.Zip, structure.Kind);
        var entry = Assert.Single(structure.Entries);
        Assert.Equal("firmware/system.img", entry.Name);
        Assert.False(entry.FromPayload);
    }

    [Theory]
    [InlineData("payload.bin")]
    [InlineData("ota/PAYLOAD.BIN")]
    public async Task Analyze_ZipWithPayload_AppendsPayloadPartitions(string memberName)
    {
        using var temp = new TempDirectory();
        var source = temp.WriteBytes("boot.img", new byte[4096]);
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new UotanStudio.Firmware.OtaDelta.Payload.Builder.PayloadBuilder())
        {
            await builder.AddPartition("boot", source).BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        var zipPath = Path.Combine(temp.Path, "ota.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(payload, memberName);
            var image = archive.CreateEntry("vendor.img");
            using var stream = image.Open();
            stream.Write(new byte[2048]);
        }

        var structure = ContainerStructureAnalyzer.Analyze(zipPath);
        Assert.Equal(ContainerKind.Zip, structure.Kind);
        Assert.Equal(2, structure.Entries.Count);
        Assert.Equal("vendor.img", structure.Entries[0].Name);
        Assert.False(structure.Entries[0].FromPayload);
        Assert.Equal("boot", structure.Entries[1].Name);
        Assert.True(structure.Entries[1].FromPayload);
    }

    [Fact]
    public void Analyze_ZipWithPayloadSuffix_DoesNotHideImageMembers()
    {
        using var temp = new TempDirectory();
        var zip = Path.Combine(temp.Path, "images.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("other-payload.bin");
            archive.CreateEntry("images/boot.img");
        }
        var structure = ContainerStructureAnalyzer.Analyze(zip);
        Assert.Equal(ContainerKind.Zip, structure.Kind);
        Assert.Equal("images/boot.img", Assert.Single(structure.Entries).Name);
    }

    [Fact]
    public void Analyze_UpdateBin_ListsComponentsWithOutputExtensions()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
        [
            ("system", 0, [1, 2, 3, 4]),
            ("board_list", 0, "HI3516"u8.ToArray()),
            ("config", 1, [0x50, 0x4B])
        ]));

        var structure = ContainerStructureAnalyzer.Analyze(path);

        Assert.Equal(ContainerKind.UpdateBin, structure.Kind);
        Assert.Equal(3, structure.Entries.Count);
        Assert.Equal("system", structure.Entries[0].Name);
        Assert.Equal("img", structure.Entries[0].Format);
        Assert.Equal(4, structure.Entries[0].SizeBytes);
        Assert.False(structure.Entries[0].FromPayload);
        Assert.Equal("board_list", structure.Entries[1].Name);
        Assert.Equal("list", structure.Entries[1].Format);
        Assert.Equal("config", structure.Entries[2].Name);
        Assert.Equal("zip", structure.Entries[2].Format);
    }

    [Fact]
    public async Task Extract_UpdateBinSelection_WritesSelectedComponentsWithoutMetadataFile()
    {
        using var temp = new TempDirectory();
        byte[] system = [1, 2, 3, 4, 5];
        byte[] board = "HI3516"u8.ToArray();
        var path = temp.WriteBytes("update.bin", HarmonyUpdateBinFixture.Build(HarmonyUpdateBinLayout.L2,
        [
            ("system", 0, system),
            ("vendor", 0, [6, 7]),
            ("board_list", 0, board)
        ]));
        var output = Path.Combine(temp.Path, "project", "images");

        var result = await ContainerEntryExtractor.ExtractAsync(path, ["system", "board_list"],
            cancellationToken: TestContext.Current.CancellationToken, outputDirectory: output);

        Assert.Equal(["system.img", "BOARD.list"], result.Entries);
        Assert.Equal(system, await File.ReadAllBytesAsync(Path.Combine(output, "system.img"),
            TestContext.Current.CancellationToken));
        Assert.Equal(board, await File.ReadAllBytesAsync(Path.Combine(output, "BOARD.list"),
            TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(output, "vendor.img")));
        Assert.False(File.Exists(Path.Combine(output, RepackMetadata.FileName)));
    }

    [Fact]
    public void Analyze_NonContainerFile_ReturnsEmpty()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("random.bin", new byte[8192]);
        var structure = ContainerStructureAnalyzer.Analyze(path);
        Assert.Equal(ContainerKind.None, structure.Kind);
        Assert.Empty(structure.Entries);
    }

    [Fact]
    public async Task Extract_SuperSelection_WritesSelectedPartitionsOnly()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "super.img");
        var image = SuperBuilder.Build(
        [
            new SuperBuildPartition("system", "default", 0x200000),
            new SuperBuildPartition("vendor", "default", 0x100000)
        ], 16 * 1024 * 1024, 64 * 1024, 2, 4096);
        File.WriteAllBytes(path, image);
        var metadata = MetadataReader.ReadFromImageFile(path);
        var bytes = File.ReadAllBytes(path);
        var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var partition in metadata.Partitions)
        {
            ulong sectors = 0;
            for (var i = 0; i < partition.NumExtents; i++)
            {
                sectors += metadata.Extents[(int)partition.FirstExtentIndex + i].NumSectors;
            }

            var content = new byte[checked((int)sectors * 512)];
            new Random(partition.NameString.Length ^ 7).NextBytes(content);
            expected[partition.NameString] = content;
            var offset = 0;
            for (var i = 0; i < partition.NumExtents; i++)
            {
                var extent = metadata.Extents[(int)partition.FirstExtentIndex + i];
                var start = (int)(extent.TargetData * 512);
                Array.Copy(content, offset, bytes, start, (int)(extent.NumSectors * 512));
                offset += (int)(extent.NumSectors * 512);
            }
        }

        File.WriteAllBytes(path, bytes);

        var result = await ContainerEntryExtractor.ExtractAsync(path, ["vendor"], cancellationToken: TestContext.Current.CancellationToken);
        var outputPath = Assert.Single(result.OutputPaths);
        Assert.Equal("vendor.img", Path.GetFileName(outputPath));
        Assert.Equal(expected["vendor"], File.ReadAllBytes(outputPath));
        Assert.False(File.Exists(Path.Combine(temp.Path, "system.img")));
    }

    [Fact]
    public async Task Extract_PayloadSelection_ExtractsSelectedPartitions()
    {
        using var temp = new TempDirectory();
        var content = new byte[4096];
        new Random(11).NextBytes(content);
        var source = temp.WriteBytes("boot.img", content);
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new UotanStudio.Firmware.OtaDelta.Payload.Builder.PayloadBuilder())
        {
            await builder.AddPartition("boot", source).BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        var result = await ContainerEntryExtractor.ExtractAsync(payload, ["boot"], cancellationToken: TestContext.Current.CancellationToken);
        var outputPath = Assert.Single(result.OutputPaths);
        Assert.Equal("boot.img", Path.GetFileName(outputPath));
        Assert.Equal(content, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public async Task Extract_ZipMembers_WritesEntryContentWithOriginalName()
    {
        using var temp = new TempDirectory();
        var zipPath = Path.Combine(temp.Path, "rom.zip");
        var content = new byte[2048];
        new Random(13).NextBytes(content);
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("images/system.img");
            using var stream = entry.Open();
            stream.Write(content);
        }

        var result = await ContainerEntryExtractor.ExtractAsync(zipPath, ["images/system.img"], cancellationToken: TestContext.Current.CancellationToken);
        var outputPath = Assert.Single(result.OutputPaths);
        Assert.Equal("system.img", Path.GetFileName(outputPath));
        Assert.Equal(content, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public async Task Extract_ZipPayloadOnly_ExtractsPartitionsFromInnerPayload()
    {
        using var temp = new TempDirectory();
        var source = temp.WriteBytes("boot.img", new byte[4096]);
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new UotanStudio.Firmware.OtaDelta.Payload.Builder.PayloadBuilder())
        {
            await builder.AddPartition("boot", source).BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        var zipPath = Path.Combine(temp.Path, "ota.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(payload, "payload.bin");
        }

        var result = await ContainerEntryExtractor.ExtractAsync(zipPath, ["boot"], fromPayloadOnly: true, cancellationToken: TestContext.Current.CancellationToken);
        var outputPath = Assert.Single(result.OutputPaths);
        Assert.Equal("boot.img", Path.GetFileName(outputPath));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(outputPath));
    }

    [Fact]
    public async Task Extract_EmptySelectionOrMissingFile_ReturnsEmpty()
    {
        Assert.Equal(ContainerExtractionResult.Empty,
            await ContainerEntryExtractor.ExtractAsync("", [], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(ContainerExtractionResult.Empty,
            await ContainerEntryExtractor.ExtractAsync(
                "Z:/definitely/missing/super.img", ["system"], cancellationToken: TestContext.Current.CancellationToken));
    }
}