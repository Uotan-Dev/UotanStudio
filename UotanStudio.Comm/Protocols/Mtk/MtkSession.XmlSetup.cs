using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Sets explicit XML runtime parameters and advances only after the complete END/START exchange.</summary>
    /// <remarks>The target may act on InitializeDram; the caller must authorize this operation.
    /// Completion is not independent proof of working DRAM, a started DA2 or storage geometry.</remarks>
    /// <param name="options">Immutable caller-selected runtime policy.</param>
    /// <param name="timeoutMs">Cooperative whole-exchange deadline, from 1 ms to 24 hours.</param>
    /// <param name="cancellationToken">Cancellation for waiting and the control exchange.</param>
    public Task ConfigureXmlRuntimeAsync(MtkXmlRuntimeOptions options, int timeoutMs = 120000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateXmlSetupTimeout(timeoutMs);
        return ExecuteXmlControlCommandsAsync([options.BuildCommand()], MtkSessionState.XmlControlReady,
            MtkSessionState.XmlRuntimeConfigured, cancellationToken, timeoutMs);
    }

    /// <summary>Advertises implemented callbacks and requests hardware initialization under one operation lease.</summary>
    /// <remarks>
    /// Only progress-report handling is advertised by default. When
    /// <see cref="MtkSession.HostFileStore"/> is attached, the file transfer and filesystem
    /// size callbacks are also declared and device-initiated requests are served through it;
    /// otherwise an unexpected request faults the session.
    /// Both complete command exchanges must succeed before advancing. This is the agent's
    /// acknowledgement, not a storage readiness or hardware interoperability guarantee.
    /// </remarks>
    /// <param name="timeoutMs">Cooperative deadline shared by both exchanges, from 1 ms to 24 hours.</param>
    /// <param name="cancellationToken">Cancellation for both command exchanges.</param>
    public Task InitializeXmlHardwareAsync(int timeoutMs = 120000, CancellationToken cancellationToken = default)
    {
        ValidateXmlSetupTimeout(timeoutMs);
        return ExecuteXmlControlCommandsAsync(XmlHardwareCommands(), MtkSessionState.XmlRuntimeConfigured,
            MtkSessionState.XmlHardwareInitialized, cancellationToken, timeoutMs);
    }

    /// <summary>Requests the newly started XML DA2's hardware initialization without enabling storage access.</summary>
    /// <remarks>Only progress callbacks are accepted during initialization. Authentication and storage
    /// discovery remain separate; acknowledgement does not independently verify hardware effects.</remarks>
    /// <param name="timeoutMs">Cooperative deadline shared by both exchanges, from 1 ms to 24 hours.</param>
    /// <param name="cancellationToken">Cancellation for capability and initialization exchanges.</param>
    public Task InitializeXmlStageTwoHardwareAsync(int timeoutMs = 120000, CancellationToken cancellationToken = default)
    {
        ValidateXmlSetupTimeout(timeoutMs);
        return ExecuteXmlControlCommandsAsync(XmlHardwareCommands(), MtkSessionState.XmlStageTwoStarted,
            MtkSessionState.XmlStageTwoHardwareInitialized, cancellationToken, timeoutMs);
    }

    private XElement[] XmlHardwareCommands() =>
    [
        // Capabilities are advertised only for callbacks the host can actually service;
        // without a host file store the session stays fail-closed.
        XmlHostCapabilities(
            download: HostFileStore is not null,
            upload: HostFileStore is not null,
            fileSize: HostFileStore is not null),
        new("da", new XElement("version", "1.0"), new XElement("command", "CMD:NOTIFY-INIT-HW"))
    ];

    private static XElement XmlHostCapabilities(bool download, bool upload = false, bool fileSize = false) => new("da", new XElement("version", "1.0"),
        new XElement("command", "CMD:HOST-SUPPORTED-COMMANDS"),
        new XElement("arg", new XElement("host_capability",
            (download ? "CMD:DOWNLOAD-FILE^1@" : "") + (fileSize ? "CMD:FILE-SYS-OPERATION^1@" : "")
            + "CMD:PROGRESS-REPORT^1@" + (upload ? "CMD:UPLOAD-FILE^1@" : ""))));

    private static void ValidateXmlSetupTimeout(int timeoutMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeoutMs, 86400000);
    }
}