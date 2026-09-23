using System.Text;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(MtkXFlashStorageKind.Emmc, false)]
    [InlineData(MtkXFlashStorageKind.Emmc, true)]
    [InlineData(MtkXFlashStorageKind.Ufs, false)]
    [InlineData(MtkXFlashStorageKind.Ufs, true)]
    public async Task Prepare_ScatterSelection_ReconcilesGptAndExecutesOnlyExplicitSource(MtkXFlashStorageKind kind, bool sparse)
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("selected.img", sparse ? PlanSparse() : new byte[512]);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport, kind);
        var disk = GptReaderTests.CreateDisk(512);
        var region = kind == MtkXFlashStorageKind.Emmc ? 8u : 3u;
        EnqueueGpt(transport, disk);
        var snapshot = await session.ReadXFlashGptAsync(region, TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;
        var layout = ScatterLayout(kind);

        using var plan = MtkFlashPlan.Prepare(snapshot, layout,
            [new("SYS2", sparse ? MtkFlashStepKind.Sparse : MtkFlashStepKind.Raw, 0, sparse ? 2048 : 512, path)],
            TestContext.Current.CancellationToken);

        Assert.Equal(before, transport.Writes.Count);
        Assert.Same(snapshot, plan.GptSnapshot);
        Assert.Equal(sparse ? 1024 : 512, plan.TotalBytes);
        Assert.Equal(sparse ? new long[] { 40 * 512, 42 * 512 } : [40 * 512],
            plan.Operations.Select(operation => operation.Offset));
        Assert.All(plan.Operations, operation => Assert.Equal(region, operation.PartitionId));
        EnqueueGpt(transport, disk);
        EnqueuePlanWrite(transport);
        if (sparse)
        {
            EnqueuePlanWrite(transport);
        }

        var result = await session.ExecuteXFlashPlanAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new MtkFlashPlanResult(sparse ? 2 : 1, sparse ? 1024 : 512, 0, 0), result);
        Assert.Equal(MtkFlashPlanState.Completed, plan.State);
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Theory]
    [InlineData("physical")]
    [InlineData("linear")]
    [InlineData("size")]
    [InlineData("sentinel")]
    [InlineData("layout")]
    public async Task Prepare_ScatterGeometryMismatch_RejectsBeforeSourceOpen(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        EnqueueGpt(transport, GptReaderTests.CreateDisk(512));
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        var layout = ScatterLayout(MtkXFlashStorageKind.Emmc);
        var entry = layout.Partitions[0];
        entry = failure switch
        {
            "physical" => entry with { PhysicalStartAddress = entry.PhysicalStartAddress + 512 },
            "linear" => entry with { LinearStartAddress = 0 },
            "size" => entry with { Size = entry.Size + 512 },
            "sentinel" => entry with { Size = ulong.MaxValue },
            _ => entry
        };
        layout = layout with { Name = failure == "layout" ? "UFS" : layout.Name, Partitions = [entry] };
        var before = transport.Writes.Count;

        Assert.Throws<InvalidDataException>(() => MtkFlashPlan.Prepare(snapshot, layout,
            [new("SYS2", MtkFlashStepKind.Raw, 0, 512, "missing.img")], TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Theory]
    [InlineData("region")]
    [InlineData("hardware")]
    [InlineData("download")]
    [InlineData("reserved")]
    [InlineData("boundary")]
    [InlineData("operation")]
    [InlineData("type")]
    [InlineData("extension")]
    [InlineData("boot-needed")]
    [InlineData("not-upgradable")]
    [InlineData("reserve")]
    public async Task Prepare_ScatterSpecialPolicy_RejectsWithoutDeviceCommands(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        EnqueueGpt(transport, GptReaderTests.CreateDisk(512));
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        var layout = ScatterLayout(MtkXFlashStorageKind.Emmc);
        var entry = layout.Partitions[0];
        var fields = entry.Fields.ToDictionary();
        switch (failure)
        {
            case "extension": fields["future_policy"] = "true"; break;
            case "boot-needed": fields["empty_boot_needed"] = "true"; break;
            case "not-upgradable": fields["is_upgradable"] = "false"; break;
            case "reserve": fields["reserve"] = "0x01"; break;
        }
        entry = failure switch
        {
            "region" => entry with { Region = "EMMC_BOOT1_BOOT2" },
            "hardware" => entry with { Storage = "HW_STORAGE_UFS" },
            "download" => entry with { IsDownload = false },
            "reserved" => entry with { IsReserved = true },
            "boundary" => entry with { BoundaryCheck = false },
            "operation" => entry with { OperationType = "PROTECTED" },
            "type" => entry with { Type = "SV5_BL_BIN" },
            _ => entry with { Fields = fields }
        };
        layout = layout with { Partitions = [entry] };
        var before = transport.Writes.Count;

        Assert.Throws<NotSupportedException>(() => MtkFlashPlan.Prepare(snapshot, layout,
            [new("SYS2", MtkFlashStepKind.Erase, 0, 512)], TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task Prepare_ScatterLaterMismatch_ReconcilesAllSelectionsBeforeOpeningFirstSource()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        EnqueueGpt(transport, GptReaderTests.CreateDisk(512));
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        var layout = ScatterLayout(MtkXFlashStorageKind.Emmc);
        layout = layout with
        {
            Partitions = [layout.Partitions[0], layout.Partitions[0] with { Index = "SYS3", Size = 512 }]
        };

        Assert.Throws<InvalidDataException>(() => MtkFlashPlan.Prepare(snapshot, layout,
        [
            new("SYS2", MtkFlashStepKind.Raw, 0, 512, "missing.img"),
            new("SYS3", MtkFlashStepKind.Erase, 0, 512)
        ], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Prepare_ScatterUnknownOrDuplicateIndex_RejectsAmbiguousSelection()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        EnqueueGpt(transport, GptReaderTests.CreateDisk(512));
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        var layout = ScatterLayout(MtkXFlashStorageKind.Emmc);

        Assert.Throws<KeyNotFoundException>(() => MtkFlashPlan.Prepare(snapshot, layout,
            [new("sys2", MtkFlashStepKind.Erase, 0, 512)], TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => MtkFlashPlan.Prepare(snapshot,
            layout with { Partitions = [layout.Partitions[0], layout.Partitions[0]] },
            [new("SYS2", MtkFlashStepKind.Erase, 0, 512)], TestContext.Current.CancellationToken));
    }

    internal static MtkScatterStorage ScatterLayout(MtkXFlashStorageKind kind)
    {
        var name = kind == MtkXFlashStorageKind.Emmc ? "EMMC" : "UFS";
        var region = kind == MtkXFlashStorageKind.Emmc ? "EMMC_USER" : "UFS_LU2";
        var text = $$"""
            - general: MTK_PLATFORM_CFG
              info:
              - config_version: V2.1.0
                platform: MT6785
                project: test
                storage: {{name}}
                boot_channel: test
                block_size: 0x20000
            - partition_index: SYS2
              partition_name: boot
              file_name: ../../not-opened.img
              is_download: true
              type: NORMAL_ROM
              linear_start_addr: 20480
              physical_start_addr: 20480
              partition_size: 2048
              region: {{region}}
              storage: HW_STORAGE_{{name}}
              boundary_check: true
              is_reserved: false
              operation_type: UPDATE
              is_upgradable: true
              empty_boot_needed: false
              reserve: 0x00
            - partition_index: SYS0
              partition_name: preloader
              file_name: not-opened-preloader.bin
              is_download: true
              type: SV5_BL_BIN
              linear_start_addr: 0
              physical_start_addr: 0
              partition_size: 524288
              region: unsupported-boot-region
              storage: HW_STORAGE_{{name}}
              boundary_check: true
              is_reserved: false
              operation_type: BOOTLOADERS
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return Assert.Single(MtkScatterReader.Read(stream, TestContext.Current.CancellationToken).Storages);
    }
}