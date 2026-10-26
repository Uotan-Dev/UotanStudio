using System.Text;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Terminal;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class DeviceShellConnectionTests
{
    private static async Task<string> ReadTextAsync(Stream stream, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new byte[256];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                return text.ToString();
            }

            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }

    [Fact]
    public async Task Relay_EchoedChunk_DeliversShellTextAndExitZero()
    {
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new DeviceShellConnection(async (input, output, token) =>
        {
            var buffer = new byte[128];
            var count = await input.ReadAsync(buffer, token);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        });
        connection.Exited += (_, code) => exited.TrySetResult(code);

        await connection.WriterStream.WriteAsync(Encoding.UTF8.GetBytes("dada:/ $ "),
            TestContext.Current.CancellationToken);
        var text = await ReadTextAsync(connection.ReaderStream, TestContext.Current.CancellationToken);

        Assert.Equal("dada:/ $ ", text);
        Assert.Equal(0, await exited.Task.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, connection.ExitCode);
        Assert.Equal(0, await connection.ReaderStream.ReadAsync(new byte[8], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Relay_Failure_WritesReasonIntoTerminalAndReportsExitOne()
    {
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new DeviceShellConnection((_, _, _) =>
            Task.FromException(new IOException("The selected server target is no longer available.")));
        connection.Exited += (_, code) => exited.TrySetResult(code);

        var text = await ReadTextAsync(connection.ReaderStream, TestContext.Current.CancellationToken);

        Assert.Contains("no longer available", text, StringComparison.Ordinal);
        Assert.Equal(1, await exited.Task.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, connection.ExitCode);
    }

    [Fact]
    public async Task Kill_RunningRelay_EndsTerminalStreamAndStopsRelay()
    {
        using var connection = new DeviceShellConnection((_, _, token) => Task.Delay(Timeout.Infinite, token));

        connection.Kill();

        Assert.Equal(0, await connection.ReaderStream.ReadAsync(new byte[8], TestContext.Current.CancellationToken));
        Assert.True(connection.WaitForExit(5000));
    }

    [Fact]
    public async Task Connection_RealAdbTarget_ProducesShellOutput()
    {
        var target = Environment.GetEnvironmentVariable("UOTAN_DEVICE_SHELL_TARGET");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(target),
            "UOTAN_DEVICE_SHELL_TARGET is not set to an ADB host:port target.");

        using var monitor = DeviceMonitorService.Create();
        var device = monitor.Scan().FirstOrDefault(candidate =>
            candidate.Protocol == DeviceProtocolKind.Adb &&
            string.Equals(candidate.Serial, target, StringComparison.Ordinal));
        Assert.SkipWhen(device is null, $"The ADB target '{target}' is not detectable on this host.");

        using var connection = DeviceShellService.CreateConnection(monitor, device!);
        await connection.WriterStream.WriteAsync(Encoding.UTF8.GetBytes("echo uotan-shell-ok\r\n"),
            TestContext.Current.CancellationToken);

        var text = new StringBuilder();
        var buffer = new byte[512];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (!text.ToString().Contains("uotan-shell-ok", StringComparison.Ordinal))
        {
            var read = await connection.ReaderStream.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                break;
            }

            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        Assert.Contains("uotan-shell-ok", text.ToString(), StringComparison.Ordinal);
    }
}