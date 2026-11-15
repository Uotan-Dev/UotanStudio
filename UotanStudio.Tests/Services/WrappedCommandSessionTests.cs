using UotanStudio.Services.Devices;
using UotanStudio.Services.Terminal.Wrapped;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class WrappedCommandSessionTests
{
    private static (WrappedCommandSession Session, List<string> Written, List<string> Sent,
        WrappedCommandTestGateway Gateway) CreateSession(Func<WrappedTool, bool>? probe = null)
    {
        var gateway = new WrappedCommandTestGateway();
        var runner = new WrappedCommandRunner(gateway, "1.0.0-test");
        var written = new List<string>();
        var sent = new List<string>();
        var session = new WrappedCommandSession(runner, text =>
        {
            lock (written)
            {
                written.Add(text);
            }
        }, data =>
        {
            lock (sent)
            {
                sent.Add(data);
            }
            return Task.CompletedTask;
        }, probe ?? (_ => false));
        return (session, written, sent, gateway);
    }

    private static string Joined(List<string> chunks)
    {
        lock (chunks)
        {
            return string.Concat(chunks);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        Assert.True(condition(), "Timed out waiting for the wrapped command session to settle.");
    }

    [Fact]
    public async Task DispatchAsync_PlainCommand_ForwardedUnchanged()
    {
        var (session, written, sent, gateway) = CreateSession();
        await session.DispatchAsync("git status\r");
        Assert.Equal("git status\r", Joined(sent));
        Assert.Empty(Joined(written));
        Assert.Empty(gateway.ShellCalls);
    }

    [Fact]
    public async Task DispatchAsync_WrappedCommandWithNativeBinary_Forwarded()
    {
        var (session, written, sent, gateway) = CreateSession(_ => true);
        await session.DispatchAsync("adb devices\r");
        Assert.Equal("adb devices\r", Joined(sent));
        Assert.Empty(gateway.ShellCalls);
        Assert.Empty(Joined(written));
    }

    [Fact]
    public async Task DispatchAsync_WrappedCommandWithoutBinary_TakesOverAndPrintsOutput()
    {
        var (session, written, sent, gateway) = CreateSession();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Adb, "R58M1234"));
        await session.DispatchAsync("adb devices\r");
        // The takeover writes its output from a background task, so waiting only for the ETX marker leaves a window
        // where the marker has landed but the output has not; wait for the output the assertions actually inspect.
        await WaitForAsync(() => Joined(written).Contains("List of devices attached", StringComparison.Ordinal));
        await WaitForAsync(() => Joined(sent).EndsWith("\x03"));
        Assert.Contains("List of devices attached", Joined(written));
        Assert.EndsWith("\x03", Joined(sent));
        Assert.DoesNotContain("\r\n", Joined(sent));
        Assert.False(session.IsBusy);
    }

    [Fact]
    public async Task DispatchAsync_TypedCharacters_MirrorAndForward()
    {
        var (session, written, sent, gateway) = CreateSession();
        await session.DispatchAsync("adb dev");
        await session.DispatchAsync("\b");
        await session.DispatchAsync("vices");
        await session.DispatchAsync("\r");
        await WaitForAsync(() => Joined(written).Contains("List of devices attached", StringComparison.Ordinal));
        await WaitForAsync(() => Joined(sent).EndsWith("\x03"));
        Assert.StartsWith("adb dev\bvices", Joined(sent));
        Assert.Contains("List of devices attached", Joined(written));
    }

    [Fact]
    public async Task DispatchAsync_TabCompletionDesyncsLine_ForwardedToShell()
    {
        var (session, written, sent, gateway) = CreateSession();
        await session.DispatchAsync("adb dev\t");
        await session.DispatchAsync("\r");
        Assert.Equal("adb dev\t\r", Joined(sent));
        Assert.Empty(Joined(written));
        Assert.Empty(gateway.ShellCalls);
    }

    [Fact]
    public async Task DispatchAsync_UnknownSubcommand_ForwardedToShell()
    {
        var (session, written, sent, gateway) = CreateSession();
        await session.DispatchAsync("adb frobnicate\r");
        Assert.Equal("adb frobnicate\r", Joined(sent));
        Assert.Empty(gateway.ShellCalls);
        Assert.Empty(Joined(written));
    }

    [Fact]
    public async Task DispatchAsync_InteractiveShell_OpensPanelAndSuppressesOutput()
    {
        var (session, written, sent, gateway) = CreateSession();
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Adb, "R58M1234"));
        await session.DispatchAsync("adb shell\r");
        await WaitForAsync(() => gateway.OpenedDeviceShells.Count == 1);
        await WaitForAsync(() => Joined(sent).EndsWith("\x03"));
        await WaitForAsync(() => Joined(written).Contains("\r\n", StringComparison.Ordinal));
        Assert.Equal("R58M1234", gateway.OpenedDeviceShells.Single().Serial);
        Assert.Equal("\r\n", Joined(written));
    }

    [Fact]
    public async Task DispatchAsync_CtrlCWhileRunning_CancelsAndResets()
    {
        var (session, written, sent, gateway) = CreateSession(_ => false);
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Adb, "R58M1234"));
        var started = new TaskCompletionSource(TestContext.Current.CancellationToken);
        gateway.OnShellCall = () => started.TrySetResult();
        gateway.ShellDelay = TimeSpan.FromSeconds(30);
        await session.DispatchAsync("adb shell sleep 30\r");
        await WaitForAsync(() => started.Task.IsCompleted);
        await session.DispatchAsync("\x03");
        await WaitForAsync(() => !session.IsBusy);
        await WaitForAsync(() => Joined(sent).EndsWith("\x03"));
        await WaitForAsync(() => Joined(written).Contains("\r\n", StringComparison.Ordinal));
        Assert.Empty(gateway.ShellCalls);
        Assert.Equal("\r\n", Joined(written));
    }

    [Fact]
    public async Task DispatchAsync_InputDuringRun_QueuesForReplay()
    {
        var (session, written, sent, gateway) = CreateSession(_ => false);
        gateway.Devices.Add(WrappedCommandTestGateway.MakeDevice(DeviceProtocolKind.Adb, "R58M1234"));
        var started = new TaskCompletionSource(TestContext.Current.CancellationToken);
        gateway.OnShellCall = () => started.TrySetResult();
        gateway.ShellDelay = TimeSpan.FromMilliseconds(300);
        await session.DispatchAsync("adb shell echo hi\r");
        await WaitForAsync(() => started.Task.IsCompleted);
        await session.DispatchAsync("adb version\r");
        await WaitForAsync(() => Joined(written).Contains("Version UotanStudio Wrapped CommandLine Tool"));
        await WaitForAsync(() => !session.IsBusy);
        var state = $"busy={session.IsBusy} written=[{Joined(written).Replace("\r", "\\r").Replace("\n", "\\n")}] " +
            $"sent=[{Joined(sent).Replace("\r", "\\r").Replace("\x03", "<ETX>")}]";
        Assert.True(Joined(sent) == "\x03\x03", state);
    }
}