using System.Globalization;
using System.Text;

namespace UotanStudio.Comm.Transports.Usb.Drivers;

/// <summary>
/// Builds the minimal in-box WinUSB INF text for one interface-level hardware id. The output is a
/// pure function of its arguments: no co-installer sections, <c>winusb.sys</c> is referenced from
/// the system driver store, and the device interface class GUID is written both as an
/// <c>AddInterface</c> registration and as the <c>DeviceInterfaceGUIDs</c> registry value.
/// <para>为一个接口级硬件 ID 构建最简 in-box WinUSB INF 文本。输出是参数的纯函数：没有
/// 协同安装段，<c>winusb.sys</c> 引用系统驱动仓库中的副本，设备接口类 GUID 同时写成
/// <c>AddInterface</c> 注册与 <c>DeviceInterfaceGUIDs</c> 注册表值。</para>
/// </summary>
internal static class WinUsbDriverInf
{
    /// <summary>The upper bound applied to the sanitized device description.
    /// <para>清洗后设备描述的长度上限。</para></summary>
    private const int MaxDeviceNameChars = 100;

    /// <summary>
    /// Renders the complete INF text for the request.
    /// <para>渲染请求对应的完整 INF 文本。</para>
    /// </summary>
    /// <param name="hardwareId">
    /// The INF match hardware id, for example <c>USB\VID_18D1&amp;PID_4EE7&amp;MI_01</c>.
    /// <para>INF 匹配用硬件 ID，例如 <c>USB\VID_18D1&amp;PID_4EE7&amp;MI_01</c>。</para>
    /// </param>
    /// <param name="deviceName">
    /// The device description used for the match-line symbol; line breaks are removed and the text
    /// is truncated to 100 characters.
    /// <para>匹配行符号使用的设备描述；换行会被移除，文本截断到 100 字符。</para>
    /// </param>
    /// <param name="interfaceGuid">
    /// The device interface class GUID registered for the interface.
    /// <para>为该接口注册的设备接口类 GUID。</para>
    /// </param>
    /// <param name="infFileName">
    /// The INF file name; the catalog name is its base with a <c>.cat</c> extension.
    /// <para>INF 文件名；目录名是去掉扩展名后加 <c>.cat</c>。</para>
    /// </param>
    /// <param name="utcToday">The UTC date written into <c>DriverVer</c>.
    /// <para>写入 <c>DriverVer</c> 的 UTC 日期。</para></param>
    /// <returns>The complete INF file text. <para>完整的 INF 文件文本。</para></returns>
    internal static string Build(string hardwareId, string deviceName, Guid interfaceGuid, string infFileName, DateTime utcToday)
    {
        var catalogName = Path.GetFileNameWithoutExtension(infFileName) + ".cat";
        var interfaceGuidText = "{" + interfaceGuid.ToString("D").ToUpperInvariant() + "}";
        var provider = "UotanStudio";
        var sanitizedDeviceName = SanitizeDeviceName(deviceName);
        if (sanitizedDeviceName.Length == 0)
        {
            sanitizedDeviceName = "UotanStudio USB Device";
        }
        var text = new StringBuilder()
            .Append("[Version]\n")
            .Append("Signature=\"$WINDOWS NT$\"\n")
            .Append("Class=USB\n")
            .Append("ClassGuid={36fc9e60-c465-11cf-8056-444553540000}\n")
            .Append("Provider=%ProviderName%\n")
            .Append("CatalogFile=").Append(catalogName).Append('\n')
            .Append("DriverVer=").Append(utcToday.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)).Append('\n')
            .Append('\n')
            .Append("[Manufacturer]\n")
            .Append("%ProviderName% = UotanStudioDevices,NTamd64.10,NTarm64.10\n")
            .Append('\n')
            .Append("[UotanStudioDevices.NTamd64.10]\n")
            .Append("%DeviceName% = USB_Install,").Append(EscapePercent(hardwareId)).Append('\n')
            .Append('\n')
            .Append("[UotanStudioDevices.NTarm64.10]\n")
            .Append("%DeviceName% = USB_Install,").Append(EscapePercent(hardwareId)).Append('\n')
            .Append('\n')
            .Append("[DestinationDirs]\n")
            .Append("DefaultDestDir = 12\n")
            .Append('\n')
            .Append("[USB_Install]\n")
            .Append('\n')
            .Append("[USB_Install.Services]\n")
            .Append("AddService = winusb,0x00000002,WinUsbServiceInstall\n")
            .Append('\n')
            .Append("[WinUsbServiceInstall]\n")
            .Append("DisplayName    = %ServiceName%\n")
            .Append("ServiceType    = 1\n")
            .Append("StartType      = 3\n")
            .Append("ErrorControl   = 1\n")
            .Append("ServiceBinary  = %12%\\WinUSB.sys\n")
            .Append('\n')
            .Append("[USB_Install.Interfaces]\n")
            .Append("AddInterface = %InterfaceGuid%,,USB_Install.AddInterface\n")
            .Append('\n')
            .Append("[USB_Install.AddInterface]\n")
            .Append("AddReg = USB_Install.InterfaceReg\n")
            .Append('\n')
            .Append("[USB_Install.InterfaceReg]\n")
            .Append("HKR,,DeviceInterfaceGUIDs,0x10000,%InterfaceGuid%\n")
            .Append('\n')
            .Append("[Strings]\n")
            .Append("ProviderName  = \"").Append(provider).Append("\"\n")
            .Append("DeviceName    = \"").Append(EscapePercent(sanitizedDeviceName)).Append("\"\n")
            .Append("ServiceName   = \"UotanStudio WinUSB\"\n")
            .Append("InterfaceGuid = \"").Append(interfaceGuidText).Append("\"\n");
        return text.ToString();
    }

    /// <summary>
    /// Escapes a literal percent sign as the INF double-percent form.
    /// <para>把字面百分号转义为 INF 的双百分号形式。</para>
    /// </summary>
    /// <param name="value">The raw INF token. <para>原始 INF 标记。</para></param>
    /// <returns>The escaped token. <para>转义后的标记。</para></returns>
    internal static string EscapePercent(string value)
        => value.Replace("%", "%%", StringComparison.Ordinal);

    /// <summary>
    /// Normalizes a device description for the INF strings section: drops control characters and
    /// line breaks from both ends and inside, collapses runs of blanks, and truncates to 100
    /// characters.
    /// <para>为 INF 字符串段规范化设备描述：移除首尾及内部的换行与控制字符，折叠连续空白，
    /// 并截断到 100 字符。</para>
    /// </summary>
    /// <param name="deviceName">The raw device description. <para>原始设备描述。</para></param>
    /// <returns>The sanitized description; empty when nothing printable remains.
    /// <para>清洗后的描述；没有可打印内容时为空字符串。</para></returns>
    internal static string SanitizeDeviceName(string deviceName)
    {
        var builder = new StringBuilder(deviceName.Length);
        foreach (var ch in deviceName)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(ch);
            }
        }

        var collapsed = CollapseSpaces(builder);
        return collapsed.Length > MaxDeviceNameChars ? collapsed[..MaxDeviceNameChars].TrimEnd() : collapsed.TrimEnd();
    }

    private static string CollapseSpaces(StringBuilder builder)
    {
        var result = new StringBuilder(builder.Length);
        var pendingSpace = false;
        foreach (var ch in builder.ToString())
        {
            if (ch == ' ')
            {
                pendingSpace = result.Length > 0;
            }
            else
            {
                if (pendingSpace)
                {
                    result.Append(' ');
                    pendingSpace = false;
                }

                result.Append(ch);
            }
        }

        return result.ToString();
    }
}