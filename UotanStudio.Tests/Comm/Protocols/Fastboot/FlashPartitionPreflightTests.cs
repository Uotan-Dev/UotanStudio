using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Fastboot;

/// <summary>
/// Flash preflight tests: the queries a flash must run before it transfers a payload, and the
/// opt-outs callers that already validated the target use to avoid paying them twice. A rejection is
/// asserted against an empty mutating-command list, because the point of the preflight is that a wrong
/// target never receives an image.
/// <para>刷写预检测试：一次刷写在传输负载之前必须执行的查询，以及已完成目标校验的调用方用来避免重复
/// 支付这些查询的退出开关。拒绝路径以“空的变更类命令列表”断言，因为预检的意义就在于错误的目标绝不
/// 收到镜像。</para>
/// </summary>
public class FlashPartitionPreflightTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static FastbootSession NewSession(ScriptedCommTransport transport)
        => new(transport, timeoutMs: 250);

    private static List<string> Commands(ScriptedCommTransport transport)
        => [.. transport.Writes.Select(Encoding.UTF8.GetString)];

    private static List<string> MutatingCommands(ScriptedCommTransport transport)
        => [.. Commands(transport).Where(c =>
            c.StartsWith("download:", StringComparison.Ordinal)
            || c.StartsWith("flash:", StringComparison.Ordinal))];

    private static ScriptedCommTransport Device(
        bool userspace, string? hasSlot = "no", string? currentSlot = null,
        string? partitionSize = null, string? isLogical = "no", long maxDownloadSize = 16 * 1024 * 1024)
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = data =>
        {
            var text = Encoding.UTF8.GetString(data);
            if (text.StartsWith("getvar:", StringComparison.Ordinal))
            {
                var key = text["getvar:".Length..];
                var value = key switch
                {
                    FastbootConstants.VarIsUserspace => userspace ? "yes" : "no",
                    FastbootConstants.VarMaxDownloadSize => "0x" + maxDownloadSize.ToString("x"),
                    FastbootConstants.VarCurrentSlot => currentSlot,
                    _ when key.StartsWith(FastbootConstants.VarHasSlotPrefix, StringComparison.Ordinal) => hasSlot,
                    _ when key.StartsWith(FastbootConstants.VarPartitionSizePrefix, StringComparison.Ordinal) => partitionSize,
                    _ when key.StartsWith(FastbootConstants.VarIsLogicalPrefix, StringComparison.Ordinal) => isLogical,
                    _ => null
                };
                return [Utf8(value is null ? "FAILunknown variable" : "OKAY" + value)];
            }

            if (text.StartsWith("download:", StringComparison.Ordinal))
            {
                return [Utf8("DATA" + text["download:".Length..]), Utf8("OKAY")];
            }

            return [Utf8("OKAY")];
        };
        return transport;
    }

    [Fact]
    public void FlashPartition_ImageLargerThanMaxDownloadSize_RejectsBeforeAnyDownload()
    {
        var transport = Device(userspace: true, partitionSize: "0x1000000", maxDownloadSize: 4096);
        using var session = NewSession(transport);
        using var image = new MemoryStream(new byte[16384]);

        Assert.Throws<FastbootException>(() => session.FlashPartition(
            "system_a", image, 16384, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(MutatingCommands(transport));
    }

    [Fact]
    public void FlashPartition_LogicalPartitionInBootloaderMode_RejectsBeforeAnyDownload()
    {
        var transport = Device(userspace: false, isLogical: "yes", partitionSize: "0x1000000");
        using var session = NewSession(transport);
        using var image = new MemoryStream(new byte[4096]);

        Assert.Throws<FastbootException>(() => session.FlashPartition(
            "system_a", image, 4096, resolveSlot: false, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(MutatingCommands(transport));
        Assert.Contains("getvar:is-userspace", Commands(transport));
        Assert.Contains("getvar:is-logical:system_a", Commands(transport));
    }

    [Fact]
    public void FlashPartition_LogicalPartitionInFastbootd_FlashesWithoutModeRejection()
    {
        var transport = Device(userspace: true, isLogical: "yes", partitionSize: "0x1000000");
        using var session = NewSession(transport);
        using var image = new MemoryStream(new byte[4096]);

        session.FlashPartition("system_a", image, 4096, resolveSlot: false,
            cancellationToken: TestContext.Current.CancellationToken).ThrowIfFailed();

        Assert.Contains("flash:system_a", Commands(transport));
        Assert.DoesNotContain(Commands(transport), c => c == "getvar:is-logical:system_a");
    }

    [Fact]
    public void FlashPartition_ImageLargerThanReportedPartition_RejectsBeforeAnyDownload()
    {
        var transport = Device(userspace: false, partitionSize: "0x1000");
        using var session = NewSession(transport);
        using var image = new MemoryStream(new byte[8192]);

        Assert.Throws<FastbootException>(() => session.FlashPartition(
            "boot", image, 8192, resolveSlot: false, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(MutatingCommands(transport));
    }

    [Fact]
    public void FlashPartition_FastbootdReportsZeroSize_RejectsMissingPartition()
    {
        var transport = Device(userspace: true, partitionSize: "0x0");
        using var session = NewSession(transport);
        using var image = new MemoryStream(new byte[4096]);

        Assert.Throws<FastbootException>(() => session.FlashPartition(
            "nope_a", image, 4096, resolveSlot: false, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(MutatingCommands(transport));
    }

    [Fact]
    public void FlashPartition_BootloaderReportsZeroSize_DoesNotRejectUnknownPartitionSize()
    {
        var transport = Device(userspace: false, partitionSize: "0x0");
        using var session = NewSession(transport);
        using var image = new MemoryStream(new byte[4096]);

        session.FlashPartition("boot", image, 4096, resolveSlot: false,
            cancellationToken: TestContext.Current.CancellationToken).ThrowIfFailed();

        Assert.Contains("flash:boot", Commands(transport));
    }

    [Fact]
    public void FlashPartition_DeviceOmitsPartitionSize_FlashesWithoutCapacityRejection()
    {
        var transport = Device(userspace: false, partitionSize: null);
        using var session = NewSession(transport);
        using var image = new MemoryStream(new byte[65536]);

        session.FlashPartition("boot", image, 65536, resolveSlot: false,
            cancellationToken: TestContext.Current.CancellationToken).ThrowIfFailed();

        Assert.Contains("flash:boot", Commands(transport));
        Assert.Contains("getvar:partition-size:boot", Commands(transport));
    }

    [Fact]
    public void FlashPartition_PreflightOptedOut_KeepsThePreChangeCommandSequence()
    {
        var transport = Device(userspace: false, isLogical: "yes", partitionSize: "0x1000");
        using var session = NewSession(transport);
        using var image = new MemoryStream(new byte[4096]);

        session.FlashPartition("system_a", image, 4096, resolveSlot: false,
            cancellationToken: TestContext.Current.CancellationToken,
            options: FlashPartitionOptions.NoPreflight).ThrowIfFailed();

        var commands = Commands(transport);
        Assert.Equal(["getvar:max-download-size"],
            [.. commands.Where(c => c.StartsWith("getvar:", StringComparison.Ordinal))]);
        Assert.Equal("download:00001000", commands[1]);
        Assert.Equal(new byte[4096], transport.Writes[2]);
        Assert.Equal("flash:system_a", commands[3]);
        Assert.Equal(4, transport.Writes.Count);
    }

    [Fact]
    public void FlashPartition_LogicalTargetCheckOptedOut_SkipsModeQueries()
    {
        var transport = Device(userspace: false, isLogical: "yes", partitionSize: "0x1000000");
        using var session = NewSession(transport);
        using var image = new MemoryStream(new byte[4096]);

        session.FlashPartition("system_a", image, 4096, resolveSlot: false,
            cancellationToken: TestContext.Current.CancellationToken,
            options: new FlashPartitionOptions { VerifyLogicalTarget = false }).ThrowIfFailed();

        var commands = Commands(transport);
        Assert.Contains("flash:system_a", commands);
        Assert.DoesNotContain(commands, c => c == "getvar:is-userspace");
        Assert.DoesNotContain(commands, c => c == "getvar:is-logical:system_a");
    }

    [Fact]
    public void FlashPartition_SlotResolutionHappensBeforePreflight()
    {
        var transport = Device(userspace: true, hasSlot: "yes", currentSlot: "b", partitionSize: "0x1000000");
        using var session = NewSession(transport);
        using var image = new MemoryStream(new byte[4096]);

        session.FlashPartition("boot", image, 4096,
            cancellationToken: TestContext.Current.CancellationToken).ThrowIfFailed();

        var commands = Commands(transport);
        Assert.Contains("flash:boot_b", commands);
        Assert.Contains("getvar:partition-size:boot_b", commands);
        Assert.DoesNotContain(commands, c => c == "getvar:partition-size:boot");
    }

    [Fact]
    public void FlashPartition_CancelledToken_RejectsBeforeAnyCommand()
    {
        var transport = Device(userspace: true, partitionSize: "0x1000000");
        using var session = NewSession(transport);
        using var image = new MemoryStream(new byte[4096]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => session.FlashPartition(
            "boot", image, 4096, resolveSlot: false, cancellationToken: cancelled.Token));

        Assert.Empty(transport.Writes);
    }
}