using System.Text.RegularExpressions;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// Covers the shared "Send Loader" button on a Qualcomm EDL endpoint: it waits for the programmer the Qualcomm
/// section names and, once clicked, uploads that programmer so the bootrom leaves Sahara for Firehose.
/// <para>覆盖高通 EDL 端点上通用的“发送引导程序”按钮：它等待高通分类指定的 programmer，点击后上传该 programmer，
/// 使 bootrom 从 Sahara 切换到 Firehose。</para>
/// </summary>
public sealed class FlashLoadBootGateTests
{
    [Fact]
    public void EdlProgrammerAddress_IsReadyOnlyWhenItNamesAnExistingFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-load-boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var elf = Path.Combine(directory, "prog_firehose_ddr.elf");
        try
        {
            File.WriteAllBytes(elf, [0x7f, (byte)'E', (byte)'L', (byte)'F']);

            Assert.False(FlashViewModel.IsEdlProgrammerPathReady(null));
            Assert.False(FlashViewModel.IsEdlProgrammerPathReady(string.Empty));
            Assert.False(FlashViewModel.IsEdlProgrammerPathReady("   "), "whitespace is not an address");
            Assert.False(FlashViewModel.IsEdlProgrammerPathReady(directory), "a directory is not a programmer");
            Assert.False(FlashViewModel.IsEdlProgrammerPathReady(Path.Combine(directory, "missing.elf")));
            Assert.True(FlashViewModel.IsEdlProgrammerPathReady(elf));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadBootButton_WaitsForTheQualcommProgrammerOnAnEdlEndpoint()
    {
        var operations = OperationsSource();
        var gating = File.ReadAllText(Path.Combine(RepositoryRoot(),
            "UotanStudio", "ViewModels", "Flash", "FlashViewModel.Gating.cs")).ReplaceLineEndings("\n");

        // The button keeps its own protocol/loader-stage gate; the programmer condition is page state, so it belongs
        // in the enable intent the page computes rather than in the device snapshot a gate receives.
        // 按钮保留自身的协议/loader 阶段门控；programmer 条件属于页面状态，因此落在页面计算的启用意图里，而不是门控收到的
        // 设备快照里。
        Assert.Contains(
            "RegisterGate(\"LoadBootButton\", FlashControlGate.LoaderOnly(\"LoadBootButton\", busyDisabled: true));",
            gating, StringComparison.Ordinal);
        Assert.Contains(
            "SetControlEnabled(\"LoadBootButton\", !_busy && CanSendBootLoader);",
            operations, StringComparison.Ordinal);
        Assert.Contains(
            "internal bool CanSendBootLoader => CanLoadBoot &&\n        (SelectedDevice?.Protocol is not DeviceProtocolKind.Edl || EdlProgrammerReady);",
            operations, StringComparison.Ordinal);
        Assert.Contains("internal bool EdlProgrammerReady => IsEdlProgrammerPathReady(EffectiveEdlProgrammer);",
            operations, StringComparison.Ordinal);
    }

    [Fact]
    public void EdlLoadBoot_SendsTheProgrammerInsteadOfOpeningAPicker()
    {
        var operations = OperationsSource();
        var edlBranch = SwitchBranch(operations, "Edl", "Mtk");

        Assert.Contains("await SendEdlProgrammerAsync(device).ConfigureAwait(true);", edlBranch,
            StringComparison.Ordinal);
        Assert.DoesNotContain("OnEdlChooseProgrammerClick", edlBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("TryLoadBootFromLoaderRowAsync", edlBranch, StringComparison.Ordinal);

        var send = MethodBody(operations, "private async Task SendEdlProgrammerAsync");
        Assert.Contains("await App.DeviceMonitor.RunEdlAsync(device", send, StringComparison.Ordinal);
        Assert.Contains("await ConfigureEdlAsync(session, initialization, cancellation.Token)", send,
            StringComparison.Ordinal);
        Assert.Contains("Flash_LoadBootEdlDone", send, StringComparison.Ordinal);
        Assert.Contains("Flash_LoadBootNoProgrammer", send, StringComparison.Ordinal);
    }

    /// <summary>Reads the part of a switch that belongs to one protocol branch.<para>读取 switch 中某个协议分支的部分。</para></summary>
    private static string SwitchBranch(string source, string branch, string nextBranch)
    {
        var start = source.IndexOf($"case DeviceProtocolKind.{branch}:", StringComparison.Ordinal);
        Assert.True(start >= 0, $"the {branch} branch is missing");
        var end = source.IndexOf($"case DeviceProtocolKind.{nextBranch}:", start, StringComparison.Ordinal);
        Assert.True(end > start, $"the {nextBranch} branch is missing");
        return source[start..end];
    }

    /// <summary>Reads a method body from its signature up to the next method-level declaration.<para>读取方法签名到下一个方法声明之间的正文。</para></summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} is missing");
        var rest = source[(start + signature.Length)..];
        var next = Regex.Match(rest, @"^    (?:private|internal|public) ", RegexOptions.Multiline);
        return next.Success ? rest[..next.Index] : rest;
    }

    private static string OperationsSource() => File.ReadAllText(Path.Combine(RepositoryRoot(),
        "UotanStudio", "ViewModels", "Flash", "FlashViewModel.Operations.cs")).ReplaceLineEndings("\n");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UotanStudio.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}