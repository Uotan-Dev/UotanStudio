using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using UotanStudio.Comm;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Protocols.Scrcpy;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Services.Flash;


namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    public async Task StartLogcatStreamingAsync(DetectedDevice device, Action<AdbLogcatEntry> onEntry, CancellationToken cancellationToken)
    {
        if (device.Protocol == DeviceProtocolKind.Hdc)
        {
            var decoder = new AdbLogcatLineDecoder(line => onEntry(AdbLogcatEntry.Parse(line)));
            try
            {
                if (UseHdcServerFor(device))
                {
                    using var hdc = new HdcServerClient();
                    await hdc.ExecuteStreamingAsync(device.Serial, "shell hilog -v year",
                        bytes => decoder.Feed(bytes), cancellationToken).ConfigureAwait(false);
                }
                else
                    await Task.Run(() =>
                    {
                        using var session = HdcDirectSessionPool.Acquire(device);
                        session.CreateShellClient("hilog -v year").ExecuteStreaming(bytes => decoder.Feed(bytes), null, cancellationToken);
                    }, cancellationToken).ConfigureAwait(false);
            }
            finally { if (!cancellationToken.IsCancellationRequested) decoder.Feed([], flush: true); }
            return;
        }
        if (device.Protocol != DeviceProtocolKind.Adb)
            throw new ArgumentException("Logcat requires an ADB device.", nameof(device));
        cancellationToken.ThrowIfCancellationRequested();
        var client = AdbServerClient;
        var serverDevice = ResolveAdbServerTarget(client, device);
        if (client is not null && serverDevice is not null)
        {
            var reader = new AdbLogcatReader(async (command, onLine, token) =>
            {
                var receiver = new AdvancedSharpAdbClient.Receivers.FunctionOutputReceiver(line =>
                {
                    if (token.IsCancellationRequested) return false;
                    onLine(line);
                    return true;
                });
                await client.ExecuteRemoteCommandAsync(command, serverDevice, receiver,
                    System.Text.Encoding.UTF8, token).ConfigureAwait(false);
            });
            await reader.RunAsync(onEntry, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var session = OpenAdbSession(device)
            ?? throw new InvalidOperationException("Unable to open an ADB session for the device.");
        await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var directReader = new AdbLogcatReader(async (command, onLine, token) =>
        {
            var stdout = new AdbLogcatLineDecoder(onLine);
            var stderr = new AdbLogcatLineDecoder(onLine);
            try
            {
                var shell = session.CreateShellClient(command);
                var exitCode = await Task.Run(() => shell.ExecuteStreaming(
                    buffer => stdout.Feed(buffer), buffer => stderr.Feed(buffer), token), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (exitCode != 0) throw new InvalidOperationException($"ADB shell exited with code {exitCode}.");
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    stdout.Feed([], flush: true);
                    stderr.Feed([], flush: true);
                }
            }
        });
        await directReader.RunAsync(onEntry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears all device logcat buffers, using adb server or direct USB.
    /// <para>通过 adb server 或 USB 直连清空设备端全部 logcat 缓冲区；失败时抛出异常。</para>
    /// </summary>
    public async Task ClearLogcatBuffersAsync(DetectedDevice device, CancellationToken cancellationToken = default)
    {
        if (device.Protocol == DeviceProtocolKind.Hdc)
        {
            await HdcDeviceService.FromSettings().ShellAsync(device, "hilog -r", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (device.Protocol != DeviceProtocolKind.Adb)
            throw new ArgumentException("Logcat requires an ADB device.", nameof(device));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        const string successMarker = "__UOTAN_LOGCAT_CLEARED__";
        // The legacy adb-server shell API does not expose the remote exit code.
        const string command = "logcat -b all -c && echo " + successMarker;
        var confirmed = false;
        var diagnostic = new System.Text.StringBuilder();
        void OnLine(string line)
        {
            if (line.Trim() == successMarker) confirmed = true;
            else if (diagnostic.Length < 4096)
                diagnostic.AppendLine(line[..Math.Min(line.Length, 4096 - diagnostic.Length)]);
        }

        try
        {
            token.ThrowIfCancellationRequested();
            var client = AdbServerClient;
            var serverDevice = ResolveAdbServerTarget(client, device);
            var exitCode = 0;
            if (client is not null && serverDevice is not null)
            {
                var receiver = new AdvancedSharpAdbClient.Receivers.FunctionOutputReceiver(line =>
                {
                    if (token.IsCancellationRequested) return false;
                    OnLine(line);
                    return true;
                });
                await client.ExecuteRemoteCommandAsync(command, serverDevice, receiver,
                    System.Text.Encoding.UTF8, token).ConfigureAwait(false);
            }
            else
            {
                using var session = OpenAdbSession(device)
                    ?? throw new InvalidOperationException("Unable to open an ADB session for the device.");
                await session.ConnectAsync(token).ConfigureAwait(false);
                var stdout = new AdbLogcatLineDecoder(OnLine);
                var stderr = new AdbLogcatLineDecoder(OnLine);
                var shell = session.CreateShellClient(command);
                exitCode = await Task.Run(() => shell.ExecuteStreaming(
                    buffer => stdout.Feed(buffer), buffer => stderr.Feed(buffer), token), token).ConfigureAwait(false);
                stdout.Feed([], flush: true);
                stderr.Feed([], flush: true);
            }
            token.ThrowIfCancellationRequested();
            if (!confirmed || exitCode != 0)
                throw new InvalidOperationException(diagnostic.Length > 0 ? diagnostic.ToString().Trim()
                    : $"Logcat buffer clear was not confirmed (exit code {exitCode}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Clearing logcat buffers timed out.");
        }
    }

}