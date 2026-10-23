using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using UotanStudio.Delivery;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class DeliveryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private string Root => _temp.Path;
    public void Dispose() => _temp.Dispose();
    private DeliveryFile Input(string name = "boot", int size = 8192)
    {
        var path = Path.Combine(Root, name + ".img");
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
            var path = Path.Combine(Root, "sparse.img");
            SparseImageConverter.ConvertRawToSparse(file.SourcePath, path);
            file = file with { SourcePath = path, SourceLength = new FileInfo(path).Length };
        }
        var config = Path.Combine(Root, "dynamic.txt");
        var plan = new DeliveryPlan
        {
            PackPayload = true,
            OrganizeDirectory = true,
            ContainerConfirmed = true,
            Container = [file with { IncludeInSuper = true }],
            Super = new() { DeviceSize = 8 * 1024 * 1024 },
            Payload = new() { DynamicPartitionsFile = config },
            OutputDirectory = Path.Combine(Root, "payload-out"),
            Files = [new() { Generated = true, Partition = "payload", FileName = "ota.zip" }],
            Txt = false
        };
        File.WriteAllText(config, DeliveryPayloadBuilder.GenerateDynamicLayout(plan));
        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: TestContext.Current.CancellationToken);
        var unpacked = Path.Combine(Root, "ota-content");
        ZipFile.ExtractToDirectory(Path.Combine(plan.OutputDirectory, "ota.zip"), unpacked);
        var payload = Path.Combine(unpacked, "payload.bin");
        var bytes = File.ReadAllBytes(payload);
        var properties = File.ReadAllLines(Path.Combine(unpacked, "payload_properties.txt"))
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(bytes)), properties["FILE_HASH"]);
        Assert.Equal(bytes.Length.ToString(), properties["FILE_SIZE"]);
        var metadataSize = int.Parse(properties["METADATA_SIZE"]);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(bytes.AsSpan(0, metadataSize))), properties["METADATA_HASH"]);
        var manifest = UotanStudio.Firmware.OtaDelta.Payload.Proto.DeltaArchiveManifest.Parser.ParseFrom(bytes.AsSpan(24, metadataSize - 24).ToArray());
        Assert.NotNull(manifest.DynamicPartitionMetadata);
        Assert.Equal("system", Assert.Single(Assert.Single(manifest.DynamicPartitionMetadata.Groups).PartitionNames));
        var extracted = Path.Combine(Root, "extracted"); Directory.CreateDirectory(extracted);
        UotanStudio.Firmware.OtaDelta.Payload.PayloadParser.Extract(payload, ["system"], extracted);
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(extracted, "system.img")));
    }

    [Fact]
    public async Task PayloadPreservesVendorPartitionNameWithHyphen()
    {
        var file = Input("vm-bootsys");
        var model = new DeliveryViewModel();
        model.SetMode(true, false, true);
        model.SelectSources([file]);
        var plan = new DeliveryPlan
        {
            PackPayload = true,
            OrganizeDirectory = true,
            ContainerConfirmed = true,
            Container = [file],
            OutputDirectory = Path.Combine(Root, "vendor-name-output"),
            Files = [new() { Generated = true, Partition = "payload", FileName = "ota.zip" }],
            Txt = false
        };
        model.Confirm(plan);
        Assert.True(model.Confirmed);
        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: TestContext.Current.CancellationToken);
        using var zip = ZipFile.OpenRead(Path.Combine(plan.OutputDirectory, "ota.zip"));
        var payload = Path.Combine(Root, "payload.bin");
        zip.GetEntry("payload.bin")!.ExtractToFile(payload);
        var metadata = UotanStudio.Firmware.OtaDelta.Payload.PayloadParser.GetPayloadMetadata(payload);
        var partition = Assert.Single(metadata.Manifest.Partitions);
        Assert.Equal("vm-bootsys", partition.PartitionName);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(file.SourcePath!)), partition.NewPartitionInfo!.Hash.ToByteArray());
        Assert.False(DeliveryValidation.IsPartition("vm-bootsys"));
    }

    [Theory]
    [InlineData("-boot")]
    [InlineData("../boot")]
    [InlineData("boot;reboot")]
    [InlineData("boot\n")]
    public void PayloadNameStillRejectsUnsafeValues(string name)
    {
        var file = Input() with { Partition = name };
        Assert.Throws<InvalidDataException>(() => DeliveryPayloadBuilder.Validate(new() { PackPayload = true, Container = [file] }));
    }

    [Fact]
    public async Task InvalidPayloadConfigurationPublishesNothing()
    {
        var file = Input("system");
        var config = Path.Combine(Root, "dynamic.txt");
        File.WriteAllText(config, "super_partition_groups=main\nsuper_main_group_size=8192\nsuper_main_partition_list=unknown\n");
        var plan = new DeliveryPlan
        {
            PackPayload = true,
            OrganizeDirectory = true,
            ContainerConfirmed = true,
            Container = [file],
            Payload = new() { DynamicPartitionsFile = config },
            OutputDirectory = Path.Combine(Root, "out"),
            Files = [new() { Generated = true, Partition = "payload", FileName = "ota.zip" }],
            Txt = false
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(plan.OutputDirectory));
        Assert.Empty(Directory.GetDirectories(Root, ".uotan-delivery-*"));
        Assert.True(File.Exists(file.SourcePath));
        Assert.Throws<NotSupportedException>(() => DeliveryValidation.Validate(plan with { Payload = new() { SignatureFile = "signature" } }));
    }

    [Fact]
    public void PayloadConfirmationDoesNotGenerateFastbootPayloadCommands()
    {
        var model = new DeliveryViewModel(); model.SetMode(true, false, true);
        var system = Input("system");
        model.SelectSources([system]); model.Confirm(new DeliveryPlan { PackPayload = true, Container = [system] });
        Assert.Equal("payload", Assert.Single(model.Output).PartitionName);
        var plan = new DeliveryPlan
        {
            PackPayload = true,
            ContainerConfirmed = true,
            Container = [Input("vendor")],
            Files = [new() { Generated = true, FileName = "ota.zip", Partition = "payload" }]
        };
        Assert.Empty(DeliveryScriptGenerator.Generate(plan));
        var scripts = DeliveryScriptGenerator.Generate(plan with { Files = [.. plan.Files, Input()] });
        Assert.Equal("boot images/boot.img\r\n", scripts["flash.txt"]);
    }

    [Fact]
    public void PayloadConfirmValidatesContainerAndPublishesGeneratedOta()
    {
        var model = new DeliveryViewModel();
        model.SetMode(true, false, true);
        var path = Path.Combine(Root, "boot_a.img");
        File.WriteAllBytes(path, new byte[4096]);
        model.SelectSources([new DeliveryFile { SourcePath = path, FileName = "boot_a.img", Partition = "boot_a", SourceLength = 4096 }]);
        var plan = new DeliveryPlan
        {
            PackPayload = true,
            Container = [.. model.Container.Select(i => i.Snapshot())],
            Payload = new() { Version = 2, MinorVersion = 0, BlockSize = 4096, Compression = "None", DynamicPartitionsFile = "" }
        };
        model.Confirm(plan);
        Assert.True(model.Confirmed);
        var generated = Assert.Single(model.Output);
        Assert.True(generated.Generated);
        Assert.Equal("ota.zip", generated.FileName);
        Assert.Equal("payload", generated.PartitionName);
    }

    [Fact]
    public void PayloadConfirmRejectsDuplicatePartitionNames()
    {
        var model = new DeliveryViewModel();
        model.SetMode(true, false, true);
        model.SelectSources([Input("boot_a"), Input("boot_a_copy") with { Partition = "boot_a" }]);
        var plan = new DeliveryPlan { PackPayload = true, Container = [.. model.Container.Select(i => i.Snapshot())] };
        Assert.ThrowsAny<InvalidDataException>(() => model.Confirm(plan));
        Assert.False(model.Confirmed);
        Assert.DoesNotContain(model.Output, i => i.Generated);
    }

    [Fact]
    public void PayloadModeDoesNotMaterializeZeroPartitionsButSuperModeDoes()
    {
        var payloadModel = new DeliveryViewModel();
        payloadModel.SetMode(true, false, true);
        payloadModel.AutoZero = true;
        payloadModel.SelectSources([Input("system_a")]);
        Assert.Single(payloadModel.Container);
        Assert.DoesNotContain(payloadModel.Container, i => i.PartitionName == "system_b");
        var superModel = new DeliveryViewModel();
        superModel.SetMode(true, true, false);
        superModel.AutoZero = true;
        superModel.SelectSources([Input("system_a")]);
        Assert.Contains(superModel.Container, i => i.PartitionName == "system_b" && i.IsAutoZero);
    }

    [Fact]
    public void SuperConfirmRejectsPartitionsThatExceedDeviceSize()
    {
        var model = new DeliveryViewModel();
        model.SetMode(true, true, false);
        model.SelectSources([Input("system_a"), Input("vendor_a")]);
        var plan = new DeliveryPlan
        {
            PackSuper = true,
            Container = [.. model.Container.Select(i => i.Snapshot())],
            Super = new DeliverySuperOptions { DeviceSize = 409_600 }
        };
        var error = Assert.Throws<InvalidDataException>(() => model.Confirm(plan));
        Assert.Contains("Super 空间不足", error.Message, StringComparison.Ordinal);
        Assert.Contains("421888", error.Message, StringComparison.Ordinal);
        Assert.False(model.Confirmed);
        Assert.DoesNotContain(model.Output, i => i.Generated);
    }

    [Fact]
    public void ConfirmedContainerKeepsMembershipAndNewSelectionsGoToOutput()
    {
        var system = Input("system"); var boot = Input();
        var model = new DeliveryViewModel();
        model.SetMode(true, true);
        model.SelectSources([system]);
        model.Confirm(new DeliveryPlan { PackSuper = true, Super = new() { DeviceSize = 8 * 1024 * 1024 }, Container = [.. model.Container.Select(i => i.Snapshot())] });
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
            if (super) model.AddZero("vendor");
            model.Confirm(new DeliveryPlan { PackSuper = super, PackPayload = payload, Super = new() { DeviceSize = 8 * 1024 * 1024 }, Container = [.. model.Container.Select(i => i.Snapshot())] });
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
            PackPayload = true,
            ContainerConfirmed = true,
            Container = [Input("system")],
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
            var path = Path.Combine(Root, "sparse.img");
            SparseImageConverter.ConvertRawToSparse(file.SourcePath, path);
            file = file with { SourcePath = path, SourceLength = new FileInfo(path).Length };
        }
        var output = Path.Combine(Root, "super.img");
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
        var a = Input("my_bigball_a");
        var b = Input("my_bigball_b", 0);
        var output = Path.Combine(Root, "super.img");
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
        var output = Path.Combine(Root, "empty.img");
        await DeliverySuperBuilder.BuildAsync([new() { Partition = "system" }], new() { DeviceSize = 8 * 1024 * 1024, Empty = true }, output, token: TestContext.Current.CancellationToken);
        Assert.Equal(0x616c4467u, BitConverter.ToUInt32(File.ReadAllBytes(output)));
        Assert.True(new FileInfo(output).Length < 128 * 1024);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = Path.Combine(Root, "cancelled.img");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeliverySuperBuilder.BuildAsync([Input()], new(), cancelled, token: cancellation.Token));
        Assert.False(File.Exists(cancelled));
    }

    [Fact]
    public async Task DirectoryExportPreservesInputsAndRejectsConflicts()
    {
        var file = Input(); var sourceHash = SHA256.HashData(File.ReadAllBytes(file.SourcePath));
        var plan = new DeliveryPlan { Files = [file], Txt = false, OutputDirectory = Path.Combine(Root, "out") };
        var service = new DeliveryOutputService(new NoTools());
        await service.ExportAsync(plan, token: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(plan.OutputDirectory, "images", "boot.img")));
        Assert.True(File.Exists(Path.Combine(plan.OutputDirectory, "checksums.sha256")));
        await Assert.ThrowsAsync<IOException>(() => service.ExportAsync(plan, token: TestContext.Current.CancellationToken));
        Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(file.SourcePath)));
        Assert.Empty(Directory.GetDirectories(Root, ".uotan-delivery-*"));
    }

    [Fact]
    public void PlanRoundTripKeepsDraftsAndDropsImageMembership()
    {
        var plan = new DeliveryPlan
        {
            Files = [Input()],
            OutputDirectory = Path.Combine(Root, "out"),
            Scripts = new() { ["flash.txt"] = "manual edits\n" },
            GeneratedScripts = new() { ["flash.txt"] = "boot images/boot.img\r\n" },
            OpenScripts = ["flash.txt"],
            ScriptCarets = new() { ["flash.txt"] = 5 }
        };
        var state = DeliveryPlanStore.CaptureElement(Root, plan);
        var restored = Assert.IsType<DeliveryPlan>(DeliveryPlanStore.LoadFromState(state, Root));
        Assert.Empty(restored.Files);
        Assert.Empty(restored.Container);
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
    public void LoadFromState_ToleratesLegacyPlanMissingRecoveryUpdaterPath()
    {
        var plan = new DeliveryPlan { OutputDirectory = Root, Files = [Input()], Container = [] };
        var captured = DeliveryPlanStore.CaptureElement(Root, plan);
        var root = System.Text.Json.Nodes.JsonNode.Parse(captured.GetRawText())!;
        root["Package"]!.AsObject().Remove("RecoveryUpdaterPath");
        using var doc = JsonDocument.Parse(root.ToJsonString());
        var restored = DeliveryPlanStore.LoadFromState(doc.RootElement, Root);
        Assert.NotNull(restored);
        Assert.Equal("", restored!.Package.RecoveryUpdaterPath);
    }

    [Fact]
    public async Task DownloadRechecksCacheAndRejectsCorruption()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var handler = new DownloadHandler(data);
        using var http = new HttpClient(handler);
        var service = new DeliveryToolService(http, Path.Combine(Root, "cache"));
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
        var archive = Path.Combine(Root, "bad.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("platform-tools/../../escaped").Open()) stream.WriteByte(1);
        await Assert.ThrowsAsync<InvalidDataException>(() => DeliveryToolService.ExtractAsync(archive, "platform-tools/", Path.Combine(Root, "tools"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(Root, "escaped")));
    }

    [Fact]
    public async Task CancellingDuringCopyLeavesExistingDirectoryUntouched()
    {
        var file = Input(size: 1024 * 1024);
        var output = Path.Combine(Root, "out"); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "keep.txt"), "keep");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<DeliveryProgress>(p => { if (p.Percent > 55) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeliveryOutputService(new NoTools()).ExportAsync(
            new() { Files = [file], Txt = false, OutputDirectory = output }, progress, cancellation.Token));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(output, "keep.txt")));
        Assert.Single(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(Root, ".uotan-delivery-*"));
    }

    [Fact]
    public async Task ZstdSuperDecompressesToSameRawImage()
    {
        var file = Input("system");
        var options = new DeliverySuperOptions { DeviceSize = 8 * 1024 * 1024 };
        var raw = Path.Combine(Root, "raw.img");
        await DeliverySuperBuilder.BuildAsync([file], options, raw, token: TestContext.Current.CancellationToken);
        var output = Path.Combine(Root, "compressed");
        await new DeliveryOutputService(new NoTools()).ExportAsync(new()
        {
            OrganizeDirectory = false,
            PackSuper = true,
            Super = options with { Zstd = true },
            Container = [file],
            OutputDirectory = output
        }, token: TestContext.Current.CancellationToken);
        using var compressed = File.OpenRead(Path.Combine(output, "super.zst"));
        using var decoded = new MemoryStream();
        UotanStudio.Compress.Compressors.ZstdCompressor.Decompress(compressed, decoded);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(raw)), SHA256.HashData(decoded.ToArray()));
    }

    [Fact]
    public async Task SparseSuperRepackFileStaysWellBelowDeviceSize()
    {
        var file = Input("system", 256 * 1024);
        const ulong deviceSize = 64UL * 1024 * 1024;
        var options = new DeliverySuperOptions { DeviceSize = deviceSize, Sparse = true, BlockSize = 4096 };
        var output = Path.Combine(Root, "sparse-super.img");
        await DeliverySuperBuilder.BuildAsync([file], options, output, token: TestContext.Current.CancellationToken);

        var length = new FileInfo(output).Length;
        Assert.True(length < (long)deviceSize / 4,
            $"Sparse super output {length} bytes should be well under the {deviceSize}-byte device.");

        using var sparse = SparseReader.FromImageFile(output);
        Assert.Equal(deviceSize, (ulong)sparse.Header.TotalBlocks * sparse.Header.BlockSize);
    }

    [Fact]
    public async Task EmptySuperGeneratesOnlySuperEmptyImage()
    {
        var file = Input("system");
        var options = new DeliverySuperOptions { DeviceSize = 8 * 1024 * 1024, Empty = true };
        var output = Path.Combine(Root, "empty-out");
        await new DeliveryOutputService(new NoTools()).ExportAsync(new()
        {
            OrganizeDirectory = false,
            PackSuper = true,
            Super = options,
            Container = [file],
            OutputDirectory = output
        }, token: TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(output, "super.img")));
        Assert.False(File.Exists(Path.Combine(output, "super.zst")));
        var empty = Path.Combine(output, "super_empty.img");
        Assert.Equal(0x616c4467u, BitConverter.ToUInt32(File.ReadAllBytes(empty)));
        Assert.True(new FileInfo(empty).Length < 128 * 1024);
    }

    [Fact]
    public void EmptySuperExcludesZstdCompression()
    {
        var plan = new DeliveryPlan
        {
            OrganizeDirectory = false,
            PackSuper = true,
            Super = new() { DeviceSize = 8 * 1024 * 1024, Empty = true, Zstd = true },
            Container = [Input("system")],
            OutputDirectory = Path.Combine(Root, "empty-zstd-out")
        };
        Assert.Throws<InvalidDataException>(() => DeliveryValidation.Validate(plan));
    }

    [Fact]
    public void SuperZstdLevelRaisesDefaultAndRejectsInvalidValues()
    {
        var defaults = new DeliverySuperOptions();
        Assert.Equal(DeliverySuperOptions.DefaultZstdLevel, defaults.ZstdLevel);
        Assert.Equal(DeliverySuperOptions.DefaultZstdLevel, DeliverySuperOptions.ResolveZstdLevel(defaults with { ZstdLevel = 0 }));
        Assert.Equal(DeliverySuperOptions.DefaultZstdLevel, DeliverySuperOptions.ResolveZstdLevel(defaults with { ZstdLevel = 30 }));
        Assert.Equal(7, DeliverySuperOptions.ResolveZstdLevel(defaults with { ZstdLevel = 7 }));

        var plan = new DeliveryPlan
        {
            OrganizeDirectory = false,
            PackSuper = true,
            Super = new() { DeviceSize = 8 * 1024 * 1024, Zstd = true, ZstdLevel = 30 },
            Container = [Input("system")],
            OutputDirectory = Path.Combine(Root, "bad-zstd-out")
        };
        Assert.Throws<InvalidDataException>(() => DeliveryValidation.Validate(plan));
    }

    [Fact]
    public void SuperZstdLevelSurvivesPlanPersistence()
    {
        var plan = new DeliveryPlan { Super = new() { Zstd = true, ZstdLevel = 12 } };
        var restored = DeliveryPlanStore.LoadFromState(DeliveryPlanStore.CaptureElement(Root, plan), Root);
        Assert.Equal(12, restored!.Super.ZstdLevel);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EmptySuperOutputsWithoutConfirmationSourcesOrScripts(bool organize, bool retainedImages)
    {
        var missing = new DeliveryFile { SourcePath = Path.Combine(Root, "missing.img"), Partition = "system", FileName = "system.img" };
        var plan = new DeliveryPlan
        {
            OrganizeDirectory = organize,
            PackSuper = true,
            Super = new() { Empty = true, DeviceSize = 8 * 1024 * 1024 },
            Container = retainedImages ? [missing] : [],
            Files = [missing],
            Windows = true,
            Linux = true,
            MacOS = true,
            Recovery = true,
            Scripts = new() { ["FlashWindows.bat"] = "user draft" },
            OutputDirectory = Path.Combine(Root, "empty-direct")
        };
        DeliveryValidation.Validate(plan);
        Assert.Empty(DeliveryScriptGenerator.Generate(plan));
        var expected = Path.Combine(plan.OutputDirectory, "super_empty.img");
        Assert.Equal([expected], DeliveryOutputService.PlannedOutputPaths(plan));
        var published = await new DeliveryOutputService(new RejectTools()).ExportAsync(plan, token: TestContext.Current.CancellationToken);
        Assert.Equal([expected], published);
        Assert.Equal([expected], Directory.GetFiles(plan.OutputDirectory, "*", SearchOption.AllDirectories));
        Assert.Equal(0x616c4467u, BitConverter.ToUInt32(File.ReadAllBytes(expected)));
        Assert.Equal([expected], DeliveryOutputService.FindExistingOutputs(plan));
        Assert.Equal("user draft", plan.Scripts["FlashWindows.bat"]);
        Assert.Equal(missing, Assert.Single(plan.Files));
    }

    private sealed class RejectTools : IDeliveryTools
    {
        public Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token)
            => throw new InvalidOperationException("Metadata-only output must not prepare tools.");
    }

    [Fact]
    public void ScriptsNeverFlashSuperEmpty()
    {
        var file = Input("system");
        var plan = new DeliveryPlan
        {
            PackSuper = true,
            OrganizeDirectory = true,
            ContainerConfirmed = true,
            Super = new() { DeviceSize = 8 * 1024 * 1024, Empty = true },
            Container = [file with { IncludeInSuper = true }],
            Files = [new DeliveryFile { Generated = true, FileName = "super_empty.img", Partition = "super_empty" }],
            OutputDirectory = Path.Combine(Root, "script-out")
        };
        DeliveryValidation.Validate(plan, false);
        var scripts = DeliveryScriptGenerator.Generate(plan, key => key);
        foreach (var (_, content) in scripts)
            Assert.DoesNotContain("super_empty", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScriptPromptsFollowLocalizeAndFallBackToEnglish()
    {
        var plan = new DeliveryPlan
        {
            Files = [Input(), Input("init_boot") with { Partition = "init_boot", Optional = true, Default = true },
                Input("init_boot_ksu") with { Partition = "init_boot", Optional = true }],
            Windows = true,
            Linux = true,
            Txt = false
        };
        var zh = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Delivery_Script_Completed"] = "已完成。",
            ["Delivery_Script_AskStart"] = "是否开始刷写？[y/N]：",
            ["Delivery_Script_Choose"] = "请选择 {0}",
            ["Delivery_Script_MissingImage"] = "缺少镜像：{0}",
        };
        Func<string, string> localize = key => zh.GetValueOrDefault("Delivery_Script_" + key, "Delivery_Script_" + key);
        var scripts = DeliveryScriptGenerator.Generate(plan, localize);
        Assert.Contains("请选择 init_boot", scripts["FlashLinux.sh"]);
        Assert.Contains("是否开始刷写？[y/N]：", scripts["FlashLinux.sh"]);
        Assert.Contains("已完成。", scripts["FlashLinux.sh"]);
        Assert.Contains("缺少镜像：boot.img", scripts["FlashLinux.sh"]);
        Assert.Contains("Invalid choice.", scripts["FlashLinux.sh"]);
        Assert.Contains("请选择 init_boot", scripts["FlashWindows.bat"]);
        var english = DeliveryScriptGenerator.Generate(plan);
        Assert.Contains("Choose init_boot", english["FlashLinux.sh"]);
        Assert.Contains("Completed.", english["FlashWindows.bat"]);
    }

    [Fact]
    public void LocalizeReturningKeyNameFallsBackToEnglish()
    {
        var plan = new DeliveryPlan { Files = [Input()], Windows = true, Linux = true, Txt = false };
        var scripts = DeliveryScriptGenerator.Generate(plan, key => "Delivery_Script_" + key);
        Assert.Contains("Connect exactly one Fastboot device.", scripts["FlashLinux.sh"]);
        Assert.Contains("Completed.", scripts["FlashLinux.sh"]);
        Assert.Contains("Delivery failed. Check device, images and tools.", scripts["FlashWindows.bat"]);
    }

    [Fact]
    public void ZstdFilesEmitDecompressionStepsInBothScripts()
    {
        var plan = new DeliveryPlan { Files = [Input("super") with { FileName = "super.zst", Partition = "super" }], Windows = true, Linux = true, Txt = false };
        var scripts = DeliveryScriptGenerator.Generate(plan);
        Assert.Contains("if not exist \"%TOOLS%\\zstd.exe\" goto failed", scripts["FlashWindows.bat"]);
        Assert.Contains("\"%TOOLS%\\zstd.exe\" -d", scripts["FlashWindows.bat"]);
        Assert.Contains("[ -x \"$ZSTD\" ] || die", scripts["FlashLinux.sh"]);
        Assert.Contains("\"$ZSTD\" -d -o \"$file\"", scripts["FlashLinux.sh"]);
    }

    [Fact]
    public void TxtManifestExcludesCompressedImages()
    {
        var plan = new DeliveryPlan
        {
            Files = [Input() with { Optional = true, Default = true }, Input("super") with { FileName = "super.zst", Partition = "super" },
                Input("boot_ksu") with { Partition = "boot", Optional = true }],
            Txt = true,
            TxtVariants = true
        };
        var scripts = DeliveryScriptGenerator.Generate(plan);
        Assert.Equal("boot images/boot.img\r\n", scripts["flash.txt"]);
        Assert.DoesNotContain("super.zst", scripts["flash.txt"]);
        Assert.Equal("boot images/boot_ksu.img\r\n", scripts["flash-boot_ksu.txt"]);
        Assert.DoesNotContain("super.zst", scripts["flash-boot_ksu.txt"]);
    }

    [Fact]
    public void ZstdSelectionDisablesTxtManifestGeneration()
    {
        var plan = new DeliveryPlan
        {
            Files = [Input("super") with { FileName = "super.zst", Partition = "super" }],
            Super = new DeliverySuperOptions { Zstd = true },
            Txt = true,
            Windows = true
        };

        var scripts = DeliveryScriptGenerator.Generate(plan);

        Assert.DoesNotContain("flash.txt", scripts.Keys);
        Assert.Contains("FlashWindows.bat", scripts.Keys);
    }

    [Fact]
    public void MacScriptSelectsZstdBinaryByArchitecture()
    {
        var plan = new DeliveryPlan { Files = [Input("super") with { FileName = "super.zst", Partition = "super" }], MacOS = true, Txt = false };
        var mac = DeliveryScriptGenerator.Generate(plan)["FlashMacOS.sh"];
        Assert.Contains("uname -m", mac);
        Assert.Contains("zstd-x86_64", mac);
        Assert.Contains("ZSTD=\"$TOOLS/zstd\"", mac);
        Assert.Contains("\"$ZSTD\" -d -o \"$file\"", mac);
        var linux = DeliveryScriptGenerator.Generate(plan with { MacOS = false, Linux = true })["FlashLinux.sh"];
        Assert.Contains("uname -m", linux);
        Assert.Contains("zstd-arm64", linux);
        Assert.Contains("zstd-loong64", linux);
        Assert.DoesNotContain("zstd-x86_64", linux);
    }

    [Theory]
    [InlineData("Windows", new[] { "fastboot.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll", "libwinpthread-1.dll", "NOTICE.txt", "source.properties", "zstd.exe" },
        new[] { "adb.exe", "sqlite3.exe", "mke2fs.exe", "etc1tool.exe", "hprof-conv.exe", "make_f2fs.exe", "make_f2fs_casefold.exe", "mke2fs.conf" })]
    [InlineData("Linux", new[] { "fastboot", "lib64/libc++.so", "NOTICE.txt", "source.properties", "zstd", "zstd-arm64", "zstd-loong64" },
        new[] { "adb", "sqlite3", "mke2fs", "etc1tool", "hprof-conv", "make_f2fs", "make_f2fs_casefold", "mke2fs.conf" })]
    [InlineData("macOS", new[] { "fastboot", "lib64/libc++.dylib", "NOTICE.txt", "source.properties", "zstd", "zstd-x86_64" },
        new[] { "adb", "sqlite3", "mke2fs", "etc1tool", "hprof-conv", "make_f2fs", "make_f2fs_casefold", "mke2fs.conf" })]
    public void TrimPlatformToolsKeepsFastbootAndRemovesDeadBinaries(string name, string[] keeps, string[] removes)
    {
        var target = Path.Combine(Root, "bin", name);
        foreach (var file in keeps.Concat(removes))
        {
            var path = Path.Combine(target, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
        DeliveryToolService.TrimPlatformTools(name, target);
        foreach (var file in keeps) Assert.True(File.Exists(Path.Combine(target, file)), $"{name}/{file} should be kept");
        foreach (var file in removes) Assert.False(File.Exists(Path.Combine(target, file)), $"{name}/{file} should be removed");
    }
}