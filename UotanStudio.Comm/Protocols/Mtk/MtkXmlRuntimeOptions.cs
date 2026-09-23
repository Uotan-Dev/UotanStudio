using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Explicit target runtime policy; acknowledging it does not prove DRAM or storage readiness.</summary>
public sealed record MtkXmlRuntimeOptions
{
    /// <summary>Gets the explicitly selected target checksum policy.</summary>
    public required MtkXmlChecksumLevel Checksum { get; init; }
    /// <summary>Gets the explicit host OS token, not an inferred target compatibility profile.</summary>
    public required MtkXmlHostOs HostOs { get; init; }
    /// <summary>Gets whether the caller authorizes the target's DRAM initialization request.</summary>
    public required bool InitializeDram { get; init; }
    /// <summary>Gets the target battery-presence policy.</summary>
    public MtkXmlBatterySetting Battery { get; init; } = MtkXmlBatterySetting.AutoDetect;
    /// <summary>Gets the target log verbosity.</summary>
    public MtkXmlLogLevel LogLevel { get; init; } = MtkXmlLogLevel.Info;
    /// <summary>Gets the target log channel.</summary>
    public MtkXmlLogChannel LogChannel { get; init; } = MtkXmlLogChannel.Uart;

    internal XElement BuildCommand()
    {
        var checksum = Checksum switch
        {
            MtkXmlChecksumLevel.None => "NONE",
            MtkXmlChecksumLevel.Usb => "USB",
            MtkXmlChecksumLevel.Storage => "STORAGE",
            MtkXmlChecksumLevel.UsbAndStorage => "USB-STORAGE",
            _ => throw new ArgumentOutOfRangeException(nameof(Checksum))
        };
        var host = HostOs switch
        {
            MtkXmlHostOs.Windows => "WINDOWS",
            MtkXmlHostOs.Linux => "LINUX",
            _ => throw new ArgumentOutOfRangeException(nameof(HostOs))
        };
        var battery = Battery switch
        {
            MtkXmlBatterySetting.AutoDetect => "AUTO-DETECT",
            MtkXmlBatterySetting.Present => "YES",
            MtkXmlBatterySetting.Absent => "NO",
            _ => throw new ArgumentOutOfRangeException(nameof(Battery))
        };
        var level = LogLevel switch
        {
            MtkXmlLogLevel.Trace => "TRACE",
            MtkXmlLogLevel.Debug => "DEBUG",
            MtkXmlLogLevel.Info => "INFO",
            MtkXmlLogLevel.Warn => "WARN",
            MtkXmlLogLevel.Error => "ERROR",
            _ => throw new ArgumentOutOfRangeException(nameof(LogLevel))
        };
        var channel = LogChannel switch
        {
            MtkXmlLogChannel.Uart => "UART",
            MtkXmlLogChannel.Usb => "USB",
            MtkXmlLogChannel.Both => "BOTH",
            _ => throw new ArgumentOutOfRangeException(nameof(LogChannel))
        };
        return new XElement("da", new XElement("version", "1.1"),
            new XElement("command", "CMD:SET-RUNTIME-PARAMETER"),
            new XElement("arg", new XElement("checksum_level", checksum), new XElement("battery_exist", battery),
                new XElement("da_log_level", level), new XElement("log_channel", channel), new XElement("system_os", host)),
            new XElement("adv", new XElement("initialize_dram", InitializeDram ? "YES" : "NO")));
    }
}

/// <summary>Target runtime checksum requests; these do not add host-side readback verification.</summary>
public enum MtkXmlChecksumLevel
{
    /// <summary>No target checksum request.</summary>
    None,
    /// <summary>Request USB checksums.</summary>
    Usb,
    /// <summary>Request storage checksums.</summary>
    Storage,
    /// <summary>Request both USB and storage checksums.</summary>
    UsbAndStorage
}

/// <summary>Host OS tokens supported by the XML runtime profile.</summary>
public enum MtkXmlHostOs
{
    /// <summary>Windows host.</summary>
    Windows,
    /// <summary>Linux host.</summary>
    Linux
}

/// <summary>Target battery-presence policy.</summary>
public enum MtkXmlBatterySetting
{
    /// <summary>Ask the agent to detect battery presence.</summary>
    AutoDetect,
    /// <summary>Declare a battery present.</summary>
    Present,
    /// <summary>Declare a battery absent.</summary>
    Absent
}

/// <summary>Target log verbosity.</summary>
public enum MtkXmlLogLevel
{
    /// <summary>Trace messages.</summary>
    Trace,
    /// <summary>Debug messages.</summary>
    Debug,
    /// <summary>Informational messages.</summary>
    Info,
    /// <summary>Warnings.</summary>
    Warn,
    /// <summary>Errors.</summary>
    Error
}

/// <summary>Target logging destination.</summary>
public enum MtkXmlLogChannel
{
    /// <summary>UART logging.</summary>
    Uart,
    /// <summary>USB logging.</summary>
    Usb,
    /// <summary>Both UART and USB logging.</summary>
    Both
}