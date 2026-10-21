using System.Globalization;
using System.IO.Ports;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Profile;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Firmware.VendorFirmware.MediaTek;
using UotanStudio.Services.Flash;

namespace UotanStudio.ViewModels.NodeViewModels;

internal enum MtkNodeProtocol
{
    XFlash,
    Xml,
    Legacy,
    Scatter
}

internal enum MtkNodeOperation
{
    Flash,
    Erase,
    Read,
    Backup,
    RepairGpt,
    FullDump,
    ReturnToBrom
}

internal static class MtkNodeSupport
{
    internal const int DefaultBaudRate = 921600;

    internal static string ProtocolText(MtkNodeProtocol protocol) => protocol switch
    {
        MtkNodeProtocol.Xml => "Xml",
        MtkNodeProtocol.Legacy => "Legacy",
        MtkNodeProtocol.Scatter => "Scatter",
        _ => "XFlash"
    };

    internal static string StorageText(MtkXFlashStorageKind storage) => storage switch
    {
        MtkXFlashStorageKind.Ufs => "Ufs",
        MtkXFlashStorageKind.Nor => "Nor",
        MtkXFlashStorageKind.Nand => "Nand",
        _ => "Emmc"
    };

    internal static bool TryParseProtocol(string? text, out MtkNodeProtocol protocol)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "":
            case "xflash":
                protocol = MtkNodeProtocol.XFlash;
                return true;
            case "xml":
                protocol = MtkNodeProtocol.Xml;
                return true;
            case "legacy":
                protocol = MtkNodeProtocol.Legacy;
                return true;
            case "scatter":
                protocol = MtkNodeProtocol.Scatter;
                return true;
            default:
                protocol = MtkNodeProtocol.XFlash;
                return false;
        }
    }

    internal static bool TryParseStorage(string? text, out MtkXFlashStorageKind storage)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "":
            case "emmc":
                storage = MtkXFlashStorageKind.Emmc;
                return true;
            case "ufs":
                storage = MtkXFlashStorageKind.Ufs;
                return true;
            case "nor":
                storage = MtkXFlashStorageKind.Nor;
                return true;
            case "nand":
                storage = MtkXFlashStorageKind.Nand;
                return true;
            default:
                storage = MtkXFlashStorageKind.Emmc;
                return false;
        }
    }

    internal static bool TryParseOperation(string? text, out MtkNodeOperation operation)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "":
            case "flash":
                operation = MtkNodeOperation.Flash;
                return true;
            case "erase":
                operation = MtkNodeOperation.Erase;
                return true;
            case "read":
                operation = MtkNodeOperation.Read;
                return true;
            case "backup":
                operation = MtkNodeOperation.Backup;
                return true;
            case "repairgpt":
            case "repair gpt":
            case "repair-gpt":
                operation = MtkNodeOperation.RepairGpt;
                return true;
            case "fulldump":
            case "full dump":
            case "full-dump":
                operation = MtkNodeOperation.FullDump;
                return true;
            case "returntobrom":
            case "return to brom":
            case "return-to-brom":
                operation = MtkNodeOperation.ReturnToBrom;
                return true;
            default:
                operation = MtkNodeOperation.Flash;
                return false;
        }
    }

    internal static bool IsStorageSupported(MtkNodeProtocol protocol, MtkXFlashStorageKind storage) => protocol switch
    {
        MtkNodeProtocol.Xml => storage is MtkXFlashStorageKind.Emmc or MtkXFlashStorageKind.Ufs,
        MtkNodeProtocol.Legacy => storage is MtkXFlashStorageKind.Emmc or MtkXFlashStorageKind.Nor
            or MtkXFlashStorageKind.Nand,
        MtkNodeProtocol.Scatter => storage is MtkXFlashStorageKind.Emmc or MtkXFlashStorageKind.Ufs,
        _ => true
    };

    internal static MtkDaProtocol ToDaProtocol(MtkNodeProtocol protocol) => protocol switch
    {
        MtkNodeProtocol.Xml => MtkDaProtocol.Xml,
        MtkNodeProtocol.Legacy => MtkDaProtocol.Legacy,
        _ => MtkDaProtocol.XFlash
    };

    internal static SerialConnectionOptions SerialOptions(string port, int baudRate) => new()
    {
        PortName = port,
        BaudRate = baudRate,
        Handshake = Handshake.None
    };

    internal static MtkLegacyOptions LegacyOptions() => new()
    {
        HostOs = HostOs
    };

    internal static MtkXmlBootstrapOptions XmlOptions() => new()
    {
        Runtime = new MtkXmlRuntimeOptions
        {
            Checksum = MtkXmlChecksumLevel.None,
            HostOs = HostOs,
            InitializeDram = true
        },
        SlaEnvelopeName = null,
        HostInfo = string.Empty
    };

    private static MtkXmlHostOs HostOs => OperatingSystem.IsWindows()
        ? MtkXmlHostOs.Windows
        : MtkXmlHostOs.Linux;

    internal static async Task<MtkAutoStartup> BuildStartupAsync(MtkNodeProtocol protocol,
        MtkXFlashStorageKind storage, string loaderPath, string? emiPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loaderPath);
        var profile = await MtkLoaderProfile.FromFileAsync(loaderPath, cancellationToken).ConfigureAwait(false);
        return new MtkAutoStartup
        {
            Profile = profile,
            StorageKind = storage,
            EmiParameters = protocol == MtkNodeProtocol.Xml || string.IsNullOrWhiteSpace(emiPath)
                ? null
                : ReadEmi(emiPath),
            Legacy = protocol == MtkNodeProtocol.Legacy ? LegacyOptions() : null,
            Xml = protocol == MtkNodeProtocol.Xml ? XmlOptions() : null
        };
    }

    internal static async Task<MtkSession> ConnectSerialAsync(string port, int baudRate, MtkNodeProtocol protocol,
        MtkXFlashStorageKind storage, string loaderPath, string? emiPath, CancellationToken cancellationToken)
    {
        var startup = await BuildStartupAsync(protocol, storage, loaderPath, emiPath, cancellationToken)
            .ConfigureAwait(false);
        return await MtkBootstrap.MatchSerialAsync(SerialOptions(port, baudRate), ToDaProtocol(protocol),
            startup, cancellationToken).ConfigureAwait(false);
    }

    internal static MtkEmiParameters ReadEmi(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using (var stream = File.OpenRead(path))
        {
            try
            {
                return MtkEmiReader.ReadPreloader(stream, 0, MtkEmiPayloadLayout.BootloaderInfo);
            }
            catch (InvalidDataException)
            {
            }
        }

        using var retry = File.OpenRead(path);
        return MtkEmiReader.ReadPreloader(retry, 0, MtkEmiPayloadLayout.BinaryParameters);
    }

    internal static uint ResolveXFlashPartition(MtkXFlashStorageKind storage, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && !string.Equals(requested.Trim(), "0", StringComparison.Ordinal))
        {
            if (!uint.TryParse(requested.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed == 0)
            {
                throw new ArgumentException("The XFlash physical partition must be a positive decimal identifier.");
            }

            return parsed;
        }

        return MtkScatterPackage.GetPhysicalPartition(storage);
    }

    internal static byte ResolveLegacyPartition(MtkXFlashStorageKind storage, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && !string.Equals(requested.Trim(), "0", StringComparison.Ordinal))
        {
            if (!byte.TryParse(requested.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed == 0)
            {
                throw new ArgumentException("The Legacy physical partition must be a positive decimal identifier.");
            }

            return parsed;
        }

        return MtkScatterPackage.GetLegacyPhysicalPartition(storage);
    }

    internal static string ResolveXmlRegion(MtkXFlashStorageKind storage, string? requested) =>
        string.IsNullOrWhiteSpace(requested) || string.Equals(requested.Trim(), "0", StringComparison.Ordinal)
            ? MtkScatterPackage.GetXmlRegionName(storage)
            : requested.Trim();

    internal static long PartitionLength(object snapshot, string partition) => snapshot switch
    {
        MtkGptSnapshot xflash => xflash.Table.FindPartition(partition).ByteLength,
        MtkLegacyGptSnapshot legacy => legacy.Table.FindPartition(partition).ByteLength,
        MtkXmlGptSnapshot xml => xml.Table.FindPartition(partition).ByteLength,
        _ => throw new ArgumentException("The GPT observation is not a supported MediaTek snapshot.", nameof(snapshot))
    };

    internal static int FindScatterRow(MtkScatterPackageResolution resolution, string partition)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var rows = resolution.Rows ?? throw new InvalidOperationException("The scatter resolution carries no rows.");
        var match = -1;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index] ?? throw new InvalidDataException("A scatter row is null.");
            var name = row.Partition?.Name;
            if (!string.Equals(name, partition, StringComparison.Ordinal))
            {
                continue;
            }

            if (match >= 0)
            {
                throw new InvalidDataException($"The scatter package declares more than one partition named '{partition}'.");
            }

            match = index;
        }

        return match >= 0
            ? match
            : throw new InvalidOperationException(
                $"The scatter package resolves no image for the partition '{partition}'.");
    }
}

internal sealed class MtkSessionHandle : IAsyncDisposable
{
    private MtkSession? _session;

    internal MtkSessionHandle(MtkSession session, string port, MtkNodeProtocol protocol,
        MtkXFlashStorageKind storage, string physicalPartition, string chip)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Port = port;
        Protocol = protocol;
        Storage = storage;
        PhysicalPartition = physicalPartition;
        Chip = chip;
    }

    internal MtkSession Session =>
        Volatile.Read(ref _session) ?? throw new ObjectDisposedException(nameof(MtkSessionHandle));

    internal bool IsDisposed => Volatile.Read(ref _session) is null;

    internal string Port { get; }

    internal MtkNodeProtocol Protocol { get; }

    internal MtkXFlashStorageKind Storage { get; }

    internal string PhysicalPartition { get; }

    internal string Chip { get; }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _session, null)?.Dispose();
        return ValueTask.CompletedTask;
    }
}