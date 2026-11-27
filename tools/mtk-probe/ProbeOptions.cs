using System.Globalization;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Firmware.VendorFirmware.MediaTek;

namespace UotanStudio.Tools.MtkProbe;

internal sealed record ProbeOptions
{
    internal MtkDaProtocol Protocol { get; init; } = MtkDaProtocol.XFlash;

    internal string? Loader { get; init; }

    internal string? Port { get; init; }

    internal int PortWaitSeconds { get; init; } = 300;

    internal int PollIntervalMs { get; init; } = 100;

    internal HashSet<string> KnownPorts { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    internal int BaudRate { get; init; } = 921600;

    internal int TransferTimeoutMs { get; init; } = 30000;

    internal int OverallTimeoutMs { get; init; } = 6 * 60 * 60 * 1000;

    internal string OutputPath { get; init; } = string.Empty;

    internal IReadOnlyList<string> ExcludedPartitions { get; init; } = ["userdata", "data", "metadata"];

    internal MtkXFlashStorageKind StorageKind { get; init; } = MtkXFlashStorageKind.Emmc;

    internal uint GptPartitionId { get; init; } = MtkLegacyEmmcRegion.User;

    internal string XmlRegionName { get; init; } = "EMMC-USER";

    internal long? LimitBytes { get; init; }

    internal bool ShowHelp { get; init; }

    internal bool ListLoaders { get; init; }

    internal bool Trace { get; init; }

    internal bool Scan { get; init; }

    internal bool Dump { get; init; }

    internal bool Table { get; init; }

    internal MtkXmlHostOs HostOs { get; init; } = MtkXmlHostOs.Windows;

    internal (ushort Hw, ushort Sub, ushort Ver)? ProbeIdentity { get; init; }

    internal TimeSpan OverallTimeout => TimeSpan.FromMilliseconds(OverallTimeoutMs);

    internal TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollIntervalMs);

    internal MtkBootstrapOptions BuildBootstrap(DaMatch match) => new()
    {
        LoaderPath = match.LoaderPath,
        DirectoryLayout = MtkDaDirectoryLayout.Auto,
        EntryIndex = match.EntryIndex,
        StageOneRegionIndex = match.StageOneRegion,
        StageTwoRegionIndex = match.StageTwoRegion,
        StageTwoContent = MtkDaRegionContent.Complete,
        StorageKind = StorageKind,
        // The container ships one entry per chip family with the lowest revisions it still serves, so an
        // entry is usable when its revisions do not exceed the device's. Exact matching rejects every
        // real device whose revision is newer than the entry.
        // 容器为每个芯片家族只提供一条条目、并使用它所支持的最低版本，因此当条目版本不超过设备版本时即可用。
        // 精确匹配会拒绝所有版本比条目更新的真实设备。
        IdentityMatch = MtkDaIdentityMatch.MinimumRevisions,
        TransferTimeoutMs = TransferTimeoutMs,
        HandshakeTimeoutMs = 2000,
        // The Legacy agent negotiates against an explicit host platform byte; Windows is 0x0C.
        // Legacy 代理依据显式的主机平台字节进行协商；Windows 取 0x0C。
        Legacy = Protocol == MtkDaProtocol.Legacy
            ? new MtkLegacyOptions { HostOs = HostOs, StartupTimeoutMs = TransferTimeoutMs }
            : null
    };

    internal static ProbeOptions Parse(string[] args)
    {
        var options = new ProbeOptions();
        HashSet<string>? known = null;
        var showHelp = args.Length == 0;

        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            string Next() => index + 1 < args.Length
                ? args[++index]
                : throw new ArgumentException($"{current} needs a value.");

            options = current switch
            {
                "--help" or "-h" => options with { ShowHelp = true },
                "--list-loaders" => options with { ListLoaders = true },
                "--trace" => options with { Trace = true },
                "--scan" => options with { Scan = true },
                "--dump" => options with { Dump = true },
                "--table" => options with { Table = true },
                "--try-hw" => options with { ProbeIdentity = ParseIdentity(Next()) },
                "--loader" => options with { Loader = Next() },
                "--out" => options with { OutputPath = Next() },
                "--protocol" => options with { Protocol = ParseProtocol(Next()) },
                "--port" => options with { Port = Next() },
                "--storage" => options with { StorageKind = ParseStorage(Next()) },
                "--baud" => options with { BaudRate = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--wait" => options with { PortWaitSeconds = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--limit" => options with { LimitBytes = long.Parse(Next(), CultureInfo.InvariantCulture) },
                _ => throw new ArgumentException($"Unknown argument '{current}'.")
            };
        }

        if (showHelp)
        {
            return options with { ShowHelp = true };
        }

        known = new HashSet<string>(SerialPortNames(), StringComparer.OrdinalIgnoreCase);
        var output = options.OutputPath;
        if (string.IsNullOrWhiteSpace(output))
        {
            output = Path.Combine(Path.GetTempPath(),
                "mtk-probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        }

        return options with { OutputPath = output, KnownPorts = known };
    }

    private static (ushort Hw, ushort Sub, ushort Ver) ParseIdentity(string value)
    {
        var parts = value.Split(',');
        if (parts.Length != 3)
        {
            throw new ArgumentException("--try-hw expects hw,sub,ver as three hex values.");
        }

        static ushort Hex(string text) =>
            ushort.Parse(text.Trim().Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        return (Hex(parts[0]), Hex(parts[1]), Hex(parts[2]));
    }

    private static MtkDaProtocol ParseProtocol(string value) => value.ToLowerInvariant() switch
    {
        "xflash" => MtkDaProtocol.XFlash,
        "legacy" => MtkDaProtocol.Legacy,
        "xml" => MtkDaProtocol.Xml,
        var other => throw new ArgumentException($"Unknown protocol '{other}'.")
    };

    private static MtkXFlashStorageKind ParseStorage(string value) => value.ToLowerInvariant() switch
    {
        "emmc" => MtkXFlashStorageKind.Emmc,
        "ufs" => MtkXFlashStorageKind.Ufs,
        "nand" => MtkXFlashStorageKind.Nand,
        "nor" => MtkXFlashStorageKind.Nor,
        var other => throw new ArgumentException($"Unknown storage kind '{other}'.")
    };

    private static string[] SerialPortNames()
    {
        try
        {
            return System.IO.Ports.SerialPort.GetPortNames();
        }
        catch (Exception)
        {
            return [];
        }
    }

    internal static void PrintUsage()
    {
        Console.WriteLine("""
            mtk-probe — MTK device discovery, bootstrap, partition table, backup and reboot.

            The probe reads the chip identity over the preloader serial handshake, picks a matching
            loader entry from the bundled DA containers by itself, and derives the protocol.

            Optional:
              --loader <path>      Force one DA container instead of scanning the candidates.
              --protocol <name>    xflash (default) | legacy | xml.
              --storage <kind>     emmc (default) | ufs | nand | nor.
              --port <COMn>        Skip discovery and use this port.
              --baud <rate>        Serial rate, default 921600.
              --wait <seconds>     Port wait budget, default 300.
              --out <dir>          Backup directory; defaults to %TEMP%\mtk-probe-<timestamp>.
              --limit <bytes>      Skip partitions larger than this.
              --list-loaders       Print every bundled container's entries and exit.
              --trace              Log every serial transaction with hex payloads.
              --scan               Read-only sampling of every eMMC region; no backup, no reboot.
              --dump               Dump every readable eMMC region whole, then reboot. For devices whose
                                   user area carries no readable partition table.
              --table              Read and print the partition table of the selected region only; no
                                   backup, no reboot. Use it to verify table detection on real hardware.
              --try-hw hw,sub,ver   Resolve a loader for this identity without a device.

            Attach the device only AFTER the probe prints its waiting line: the preloader port
            usually exists for a few seconds only.
            """);
    }
}