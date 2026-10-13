using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>Reads and changes the selected device's A/B active slot through its managed Fastboot session.<para>经由受管 Fastboot 会话读取并改换所选设备的 A/B 活动槽位。</para></summary>
public static class FastbootSlotService
{
    /// <summary>Reads the current active slot reported by the selected Fastboot device.<para>读取所选 Fastboot 设备上报的当前活动槽位。</para></summary>
    /// <param name="device">The Fastboot device to query.<para>要查询的 Fastboot 设备。</para></param>
    /// <param name="ct">Token cancelling the read.<para>用于取消读取的令牌。</para></param>
    /// <returns>The trimmed slot identifier reported by the device.<para>设备上报并去除空白的槽位标识。</para></returns>
    public static Task<string> GetCurrentSlotAsync(DetectedDevice device, CancellationToken ct)
    {
        if (device.Protocol != DeviceProtocolKind.Fastboot)
        {
            throw new ArgumentException("A Fastboot device is required.", nameof(device));
        }

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return App.DeviceMonitor.RunFastboot(device,
                session => session.GetVariable("current-slot", useCache: false).Trim(), ct);
        }, ct);
    }

    /// <summary>Switches from a to b or b to a; devices without a recognized current slot are rejected.<para>在 a 与 b 之间切换，无法识别当前槽位的设备会被拒绝。</para></summary>
    /// <param name="device">The Fastboot device whose slot changes.<para>要改换槽位的 Fastboot 设备。</para></param>
    /// <param name="ct">Token cancelling the change.<para>用于取消改换的令牌。</para></param>
    /// <returns>The slot activated by the change.<para>本次改换激活的槽位。</para></returns>
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