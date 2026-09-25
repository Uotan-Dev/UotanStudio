using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>Changes the selected device's A/B active slot through its managed Fastboot session.</summary>
public static class FastbootSlotService
{
    /// <summary>Switches from a to b or b to a; devices without a recognized current slot are rejected.</summary>
    public static Task<string> SetOtherAsync(DetectedDevice device, CancellationToken ct)
    {
        if (device.Protocol != DeviceProtocolKind.Fastboot)
        {
            throw new ArgumentException("A Fastboot device is required.", nameof(device));
        }

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return App.DeviceMonitor.RunFastboot(device, session =>
            {
                var slot = session.GetVariable("current-slot", useCache: false).Trim();
                var other = slot switch
                {
                    "a" => "b",
                    "b" => "a",
                    _ => throw new InvalidOperationException($"Unrecognized active slot: {slot}")
                };
                ct.ThrowIfCancellationRequested();
                session.SetActiveSlot(other).ThrowIfFailed();
                return other;
            }, ct);
        }, ct);
    }
}