using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Identifies an explicitly requested XML DA boot destination.
/// <para>标识显式请求的 XML DA 启动目标。</para></summary>
public enum MtkXmlBootMode
{
    /// <summary>Reboots without changing the target's boot-mode setting.
    /// <para>不更改目标的启动模式设置，直接重启。</para></summary>
    Normal,
    /// <summary>Requests Fastboot.
    /// <para>请求 Fastboot。</para></summary>
    Fastboot,
    /// <summary>Requests META mode.
    /// <para>请求 META 模式。</para></summary>
    Meta,
    /// <summary>Requests Android test mode.
    /// <para>请求 Android 测试模式。</para></summary>
    AndroidTest
}

/// <summary>Defines authorized boot-mode settings for one XML immediate reboot request.
/// <para>定义一次 XML 立即重启请求的授权启动模式设置。</para></summary>
public sealed record MtkXmlRebootOptions
{
    /// <summary>Gets the explicitly authorized boot destination.
    /// <para>获取显式授权的启动目标。</para></summary>
    /// <value>The selected mode; Normal preserves the target's existing mode.
    /// <para>选定的模式；Normal 保留目标已有模式。</para></value>
    public required MtkXmlBootMode Mode { get; init; }

    /// <summary>Gets whether the boot-mode request explicitly enables ADB.
    /// <para>获取启动模式请求是否显式启用 ADB。</para></summary>
    /// <value>False by default; true is invalid with Normal because no mode command is sent.
    /// <para>默认为 false；Normal 不发送模式命令，因此不允许 true。</para></value>
    public bool EnableAdb { get; init; }

    /// <summary>Gets whether the boot-mode request explicitly enables mobile logging.
    /// <para>获取启动模式请求是否显式启用移动端日志。</para></summary>
    /// <value>False by default; true is invalid with Normal because no mode command is sent.
    /// <para>默认为 false；Normal 不发送模式命令，因此不允许 true。</para></value>
    public bool EnableMobileLog { get; init; }

    internal XElement? BuildBootModeCommand()
    {
        var mode = Mode switch
        {
            MtkXmlBootMode.Normal => null,
            MtkXmlBootMode.Fastboot => "FASTBOOT",
            MtkXmlBootMode.Meta => "META",
            MtkXmlBootMode.AndroidTest => "ANDROID-TEST-MODE",
            _ => throw new ArgumentOutOfRangeException(nameof(Mode))
        };
        if (mode is null)
        {
            if (EnableAdb || EnableMobileLog)
            {
                throw new ArgumentException("Normal reboot does not send boot-mode flags.");
            }
            return null;
        }
        return new XElement("da", new XElement("version", "1.0"), new XElement("command", "CMD:SET-BOOT-MODE"),
            new XElement("arg", new XElement("mode", mode), new XElement("connect_type", "USB"),
                new XElement("mobile_log", EnableMobileLog ? "ON" : "OFF"), new XElement("adb", EnableAdb ? "ON" : "OFF")));
    }
}