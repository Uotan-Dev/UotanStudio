using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Terminal.Wrapped;

namespace UotanStudio.Tests.Services;

internal sealed class WrappedCommandTestGateway : IWrappedCommandGateway
{
    internal List<DetectedDevice> Devices { get; } = [];

    internal List<(DetectedDevice Device, string Command)> ShellCalls { get; } = [];

    internal List<(DetectedDevice Device, string Local, string Remote)> PushCalls { get; } = [];

    internal List<(DetectedDevice Device, string Remote, string Local)> PullCalls { get; } = [];

    internal List<string> InstallOptions { get; } = [];

    internal List<(string Serial, string Service)> AdbServiceCalls { get; } = [];

    internal List<DetectedDevice> OpenedDeviceShells { get; } = [];

    internal List<string> StopServerCalls { get; } = [];

    internal List<string> FastbootCommands { get; } = [];

    internal int NextShellExitCode { get; set; }

    internal string NextShellOutput { get; set; } = string.Empty;

    internal Action? OnShellCall { get; set; }

    internal TimeSpan ShellDelay { get; set; }

    internal Dictionary<string, string> FastbootVariables { get; } = new();

    internal FastbootResponse NextFastbootResponse { get; set; } = new()
    {
        State = FastbootResponseState.Success,
        Text = string.Empty,
    };

    public IReadOnlyList<DetectedDevice> Scan() => Devices;

    public async Task<(int ExitCode, string Output)> ExecuteAdbShellAsync(DetectedDevice device, string command,
        CancellationToken cancellationToken = default)
    {
        OnShellCall?.Invoke();
        if (ShellDelay > TimeSpan.Zero)
        {
            await Task.Delay(ShellDelay, cancellationToken).ConfigureAwait(false);
        }
        ShellCalls.Add((device, command));
        return (NextShellExitCode, NextShellOutput);
    }

    public Task<int> StreamAdbShellAsync(DetectedDevice device, string command, Action<byte[]> onChunk,
        CancellationToken cancellationToken = default)
    {
        ShellCalls.Add((device, command));
        onChunk("streamed"u8.ToArray());
        return Task.FromResult(0);
    }

    public string RunAdbService(string serial, string service)
    {
        AdbServiceCalls.Add((serial, service));
        return $"service {service} ok";
    }

    public Task PushAdbFileAsync(DetectedDevice device, string localPath, string remotePath,
        CancellationToken cancellationToken = default)
    {
        PushCalls.Add((device, localPath, remotePath));
        return Task.CompletedTask;
    }

    public Task PullAdbFileAsync(DetectedDevice device, string remotePath, string localPath,
        CancellationToken cancellationToken = default)
    {
        PullCalls.Add((device, remotePath, localPath));
        return Task.CompletedTask;
    }

    public async Task<(bool Success, string Output)> InstallAdbAsync(DetectedDevice device, string apkPath,
        string options, CancellationToken cancellationToken = default)
    {
        InstallOptions.Add(options);
        await PushAdbFileAsync(device, apkPath, "/data/local/tmp/staged.apk", cancellationToken).ConfigureAwait(false);
        return (true, "Success");
    }

    public Task<(bool Success, string Output)> UninstallAdbAsync(DetectedDevice device, string package, bool keepData,
        CancellationToken cancellationToken = default)
    {
        ShellCalls.Add((device, keepData ? $"pm uninstall -k {package}" : $"pm uninstall {package}"));
        return Task.FromResult((true, "Success"));
    }

    public Task<bool> RebootAsync(DetectedDevice device, string? mode, CancellationToken cancellationToken = default)
    {
        AdbServiceCalls.Add((device.Serial, $"reboot:{mode}"));
        return Task.FromResult(true);
    }

    internal bool ConnectSucceeds { get; set; }

    public Task ConnectNetworkDeviceAsync(DeviceProtocolKind protocol, string address,
        CancellationToken cancellationToken = default) => ConnectSucceeds
        ? Task.CompletedTask
        : Task.FromException(new IOException("cannot connect to 192.168.1.50:5555: connection refused"));

    public Task DisconnectAdbAsync(string? address, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<string> PairAdbAsync(string address, string code, CancellationToken cancellationToken = default) =>
        Task.FromResult($"Successfully paired to {address}");

    public Task StopAdbServerAsync(CancellationToken cancellationToken = default)
    {
        StopServerCalls.Add("adb");
        return Task.CompletedTask;
    }

    public Task OpenDeviceShellAsync(DetectedDevice device)
    {
        OpenedDeviceShells.Add(device);
        return Task.CompletedTask;
    }

    public (string Value, bool Found) GetFastbootVariable(DetectedDevice device, string key) =>
        FastbootVariables.TryGetValue(key, out var value) ? (value, true) : (string.Empty, false);

    public Dictionary<string, string> GetFastbootAllVariables(DetectedDevice device) => new(FastbootVariables);

    public FastbootResponse RunFastbootCommand(DetectedDevice device, string command,
        CancellationToken cancellationToken = default)
    {
        FastbootCommands.Add(command);
        return NextFastbootResponse;
    }

    public FastbootResponse FastbootDownload(DetectedDevice device, Stream stream, long length,
        CancellationToken cancellationToken = default)
    {
        FastbootCommands.Add($"download:{length}");
        return NextFastbootResponse;
    }

    public Task<string> ExecuteHdcShellAsync(DetectedDevice device, string command,
        CancellationToken cancellationToken = default)
    {
        ShellCalls.Add((device, command));
        return Task.FromResult("hdc shell output");
    }

    public Task<int> StreamHdcShellAsync(DetectedDevice device, string command, Action<byte[]> onChunk,
        CancellationToken cancellationToken = default)
    {
        ShellCalls.Add((device, command));
        onChunk("hdc streamed"u8.ToArray());
        return Task.FromResult(0);
    }

    public Task HdcSendFileAsync(DetectedDevice device, string localPath, string remotePath,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task HdcReceiveFileAsync(DetectedDevice device, string remotePath, string localPath,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string> HdcInstallAsync(DetectedDevice device, string path, bool replace,
        CancellationToken cancellationToken = default) => Task.FromResult("install bundle successfully");

    public Task<string> HdcUninstallAsync(DetectedDevice device, string bundle, bool keepData,
        CancellationToken cancellationToken = default) => Task.FromResult("uninstall bundle successfully");

    public Task HdcRebootAsync(DetectedDevice device, string? mode, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task HdcDisconnectAsync(DetectedDevice device, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task StopHdcServerAsync(CancellationToken cancellationToken = default)
    {
        StopServerCalls.Add("hdc");
        return Task.CompletedTask;
    }

    internal static DetectedDevice MakeDevice(DeviceProtocolKind protocol, string serial, string state = "device",
        DeviceTransportKind transport = DeviceTransportKind.Usb) => new()
        {
            Key = protocol switch
            {
                DeviceProtocolKind.Adb => "adb:" + serial,
                DeviceProtocolKind.Fastboot => "fastboot:" + serial,
                DeviceProtocolKind.Hdc => "hdc:" + serial,
                _ => serial,
            },
            Serial = serial,
            Protocol = protocol,
            Transport = transport,
            State = state,
        };
}