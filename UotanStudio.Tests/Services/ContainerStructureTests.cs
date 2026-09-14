using System.Buffers.Binary;
using System.IO.Compression;
using UotanStudio.Comm.Protocols.Fastboot.Utils;
using UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Firmware;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Container structure analysis and targeted entry extraction tests for the new-project flow:
/// empty-directory validation, default-name probing, super/payload/zip structure enumeration, and
/// per-selection extraction round-trips.
/// <para>新建项目流程的容器结构分析与定向提取测试：空目录校验、默认名探测、
/// super/payload/zip 结构枚举以及按勾选提取的回环验证。</para>
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
        UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse.SparseImageConverter.ConvertRawToSparse(raw, sparse);

        UotanStudio.Firmware.SharedKernel.FirmwareFormatRegistry.RegisterAll();
        Assert.IsType<LpFormatHandler>(UotanStudio.Firmware.SharedKernel.FormatDetector.Detect(sparse));
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
            UotanStudio.Services.Projects.ProjectFiles.CopyAsync(source, destination,
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

    // ============== IsDirectoryEmptyOrMissing ==============

    [Fact]
    public void IsDirectoryEmptyOrMissing_MissingOrEmpty_True()
    {
        using var temp = new TempDirectory();
        Assert.True(ContainerStructureAnalyzer.IsDirectoryEmptyOrMissing(Path.Combine(temp.Path, "not-created")));
        Assert.True(ContainerStructureAnalyzer.IsDirectoryEmptyOrMissing(temp.Path));
    }

    [Fact]
    public void IsDirectoryEmptyOrMissing_NonEmptyOrBlank_False()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "file.txt"), "x");
        Assert.False(ContainerStructureAnalyzer.IsDirectoryEmptyOrMissing(temp.Path));
        Assert.False(ContainerStructureAnalyzer.IsDirectoryEmptyOrMissing(" "));
        Assert.False(ContainerStructureAnalyzer.IsDirectoryEmptyOrMissing(null));
    }

    // ============== GetNextDefaultName ==============

    [Fact]
    public void GetNextDefaultName_SkipsExistingDirectoriesAndReservedNames()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "NewProject1"));
        Assert.Equal("NewProject2", ContainerStructureAnalyzer.GetNextDefaultName(temp.Path));
        Assert.Equal("NewProject3", ContainerStructureAnalyzer.GetNextDefaultName(temp.Path, ["NewProject2"]));
        // 空目录且无占用：返回第一个名称。
        // Fresh directory with no reservations: the first name is returned.
        Assert.Equal("NewProject1", ContainerStructureAnalyzer.GetNextDefaultName(Path.Combine(temp.Path, "fresh")));
        // 空目录但名称被显式占用：跳到下一个。
        // Fresh directory but the name is explicitly reserved: skip to the next one.
        Assert.Equal("NewProject2", ContainerStructureAnalyzer.GetNextDefaultName(
            Path.Combine(temp.Path, "fresh2"), ["NewProject1"]));
    }

    // ============== Analyze: super ==============

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

    // ============== Analyze: payload ==============

    [Fact]
    public async Task Analyze_Payload_ListsManifestPartitions()
    {
        using var temp = new TempDirectory();
        var source = temp.WriteBytes("boot.img", new byte[4096]);
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.PayloadBuilder())
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

    // ============== Analyze: zip ==============

    [Fact]
    public void Analyze_ZipWithoutPayload_EnumeratesImageMembersOnly()
    {
        using var temp = new TempDirectory();
        var zipPath = Path.Combine(temp.Path, "rom.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
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
        using (var builder = new UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.PayloadBuilder())
        {
            await builder.AddPartition("boot", source).BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        var zipPath = Path.Combine(temp.Path, "ota.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
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
    public void Analyze_NonContainerFile_ReturnsEmpty()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("random.bin", new byte[8192]);
        var structure = ContainerStructureAnalyzer.Analyze(path);
        Assert.Equal(ContainerKind.None, structure.Kind);
        Assert.Empty(structure.Entries);
    }

    // ============== Extract: super ==============

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
        // 构建器数据区全零；按 LP extent 向分区数据区回填随机内容以验证提取正确。
        // The builder leaves data areas zeroed; fill each partition's extents with random content
        // via LP metadata so the extracted bytes can be verified.
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

    // ============== Extract: payload ==============

    [Fact]
    public async Task Extract_PayloadSelection_ExtractsSelectedPartitions()
    {
        using var temp = new TempDirectory();
        var content = new byte[4096];
        new Random(11).NextBytes(content);
        var source = temp.WriteBytes("boot.img", content);
        var payload = Path.Combine(temp.Path, "payload.bin");
        using (var builder = new UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.PayloadBuilder())
        {
            await builder.AddPartition("boot", source).BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        var result = await ContainerEntryExtractor.ExtractAsync(payload, ["boot"], cancellationToken: TestContext.Current.CancellationToken);
        var outputPath = Assert.Single(result.OutputPaths);
        Assert.Equal("boot.img", Path.GetFileName(outputPath));
        Assert.Equal(content, File.ReadAllBytes(outputPath));
    }

    // ============== Extract: zip ==============

    [Fact]
    public async Task Extract_ZipMembers_WritesEntryContentWithOriginalName()
    {
        using var temp = new TempDirectory();
        var zipPath = Path.Combine(temp.Path, "rom.zip");
        var content = new byte[2048];
        new Random(13).NextBytes(content);
        using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
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
        using (var builder = new UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.PayloadBuilder())
        {
            await builder.AddPartition("boot", source).BuildAsync(payload, TestContext.Current.CancellationToken);
        }

        var zipPath = Path.Combine(temp.Path, "ota.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(payload, "payload.bin");
        }

        var result = await ContainerEntryExtractor.ExtractAsync(zipPath, ["boot"], fromPayloadOnly: true, cancellationToken: TestContext.Current.CancellationToken);
        var outputPath = Assert.Single(result.OutputPaths);
        Assert.Equal("boot.img", Path.GetFileName(outputPath));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(outputPath));
    }

    // ============== Extract: validation ==============

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