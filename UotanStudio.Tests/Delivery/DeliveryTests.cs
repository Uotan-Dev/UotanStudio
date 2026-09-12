using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using UotanStudio.Services.Delivery;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;
using UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class DeliveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "delivery-tests-" + Guid.NewGuid().ToString("N"));
    public DeliveryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private DeliveryFile Input(string name = "boot", int size = 8192)
    {
        var path = Path.Combine(_root, name + ".img");
        File.WriteAllBytes(path, Enumerable.Range(0, size).Select(i => (byte)(i * 37)).ToArray());
        return new() { SourcePath = path, FileName = name + ".img", Partition = name, SourceLength = size };
    }
    private sealed class NoTools : IDeliveryTools
    {
        public Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PayloadExportsLogicalImagesAndVerifiableProperties(bool sparse)
    {
        var file = Input("system");
        var expected = File.ReadAllBytes(file.SourcePath);
        if (sparse)
        {
            var path = Path.Combine(_root, "sparse.img");
            SparseImageConverter.ConvertRawToSparse(file.SourcePath, path);
            file = file with { SourcePath = path, SourceLength = new FileInfo(path).Length };
        }
        var config = Path.Combine(_root, "dynamic.txt");
        var plan = new DeliveryPlan { PackPayload = true, OrganizeDirectory = false, Container = [file with { IncludeInSuper = true }],
            Payload = new() { DynamicPartitionsFile = config }, OutputDirectory = Path.Combine(_root, "payload-out") };
        File.WriteAllText(config, DeliveryPayloadBuilder.GenerateDynamicLayout(plan));
        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: TestContext.Current.CancellationToken);
        var unpacked = Path.Combine(_root, "ota-content");
        ZipFile.ExtractToDirectory(Path.Combine(plan.OutputDirectory, "ota.zip"), unpacked);
        var payload = Path.Combine(unpacked, "payload.bin");
        var bytes = File.ReadAllBytes(payload);
        var properties = File.ReadAllLines(Path.Combine(unpacked, "payload_properties.txt"))
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(bytes)), properties["FILE_HASH"]);
        Assert.Equal(bytes.Length.ToString(), properties["FILE_SIZE"]);
        var metadataSize = int.Parse(properties["METADATA_SIZE"]);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(bytes.AsSpan(0, metadataSize))), properties["METADATA_HASH"]);
        var manifest = UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto.DeltaArchiveManifest.Parser.ParseFrom(bytes.AsSpan(24, metadataSize - 24).ToArray());
        Assert.NotNull(manifest.DynamicPartitionMetadata);
        Assert.Equal("system", Assert.Single(Assert.Single(manifest.DynamicPartitionMetadata.Groups).PartitionNames));
        var extracted = Path.Combine(_root, "extracted"); Directory.CreateDirectory(extracted);
        UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.PayloadParser.Extract(payload, ["system"], extracted);
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(extracted, "system.img")));
    }

    [Fact]
    public async Task InvalidPayloadConfigurationPublishesNothing()
    {
        var file = Input("system");
        var config = Path.Combine(_root, "dynamic.txt");
        File.WriteAllText(config, "super_partition_groups=main\nsuper_main_group_size=8192\nsuper_main_partition_list=unknown\n");
        var plan = new DeliveryPlan { PackPayload = true, OrganizeDirectory = false, Container = [file],
            Payload = new() { DynamicPartitionsFile = config }, OutputDirectory = Path.Combine(_root, "out") };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(plan.OutputDirectory));
        Assert.Empty(Directory.GetDirectories(_root, ".uotan-delivery-*"));
        Assert.True(File.Exists(file.SourcePath));
        Assert.Throws<NotSupportedException>(() => DeliveryValidation.Validate(plan with { Payload = new() { SignatureFile = "signature" } }));
    }

    [Fact]
    public void PayloadConfirmationDoesNotGenerateFastbootPayloadCommands()
    {
        var model = new DeliveryViewModel(); model.SetMode(true, false, true);
        model.SelectSources([Input("system")]); model.Confirm(new());
        Assert.Equal("payload", Assert.Single(model.Output).PartitionName);
        var plan = new DeliveryPlan { PackPayload = true, ContainerConfirmed = true, Container = [Input("vendor")],
            Files = [new() { Generated = true, FileName = "ota.zip", Partition = "payload" }] };
        Assert.Empty(DeliveryScriptGenerator.Generate(plan));
        var scripts = DeliveryScriptGenerator.Generate(plan with { Files = [.. plan.Files, Input()] });
        Assert.Equal("boot images/boot.img\r\n", scripts["flash.txt"]);
    }

    [Fact]
    public void ConfirmedContainerKeepsMembershipAndNewSelectionsGoToOutput()
    {
        var system = Input("system"); var boot = Input();
        var model = new DeliveryViewModel();
        model.SetMode(true, true);
        model.SelectSources([system]);
        model.Confirm(new());
        model.SelectSources([system, boot]);
        Assert.Single(model.Container);
        Assert.Equal(2, model.Output.Count);
        Assert.Equal("boot", model.Output[1].PartitionName);
        model.SelectSources([boot]);
        Assert.Single(model.Container);
        model.ReturnToContainer();
        model.SelectSources([system, boot]);
        model.SelectSources([boot]);
        Assert.Empty(model.Container);
    }

    [Theory]
    [InlineData(true, true, false, false, true, false)]
    [InlineData(false, true, false, true, true, false)]
    [InlineData(true, true, false, true, false, true)]
    [InlineData(true, false, true, true, true, false)]
    [InlineData(true, true, false, true, false, false)]
    [InlineData(true, false, true, true, false, false)]
    [InlineData(true, false, true, false, false, true)]
    [InlineData(false, false, true, true, false, true)]
    [InlineData(true, false, false, true, true, false)]
    [InlineData(true, false, false, true, false, true)]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(false, false, false, true, false, false)]
    public void ModeChangesClearMembershipAndAllowReselectingSources(
        bool directory, bool super, bool payload, bool nextDirectory, bool nextSuper, bool nextPayload)
    {
        var system = Input("system"); var boot = Input();
        var model = new DeliveryViewModel();
        model.SetMode(directory, super, payload);
        model.SelectSources([system]);
        if (super || payload)
        {
            model.AddZero("vendor");
            model.Confirm(new());
        }
        model.SelectSources([system, boot]);
        var original = model.Container.Concat(model.Output).First(i => i.FilePath == system.SourcePath);
        original.PartitionName = "edited_system";

        Assert.True(model.SetMode(nextDirectory, nextSuper, nextPayload));
        Assert.False(model.Confirmed);
        Assert.Empty(model.Container);
        Assert.Empty(model.Output);
        Assert.True(File.Exists(system.SourcePath));
        Assert.True(File.Exists(boot.SourcePath));

        model.SelectSources([system]);
        var selected = Assert.Single(nextSuper || nextPayload ? model.Container : model.Output);
        Assert.Equal(system.Partition, selected.PartitionName);
        Assert.NotSame(original, selected);
    }

    [Fact]
    public void SameModeRefreshPreservesRestoredMembershipAndConfirmation()
    {
        var plan = new DeliveryPlan
        {
            PackPayload = true, ContainerConfirmed = true, Container = [Input("system")],
            Files = [new() { Generated = true, Partition = "payload", FileName = "payload.bin" }, Input()]
        };
        var model = new DeliveryViewModel();
        model.Reset(plan);
        var container = model.Container.ToArray();
        var output = model.Output.ToArray();

        Assert.False(model.SetMode(true, false, true));
        model.SelectSources(plan.Container.Concat(plan.Files.Where(f => !f.Generated)).ToArray());

        Assert.True(model.Confirmed);
        Assert.Equal(container, model.Container);
        Assert.Equal(output, model.Output);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SuperWritesPartitionAtDeclaredAbsoluteOffset(bool sparseInput, bool sparseOutput)
    {
        var file = Input("system");
        var expected = File.ReadAllBytes(file.SourcePath);
        if (sparseInput)
        {
            var path = Path.Combine(_root, "sparse.img");
            SparseImageConverter.ConvertRawToSparse(file.SourcePath, path);
            file = file with { SourcePath = path, SourceLength = new FileInfo(path).Length };
        }
        var output = Path.Combine(_root, "super.img");
        await DeliverySuperBuilder.BuildAsync([file], new() { DeviceSize = 8 * 1024 * 1024, AutoZero = true, Sparse = sparseOutput }, output, token: TestContext.Current.CancellationToken);
        using var raw = new MemoryStream();
        using (var source = SparseReader.ImportAuto(output)) source.WriteRawToStream(raw);
        raw.Position = 0;
        var metadata = MetadataReader.ReadFromImageStream(raw);
        Assert.Equal(2, metadata.Partitions.Count);
        var extent = metadata.Extents[(int)metadata.Partitions[0].FirstExtentIndex];
        raw.Position = checked((long)extent.TargetData * 512);
        var actual = new byte[expected.Length];
        raw.ReadExactly(actual);
        Assert.Equal(expected, actual);
        Assert.Equal(0u, metadata.Partitions[1].NumExtents);
    }

    [Fact]
    public async Task BuildSuperAcceptsEmptyBSlotImageWithAutoZero()
    {
        // The empty B-slot image selected from the source list is matched by file name and becomes a zero-size partition.
        // 勾选的空 B 槽镜像按文件名匹配并作为零大小分区参与打包。
        var a = Input("my_bigball_a");
        var b = Input("my_bigball_b", 0);
        var output = Path.Combine(_root, "super.img");
        await DeliverySuperBuilder.BuildAsync([a, b], new() { DeviceSize = 8 * 1024 * 1024, AutoZero = true }, output, token: TestContext.Current.CancellationToken);
        using var raw = new MemoryStream();
        using (var source = SparseReader.ImportAuto(output)) source.WriteRawToStream(raw);
        raw.Position = 0;
        var metadata = MetadataReader.ReadFromImageStream(raw);
        Assert.Equal(2, metadata.Partitions.Count);
        Assert.Equal(0u, metadata.Partitions[1].NumExtents);
    }

    [Fact]
    public async Task EmptySuperIsStandaloneAndCancellationPublishesNothing()
    {
        var output = Path.Combine(_root, "empty.img");
        await DeliverySuperBuilder.BuildAsync([new() { Partition = "system" }], new() { DeviceSize = 8 * 1024 * 1024, Empty = true }, output, token: TestContext.Current.CancellationToken);
        Assert.Equal(0x616c4467u, BitConverter.ToUInt32(File.ReadAllBytes(output)));
        Assert.True(new FileInfo(output).Length < 128 * 1024);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = Path.Combine(_root, "cancelled.img");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeliverySuperBuilder.BuildAsync([Input()], new(), cancelled, token: cancellation.Token));
        Assert.False(File.Exists(cancelled));
    }

    [Fact]
    public async Task DirectoryExportPreservesInputsAndRejectsConflicts()
    {
        var file = Input(); var sourceHash = SHA256.HashData(File.ReadAllBytes(file.SourcePath));
        var plan = new DeliveryPlan { Files = [file], Txt = false, OutputDirectory = Path.Combine(_root, "out") };
        var service = new DeliveryOutputService(new NoTools());
        await service.ExportAsync(plan, token: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(plan.OutputDirectory, "images", "boot.img")));
        Assert.True(File.Exists(Path.Combine(plan.OutputDirectory, "checksums.sha256")));
        await Assert.ThrowsAsync<IOException>(() => service.ExportAsync(plan, token: TestContext.Current.CancellationToken));
        Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(file.SourcePath)));
        Assert.Empty(Directory.GetDirectories(_root, ".uotan-delivery-*"));
    }

    [Fact]
    public async Task PlanRoundTripKeepsDraftsAndRebasesSources()
    {
        var plan = new DeliveryPlan
        {
            Files = [Input()], OutputDirectory = Path.Combine(_root, "out"), Scripts = new() { ["flash.txt"] = "manual edits\n" },
            GeneratedScripts = new() { ["flash.txt"] = "boot images/boot.img\r\n" }, OpenScripts = ["flash.txt"], ScriptCarets = new() { ["flash.txt"] = 5 }
        };
        await DeliveryPlanStore.SaveAsync(_root, plan, TestContext.Current.CancellationToken);
        var restored = Assert.IsType<DeliveryPlan>(await DeliveryPlanStore.LoadAsync(_root, TestContext.Current.CancellationToken));
        Assert.Equal(plan.Files[0], restored.Files[0]);
        Assert.Equal("manual edits\n", restored.Scripts["flash.txt"]);
        Assert.Equal(plan.OutputDirectory, restored.OutputDirectory);
        Assert.Equal(plan.GeneratedScripts["flash.txt"], restored.GeneratedScripts["flash.txt"]);
        Assert.Equal(5, restored.ScriptCarets["flash.txt"]);
        Assert.Equal(DeliveryScriptGenerator.Fingerprint(plan), DeliveryScriptGenerator.Fingerprint(plan with { OpenScripts = [], ScriptCarets = [] }));
    }

    [Fact]
    public void LegacyVariantsAreCompleteAndContainOneImagePerTarget()
    {
        var plan = new DeliveryPlan { TxtVariants = true, Files = [Input(), Input("init_boot") with { Optional = true, Default = true }, Input("init_boot_ksu") with { Partition = "init_boot", Optional = true }] };
        var scripts = DeliveryScriptGenerator.Generate(plan);
        Assert.Equal("boot images/boot.img\r\ninit_boot images/init_boot.img\r\n", scripts["flash.txt"]);
        Assert.Equal("boot images/boot.img\r\ninit_boot images/init_boot_ksu.img\r\n", scripts["flash-init_boot_ksu.txt"]);
        Assert.NotEqual(DeliveryScriptGenerator.Fingerprint(plan), DeliveryScriptGenerator.Fingerprint(plan with { Slot = "all" }));
        Assert.Throws<InvalidDataException>(() => DeliveryValidation.Validate(plan with { Files = [plan.Files[1], plan.Files[2] with { Default = true }] }));
    }

    [Fact]
    public async Task DownloadRechecksCacheAndRejectsCorruption()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var handler = new DownloadHandler(data);
        using var http = new HttpClient(handler);
        var service = new DeliveryToolService(http, Path.Combine(_root, "cache"));
        var package = new DeliveryToolPackage("sample", "https://example.org/tools.zip", Convert.ToHexString(SHA256.HashData(data)), "");
        var path = await service.AcquireAsync(package, null, TestContext.Current.CancellationToken);
        await service.AcquireAsync(package, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Requests);
        File.WriteAllText(path, "corrupt");
        await service.AcquireAsync(package, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Requests);
        await Assert.ThrowsAsync<IOException>(() => service.AcquireAsync(package with { Name = "bad", Sha256 = new string('0', 64) }, null, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.download-*"));
    }
    private sealed class DownloadHandler(byte[] data) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }); }
    }

    [Fact]
    public async Task ToolArchiveCannotEscapeDestination()
    {
        var archive = Path.Combine(_root, "bad.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("platform-tools/../../escaped").Open()) stream.WriteByte(1);
        await Assert.ThrowsAsync<InvalidDataException>(() => DeliveryToolService.ExtractAsync(archive, "platform-tools/", Path.Combine(_root, "tools"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_root, "escaped")));
    }

    [Fact]
    public async Task CancellingDuringCopyLeavesExistingDirectoryUntouched()
    {
        var file = Input(size: 1024 * 1024);
        var output = Path.Combine(_root, "out"); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "keep.txt"), "keep");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<DeliveryProgress>(p => { if (p.Percent > 55) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeliveryOutputService(new NoTools()).ExportAsync(
            new() { Files = [file], Txt = false, OutputDirectory = output }, progress, cancellation.Token));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(output, "keep.txt")));
        Assert.Single(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(_root, ".uotan-delivery-*"));
    }

    [Fact]
    public async Task ZstdSuperDecompressesToSameRawImage()
    {
        var file = Input("system");
        var options = new DeliverySuperOptions { DeviceSize = 8 * 1024 * 1024 };
        var raw = Path.Combine(_root, "raw.img");
        await DeliverySuperBuilder.BuildAsync([file], options, raw, token: TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "compressed");
        await new DeliveryOutputService(new NoTools()).ExportAsync(new()
        {
            OrganizeDirectory = false, PackSuper = true, Super = options with { Zstd = true }, Container = [file], OutputDirectory = output
        }, token: TestContext.Current.CancellationToken);
        using var compressed = File.OpenRead(Path.Combine(output, "super.zst"));
        using var decoded = new MemoryStream();
        UotanStudio.Firmware.CrossCutting.Compress.Compressors.ZstdCompressor.Decompress(compressed, decoded);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(raw)), SHA256.HashData(decoded.ToArray()));
    }
}
