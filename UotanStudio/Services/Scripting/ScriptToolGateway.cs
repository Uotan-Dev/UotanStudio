using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Mirroring;

namespace UotanStudio.Services.Scripting;

/// <summary>
/// Device and Flash operations the script tool surface dispatches to. The host supplies the default
/// gateway; tests inject a stub so tool keys, confirmation flags and argument mapping can be verified
/// without touching a real device.
/// </summary>
public interface IScriptToolGateway
{
    /// <summary>Returns the current device inventory.</summary>
    IReadOnlyList<DetectedDevice> Scan();

    /// <summary>Reboots one device into the given mode.</summary>
    Task<string> RebootAsync(DetectedDevice device, string mode, CancellationToken cancellationToken);

    /// <summary>Flashes one image to a partition.</summary>
    Task<FlashResult> FlashImageAsync(DetectedDevice device, string partition, string imagePath, CancellationToken cancellationToken);

    /// <summary>Erases one partition.</summary>
    Task<FlashResult> ErasePartitionAsync(DetectedDevice device, string partition, CancellationToken cancellationToken);

    /// <summary>Reads one property or fastboot variable.</summary>
    Task<FlashResult> GetDevicePropertyAsync(DetectedDevice device, string propertyName, CancellationToken cancellationToken);

    /// <summary>Reads the Fastboot partition table.</summary>
    Task<IReadOnlyList<(string Name, long Size)>> ReadPartitionTableAsync(DetectedDevice device, bool showAll, CancellationToken cancellationToken);

    /// <summary>Reads the current A/B slot.</summary>
    Task<string> GetCurrentSlotAsync(DetectedDevice device, CancellationToken cancellationToken);

    /// <summary>Switches to the other A/B slot.</summary>
    Task<string> SetOtherSlotAsync(DetectedDevice device, CancellationToken cancellationToken);

    /// <summary>Runs a TWRP-style recovery wipe.</summary>
    Task<string> RecoveryCleanAsync(DetectedDevice device, string mode, CancellationToken cancellationToken);

    /// <summary>Runs one shell command on an ADB target.</summary>
    Task<string> ShellAsync(DetectedDevice device, string command, CancellationToken cancellationToken);

    /// <summary>Starts one scrcpy mirror session using the bundled server jar.</summary>
    ManagedMirrorSession StartMirror(DetectedDevice device);
}

/// <summary>Default gateway backed by the application device monitor, flash service and mirror host.</summary>
public sealed class DeviceScriptToolGateway : IScriptToolGateway
{
    /// <summary>Shared default gateway instance.</summary>
    public static DeviceScriptToolGateway Default { get; } = new();

    private DeviceScriptToolGateway() { }

    /// <inheritdoc />
    public IReadOnlyList<DetectedDevice> Scan() => App.DeviceMonitor.Scan();

    /// <inheritdoc />
    public Task<string> RebootAsync(DetectedDevice device, string mode, CancellationToken cancellationToken)
        => RebootCoreAsync(device, mode, cancellationToken);

    /// <inheritdoc />
    public Task<FlashResult> FlashImageAsync(DetectedDevice device, string partition, string imagePath,
        CancellationToken cancellationToken)
        => FlashService.Default.FlashImageAsync(new ViewModels.Devices.DeviceInfo(device), partition, imagePath,
            null, cancellationToken);

    /// <inheritdoc />
    public Task<FlashResult> ErasePartitionAsync(DetectedDevice device, string partition,
        CancellationToken cancellationToken)
        => FlashService.Default.ErasePartitionAsync(new ViewModels.Devices.DeviceInfo(device), partition,
            null, cancellationToken);

    /// <inheritdoc />
    public Task<FlashResult> GetDevicePropertyAsync(DetectedDevice device, string propertyName,
        CancellationToken cancellationToken)
        => FlashService.Default.GetDevicePropertyAsync(new ViewModels.Devices.DeviceInfo(device), propertyName,
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<(string Name, long Size)>> ReadPartitionTableAsync(DetectedDevice device, bool showAll,
        CancellationToken cancellationToken)
        => FastbootPartitionReader.ReadAsync(device, showAll, cancellationToken);

    /// <inheritdoc />
    public Task<string> GetCurrentSlotAsync(DetectedDevice device, CancellationToken cancellationToken)
        => FastbootSlotService.GetCurrentSlotAsync(device, cancellationToken);

    /// <inheritdoc />
    public Task<string> SetOtherSlotAsync(DetectedDevice device, CancellationToken cancellationToken)
        => FastbootSlotService.SetOtherAsync(device, cancellationToken);

    /// <inheritdoc />
    public Task<string> RecoveryCleanAsync(DetectedDevice device, string mode, CancellationToken cancellationToken)
        => RecoveryCleanService.CleanAsync(device, mode, cancellationToken);

    /// <inheritdoc />
    public Task<string> ShellAsync(DetectedDevice device, string command, CancellationToken cancellationToken)
        => DeviceShellCommandService.ExecuteAsync(device, command, cancellationToken);

    /// <inheritdoc />
    public ManagedMirrorSession StartMirror(DetectedDevice device)
    {
        var options = new ScrcpyServerOptions();
        var session = App.DeviceMonitor.StartMirror(device, ScrcpyServerLocator.Resolve(), options);
        try
        {
            session.Connect();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static async Task<string> RebootCoreAsync(DetectedDevice device, string mode,
        CancellationToken cancellationToken)
    {
        await App.DeviceMonitor.RebootAsync(device, mode, cancellationToken).ConfigureAwait(false);
        return mode;
    }
}