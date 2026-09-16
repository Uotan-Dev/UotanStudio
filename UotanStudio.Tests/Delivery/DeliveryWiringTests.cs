using System.Text.Json;
using Google.Protobuf;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Firmware;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class DeliveryWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "delivery-wiring-" + Guid.NewGuid().ToString("N"));
    public DeliveryWiringTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private void SaveSource(string root, DeliverySourceContainerSettings settings)
    {
        var path = SourceContainerMetadataService.GetPath(root, settings.Kind);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(settings, SourceContainerJsonContext.Default.DeliverySourceContainerSettings));
    }

    [Fact]
    public void PayloadMembershipExcludesPhysicalBootAndDoesNotLeakAcrossProjects()
    {
        var dynamic = new DynamicPartitionMetadata();
        var group = new DynamicPartitionGroup { Name = "main", Size = 1024 * 1024 };
        group.PartitionNames.Add("system");
        dynamic.Groups.Add(group);
        SaveSource(_root, new() { Kind = "payload", PartitionNames = ["boot", "system"], OriginalStructures = [dynamic.ToByteArray()] });
        var names = DeliveryDynamicPartitions.Read(_root)!;
        Assert.True(DeliveryDynamicPartitions.Contains(names, "system_a"));
        Assert.False(DeliveryDynamicPartitions.Contains(names, "boot"));
        Assert.Null(DeliveryDynamicPartitions.Read(Path.Combine(_root, "other-project")));
    }

    [Fact]
    public async Task DynamicAttributeChoosesFastbootdInBothScripts()
    {
        SaveSource(_root, new() { Kind = "super", PartitionNames = ["system_a", "system_b"] });
        var model = new DeliveryViewModel();
        var file = new DeliveryFile { SourcePath = Path.Combine(_root, "system.img"), Partition = "system", FileName = "system.img" };
        model.SelectSources([file]);
        model.ApplySourcePartitions(file.SourcePath, DeliveryDynamicPartitions.Read(_root));
        var row = Assert.Single(model.Output);
        Assert.True(row.Fastbootd);
        var plan = new DeliveryPlan { Files = [row.Snapshot()], Windows = true, Linux = true, Txt = false };
        var scripts = DeliveryScriptGenerator.Generate(plan);
        Assert.Contains("ensure_mode yes; flash 'system'", scripts["FlashLinux.sh"]);
        Assert.Contains("call :ensure_mode yes", scripts["FlashWindows.bat"]);
        // 镜像归属不随交付计划持久化：动态属性只影响当前会话的脚本选择，重开后由作者重新选择。
        // Image membership is not persisted with the plan: the dynamic attribute only drives the current
        // session's scripts and the author re-selects after reopen.
        var state = DeliveryPlanStore.CaptureElement(_root, plan);
        Assert.Empty(DeliveryPlanStore.LoadFromState(state, _root)!.Files);
        row.PartitionName = "boot";
        Assert.False(row.Fastbootd);
        row.PartitionName = "system_b";
        Assert.True(row.Fastbootd);
    }

    [Fact]
    public void UnknownMetadataDoesNotEraseExplicitSavedAttribute()
    {
        var file = new DeliveryFile { SourcePath = Path.Combine(_root, "external.img"), Partition = "system", FileName = "external.img", Fastbootd = true };
        var model = new DeliveryViewModel();
        model.Reset(new() { Files = [file] });
        model.ApplySourcePartitions(file.SourcePath, null);
        Assert.True(Assert.Single(model.Output).Fastbootd);
    }

    [Theory]
    [InlineData("copy", "Delivery_ProgressCopy")]
    [InlineData("super", "Delivery_ProgressSuper")]
    [InlineData("payload", "Delivery_ProgressPayloadWrite")]
    [InlineData("recovery", "Delivery_ProgressPackage")]
    public async Task RealOutputReportsMeasuredBytesAndCompletes(string mode, string stage)
    {
        var source = Path.Combine(_root, "boot.img");
        File.WriteAllBytes(source, new byte[8192]);
        var file = new DeliveryFile { SourcePath = source, Partition = "boot", FileName = "boot.img" };
        var plan = new DeliveryPlan
        {
            OutputDirectory = Path.Combine(_root, "out"),
            Txt = false,
            OrganizeDirectory = mode is "copy" or "recovery",
            PackSuper = mode == "super",
            PackPayload = mode == "payload",
            Recovery = mode == "recovery",
            Container = mode is "super" or "payload" ? [file] : [],
            Files = mode is "copy" or "recovery" ? [file] : [],
            Super = new() { DeviceSize = 4 * 1024 * 1024 }
        };
        var reports = new List<DeliveryProgress>();
        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, new InlineProgress<DeliveryProgress>(reports.Add), TestContext.Current.CancellationToken);
        Assert.Contains(reports, value => value.Stage == stage && value.Bytes is { Processed: > 0 });
        Assert.Equal(100, reports[^1].Percent);
        if (mode == "copy") Assert.Contains(reports, value => value.Bytes == (8192L, 8192L));
    }

    [Fact]
    public async Task VerifyStageReportsProcessedBytes()
    {
        var source = Path.Combine(_root, "boot.img");
        File.WriteAllBytes(source, new byte[8192]);
        var file = new DeliveryFile { SourcePath = source, Partition = "boot", FileName = "boot.img" };
        var plan = new DeliveryPlan { OutputDirectory = Path.Combine(_root, "out"), Txt = false, Files = [file] };
        var reports = new List<DeliveryProgress>();
        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, new InlineProgress<DeliveryProgress>(reports.Add), TestContext.Current.CancellationToken);
        var verify = reports.Where(value => value.Stage == "Delivery_ProgressVerify").ToArray();
        Assert.NotEmpty(verify);
        Assert.Equal(0L, verify[0].Bytes!.Value.Processed);
        Assert.Equal(verify[^1].Bytes!.Value.Total, verify[^1].Bytes!.Value.Processed);
        Assert.True(verify[^1].Bytes!.Value.Total > 0);
        Assert.True(verify[^1].Percent >= verify[0].Percent);
    }

    private sealed class NoTools : IDeliveryTools
    {
        public Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token) => Task.CompletedTask;
    }
}