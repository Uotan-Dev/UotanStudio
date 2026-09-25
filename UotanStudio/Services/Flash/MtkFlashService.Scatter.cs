using System.Collections.ObjectModel;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

namespace UotanStudio.Services.Flash;

public static partial class MtkFlashService
{
    /// <summary>Starts an explicitly selected serial agent and prepares selected Scatter operations against live GPT.</summary>
    /// <remarks>
    /// The caller must authorize startup separately from execution. Selection and metadata
    /// collections are copied before asynchronous connection; concurrent mutation while copying
    /// is unsupported. Manifest file names are never resolved to local source paths.
    /// </remarks>
    /// <param name="serial">Explicit serial port and line settings.</param>
    /// <param name="startup">Explicit loader, agent stages and compatibility policy.</param>
    /// <param name="physicalPartition">Explicit physical user region to observe.</param>
    /// <param name="layout">Selected Scatter storage layout, not automatic platform authorization.</param>
    /// <param name="steps">Explicit operations and caller-selected sources.</param>
    /// <param name="cancellationToken">Cancellation for connection, metadata and source preflight.</param>
    /// <returns>An owned preview which performs storage writes only on a separate ExecuteAsync call.</returns>
    public static Task<MtkFlashPreparation> PrepareScatterAsync(SerialConnectionOptions serial,
        MtkXFlashBootstrapOptions startup, uint physicalPartition, MtkScatterStorage layout,
        IReadOnlyList<MtkScatterFlashStep> steps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(startup);
        return PrepareScatterAsync(token => MtkXFlashBootstrap.ConnectSerialAsync(serial, startup, token),
            physicalPartition, layout, steps, cancellationToken);
    }

    internal static async Task<MtkFlashPreparation> PrepareScatterAsync(Func<CancellationToken, Task<MtkSession>> connect,
        uint physicalPartition, MtkScatterStorage layout, IReadOnlyList<MtkScatterFlashStep> steps,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(layout.Partitions);
        ArgumentNullException.ThrowIfNull(steps);
        cancellationToken.ThrowIfCancellationRequested();
        if (steps.Count is 0 or > 256 || layout.Partitions.Count is 0 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), "Scatter selections or metadata exceed host limits.");
        }
        var selected = steps.ToArray();
        if (selected.Any(step => step is null))
        {
            throw new ArgumentException("A selected scatter operation is null.", nameof(steps));
        }
        var partitions = new MtkScatterPartition[layout.Partitions.Count];
        for (var index = 0; index < partitions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = layout.Partitions[index] ?? throw new ArgumentException("A scatter entry is null.", nameof(layout));
            partitions[index] = entry with { Fields = SnapshotScatterFields(entry.Fields) };
        }
        var captured = layout with
        {
            Fields = SnapshotScatterFields(layout.Fields),
            Partitions = Array.AsReadOnly(partitions)
        };
        return await PrepareCoreAsync(connect, physicalPartition,
            snapshot => MtkFlashPlan.Prepare(snapshot, captured, selected, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, string> SnapshotScatterFields(IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count > 64 || fields.Any(pair => pair.Key.Length > 128 || pair.Value is null || pair.Value.Length > 4096))
        {
            throw new ArgumentException("Scatter metadata fields exceed host limits.", nameof(fields));
        }
        return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(fields, StringComparer.Ordinal));
    }
}