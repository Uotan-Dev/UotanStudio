using System.Collections.Generic;

namespace UotanStudio.Abstractions.Devices
{
    /// <summary>
    /// <para><see cref="DeviceProtocol"/> 枚举扩展，提供 UI 可绑定的协议列表。</para>
    /// Extensions for <see cref="DeviceProtocol"/>, providing a UI-bindable list of protocols.
    /// </summary>
    public static class DeviceProtocolExtensions
    {
        /// <summary>
        /// <para>所有支持的设备通讯协议，按 UI 显示顺序排列。</para>
        /// All supported device protocols, ordered for UI display.
        /// </summary>
        public static IReadOnlyList<DeviceProtocol> AllProtocols { get; } = new[]
        {
            DeviceProtocol.Adb,
            DeviceProtocol.Fastboot,
            DeviceProtocol.Fastbootd,
            DeviceProtocol.Recovery,
            DeviceProtocol.EdlQcom,
            DeviceProtocol.MtkCom,
            DeviceProtocol.RkBootrom
        };

        /// <summary>
        /// <para>获取协议的本地化显示名称。</para>
        /// Gets the localized display name of the protocol.
        /// </summary>
        public static string GetDisplayName(this DeviceProtocol protocol) => protocol switch
        {
            DeviceProtocol.Adb => "ADB",
            DeviceProtocol.Fastboot => "Fastboot",
            DeviceProtocol.Fastbootd => "Fastbootd",
            DeviceProtocol.Recovery => "Recovery",
            DeviceProtocol.EdlQcom => "Qualcomm EDL",
            DeviceProtocol.MtkCom => "MTK Brom",
            DeviceProtocol.RkBootrom => "Rockchip BootROM",
            _ => protocol.ToString()
        };
    }
}
