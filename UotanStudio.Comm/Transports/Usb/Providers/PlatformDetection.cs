using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Transports.Usb.Providers;

/// <summary>
/// Host-platform detection used by the platform USB providers (<see cref="AndroidUsbApiProvider"/>,
/// <see cref="HarmonyUsbApiProvider"/> and the native provider).
/// <para>用于平台 USB 提供器（Android、鸿蒙等）的宿主平台检测。</para>
/// </summary>
internal static class PlatformDetection
{
    /// <summary>
    /// Gets whether the current runtime is Android.
    /// <para>获取当前运行时是否为 Android。</para>
    /// </summary>
    public static bool IsAndroidOs => OperatingSystem.IsAndroid();

    /// <summary>
    /// Gets whether the current runtime is HarmonyOS (OpenHarmony). Detection is RID-based:
    /// OpenHarmony builds carry an <c>ohos</c>/<c>harmony</c> RID segment such as <c>ohos-arm64</c>.
    /// <para>获取当前运行时是否为鸿蒙系统（OpenHarmony）。检测基于 RID：OpenHarmony 的构建带有
    /// <c>ohos</c>/<c>harmony</c> RID 段，如 <c>ohos-arm64</c>。</para>
    /// </summary>
    /// <remarks>
    /// There is no official <c>OperatingSystem.IsHarmonyOs()</c> yet; hosts that know their runtime can
    /// override via <c>UsbCommunicationLayer.RegisterProvider</c> before the first enumeration.
    /// <para>尚无官方的 <c>OperatingSystem.IsHarmonyOs()</c>；宿主知道运行时时可在首次枚举前通过
    /// <c>UsbCommunicationLayer.RegisterProvider</c> 覆盖。</para>
    /// </remarks>
    public static bool IsHarmonyOs
    {
        get
        {
            var rid = RuntimeInformation.RuntimeIdentifier;
            return rid.Contains("ohos", StringComparison.OrdinalIgnoreCase) ||
                   rid.Contains("harmony", StringComparison.OrdinalIgnoreCase);
        }
    }
}