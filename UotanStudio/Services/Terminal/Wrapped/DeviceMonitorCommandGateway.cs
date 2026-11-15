using System.Text;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Terminal.Wrapped;

/// <summary>
/// Production gateway that routes every wrapped command through the host's own
/// <see cref="DeviceMonitorService"/>, so the terminal panel and the rest of the app share one set of
/// device sessions. The shell panel opener is supplied by the composition layer because dock work
/// belongs on the UI thread.
/// <para>生产 gateway：把所有包装命令路由到宿主自己的 <see cref="DeviceMonitorService"/>，使终端面板与
/// 应用其余部分共享同一组设备会话。设备 shell 面板的打开器由组合层注入，因为停靠操作属于 UI 线程。</para>
/// </summary>
/// <param name="monitor">The host device monitor.<para>宿主设备监视器。</para></param>
/// <param name="openShellPanel">Opens the in-app device shell terminal on the UI thread.
/// <para>在 UI 线程打开应用内设备 shell 终端。</para></param>
internal sealed class DeviceMonitorCommandGateway(DeviceMonitorService monitor,
    Func<DetectedDevice, Task> openShellPanel) : IWrappedCommandGateway
{
    private const int ShellReadTimeoutMs = 10 * 60 * 1000;

    /// <inheritdoc/>
    public IReadOnlyList<DetectedDevice> Scan() => monitor.Scan();

    /// <inheritdoc/>
    public async Task<(int ExitCode, string Output)> ExecuteAdbShellAsync(DetectedDevice device, string command,
        CancellationToken cancellationToken = default)
    {
        var opened = RequireAdbLease(device);
        using var lease = opened.Lease;
        var result = await opened.Session.CreateShellClient(command)
            .ExecuteAsync(ShellReadTimeoutMs, cancellationToken).ConfigureAwait(false);
        var output = Encoding.UTF8.GetString(result.Stdout);
        var stderr = Encoding.UTF8.GetString(result.Stderr);
        return (result.ExitCode, stderr.Length == 0 ? output : $"{output}{stderr}");
    }

    /// <inheritdoc/>
    public string RunAdbService(string serial, string service) => monitor.RunAdbService(serial, service, ReadServiceText);

    private static string ReadServiceText(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().TrimEnd('\r', '\n');
    }

    /// <inheritdoc/>
    public async Task<int> StreamAdbShellAsync(DetectedDevice device, string command, Action<byte[]> onChunk,
        CancellationToken cancellationToken = default)
    {
        var opened = RequireAdbLease(device);
        using var lease = opened.Lease;
        return await opened.Session.CreateShellClient(command).ExecuteStreamingAsync(
            onChunk, onChunk, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task PushAdbFileAsync(DetectedDevice device, string localPath, string remotePath,
        CancellationToken cancellationToken = default) =>
        monitor.PushAdbFileAsync(device, localPath, remotePath, cancellationToken);

    /// <inheritdoc/>
    public Task PullAdbFileAsync(DetectedDevice device, string remotePath, string localPath,
        CancellationToken cancellationToken = default) =>
        monitor.PullAdbFileAsync(device, remotePath, localPath, cancellationToken);

    /// <inheritdoc/>
    public async Task<(bool Success, string Output)> InstallAdbAsync(DetectedDevice device, string apkPath,
        string options, CancellationToken cancellationToken = default)
    {
        var remote = $"/data/local/tmp/uotan-install-{Guid.NewGuid():N}.apk";
        await PushAdbFileAsync(device, apkPath, remote, cancellationToken).ConfigureAwait(false);
        try
        {
            var install = options.Length == 0
                ? $"pm install \"{remote}\""
                : $"pm install {options} \"{remote}\"";
            var (exitCode, output) = await ExecuteAdbShellAsync(device, install, cancellationToken)
                .ConfigureAwait(false);
            return (exitCode == 0 && output.Contains("Success", StringComparison.Ordinal), output);
        }
        finally
        {
            try { await ExecuteAdbShellAsync(device, $"rm -f \"{remote}\"", cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // The staged copy cleanup is best effort; a leftover file in /data/local/tmp is harmless.
                // 暂存副本清理尽力而为；/data/local/tmp 中的残留文件无害。
            }
        }
    }

    /// <inheritdoc/>
    public async Task<(bool Success, string Output)> UninstallAdbAsync(DetectedDevice device, string package,
        bool keepData, CancellationToken cancellationToken = default)
    {
        var command = keepData ? $"pm uninstall -k \"{package}\"" : $"pm uninstall \"{package}\"";
        var (exitCode, output) = await ExecuteAdbShellAsync(device, command, cancellationToken).ConfigureAwait(false);
        return (exitCode == 0, output);
    }

    /// <inheritdoc/>
    public Task<bool> RebootAsync(DetectedDevice device, string? mode, CancellationToken cancellationToken = default) =>
        monitor.RebootAsync(device, mode ?? string.Empty, cancellationToken);

    /// <inheritdoc/>
    public Task ConnectNetworkDeviceAsync(DeviceProtocolKind protocol, string address,
        CancellationToken cancellationToken = default) =>
        monitor.ConnectNetworkDeviceAsync(protocol, address, cancellationToken);

    /// <inheritdoc/>
    public async Task DisconnectAdbAsync(string? address, CancellationToken cancellationToken = default)
    {
        var targets = monitor.Scan().Where(candidate => candidate.Protocol == DeviceProtocolKind.Adb
            && candidate.Transport == DeviceTransportKind.Tcp
            && (address is null
                || string.Equals(candidate.Serial, address, StringComparison.Ordinal)
                || string.Equals(candidate.NetworkAlias, address, StringComparison.Ordinal)));
        foreach (var target in targets)
        {
            await monitor.DisconnectAdbAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<string> PairAdbAsync(string address, string code,
        CancellationToken cancellationToken = default)
    {
        var outcome = await monitor.PairWithCodeAsync(address, code, null, cancellationToken).ConfigureAwait(false);
        return $"Successfully paired to {outcome.Address}";
    }

    /// <inheritdoc/>
    public Task StopAdbServerAsync(CancellationToken cancellationToken = default) =>
        monitor.StopAdbServerAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task OpenDeviceShellAsync(DetectedDevice device) => await openShellPanel(device).ConfigureAwait(false);

    /// <inheritdoc/>
    public (string Value, bool Found) GetFastbootVariable(DetectedDevice device, string key)
    {
        var value = monitor.RunFastboot(device, session => session.GetVariable(key, useCache: false));
        return (value, value.Length > 0);
    }

    /// <inheritdoc/>
    public Dictionary<string, string> GetFastbootAllVariables(DetectedDevice device) =>
        monitor.RunFastboot(device, session => session.GetAllVariables());

    /// <inheritdoc/>
    public FastbootResponse RunFastbootCommand(DetectedDevice device, string command,
        CancellationToken cancellationToken = default) =>
        monitor.RunFastboot(device, session => session.ExecuteCommand(command, cancellationToken), cancellationToken);

    /// <inheritdoc/>
    public FastbootResponse FastbootDownload(DetectedDevice device, Stream stream, long length,
        CancellationToken cancellationToken = default) =>
        monitor.RunFastboot(device, session => session.DownloadData(stream, length, null, null, cancellationToken),
            cancellationToken);

    /// <inheritdoc/>
    public async Task<string> ExecuteHdcShellAsync(DetectedDevice device, string command,
        CancellationToken cancellationToken = default) =>
        await monitor.HdcShellAsync(device, command, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<int> StreamHdcShellAsync(DetectedDevice device, string command, Action<byte[]> onChunk,
        CancellationToken cancellationToken = default)
    {
        var opened = RequireHdcLease(device);
        using var lease = opened.Lease;
        return await Task.Run(() => opened.Session.CreateShellClient(command).ExecuteStreaming(
            onChunk, _ => { }, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task HdcSendFileAsync(DetectedDevice device, string localPath, string remotePath,
        CancellationToken cancellationToken = default) =>
        monitor.HdcSendFileAsync(device, localPath, remotePath, cancellationToken);

    /// <inheritdoc/>
    public Task HdcReceiveFileAsync(DetectedDevice device, string remotePath, string localPath,
        CancellationToken cancellationToken = default) =>
        monitor.HdcReceiveFileAsync(device, remotePath, localPath, cancellationToken);

    /// <inheritdoc/>
    public Task<string> HdcInstallAsync(DetectedDevice device, string path, bool replace,
        CancellationToken cancellationToken = default)
    {
        _ = replace;
        return monitor.HdcInstallAsync(device, path, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<string> HdcUninstallAsync(DetectedDevice device, string bundle, bool keepData,
        CancellationToken cancellationToken = default)
    {
        _ = keepData;
        return monitor.HdcUninstallAsync(device, bundle, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task HdcRebootAsync(DetectedDevice device, string? mode,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var opened = RequireHdcLease(device);
        using var lease = opened.Lease;
        opened.Session.CreateDeviceServices().Reboot(NormalizeHdcRebootMode(mode));
    }

    /// <inheritdoc/>
    public async Task HdcDisconnectAsync(DetectedDevice device, CancellationToken cancellationToken = default) =>
        await monitor.HdcDisconnectAsync(device, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public Task StopHdcServerAsync(CancellationToken cancellationToken = default) =>
        monitor.StopHdcServerAsync(cancellationToken);

    private (DeviceLease Lease, IAdbDeviceSession Session) RequireAdbLease(DetectedDevice device)
    {
        var opened = monitor.OpenAdbLease(device)
            ?? throw new IOException($"The wrapped adb command cannot open the session for '{device.Serial}'.");
        return opened;
    }

    private (HdcSessionHandle Lease, HdcSession Session) RequireHdcLease(DetectedDevice device)
    {
        var opened = monitor.OpenHdcLease(device)
            ?? throw new IOException($"The wrapped hdc command cannot open the session for '{device.Serial}'.");
        return opened;
    }

    private static string? NormalizeHdcRebootMode(string? mode) => mode switch
    {
        null or "" => null,
        "bootloader" => "bootloader",
        "recovery" => "recovery",
        _ => mode,
    };
}