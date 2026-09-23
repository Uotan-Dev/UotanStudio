using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Services.Flash;

/// <summary>Host orchestration for explicitly selected serial XFlash targets, separate from USB device discovery.</summary>
public static partial class MtkFlashService
{
    /// <summary>Starts the selected agent, reads GPT and prepares a preview without storage writes or erases.</summary>
    /// <remarks>Connecting and executing agents can affect the device. The host must authorize startup separately
    /// from confirming execution of the returned plan; this API does not supply UI confirmation policy.</remarks>
    /// <param name="serial">Explicit COM/TTY and line settings.</param>
    /// <param name="startup">Selected loader/stages/EMI and identity policy.</param>
    /// <param name="physicalPartition">Explicit XFlash physical region containing GPT.</param>
    /// <param name="steps">Explicit named partition operations; copied before asynchronous startup.</param>
    /// <param name="cancellationToken">The preparation cancellation token.</param>
    /// <returns>A caller-owned preparation retaining the session and all source handles until disposed.</returns>
    public static Task<MtkFlashPreparation> PrepareAsync(SerialConnectionOptions serial, MtkXFlashBootstrapOptions startup,
        uint physicalPartition, IReadOnlyList<MtkPartitionFlashStep> steps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(startup);
        return PrepareAsync(token => MtkXFlashBootstrap.ConnectSerialAsync(serial, startup, token),
            physicalPartition, steps, cancellationToken);
    }

    internal static async Task<MtkFlashPreparation> PrepareAsync(Func<CancellationToken, Task<MtkSession>> connect,
        uint physicalPartition, IReadOnlyList<MtkPartitionFlashStep> steps, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count is 0 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(steps));
        }
        var selected = steps.ToArray();
        if (selected.Any(step => step is null))
        {
            throw new ArgumentException("A selected flash step is null.", nameof(steps));
        }
        return await PrepareCoreAsync(connect, physicalPartition,
            snapshot => MtkFlashPlan.Prepare(snapshot, selected, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MtkFlashPreparation> PrepareCoreAsync(Func<CancellationToken, Task<MtkSession>> connect,
        uint physicalPartition, Func<MtkGptSnapshot, MtkFlashPlan> prepare, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = await connect(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The connector returned no MTK session.");
        MtkFlashPlan? plan = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await session.ReadXFlashGptAsync(physicalPartition, cancellationToken).ConfigureAwait(false);
            plan = prepare(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            return new MtkFlashPreparation(session, plan, snapshot);
        }
        catch
        {
            plan?.Dispose();
            session.Dispose();
            throw;
        }
    }
}

/// <summary>Owns a previewed one-shot MTK plan and its live session. Abandoning a preview requires disposal.</summary>
public sealed class MtkFlashPreparation : IDisposable
{
    private readonly MtkSession _session;
    private readonly MtkFlashPlan _plan;

    internal MtkFlashPreparation(MtkSession session, MtkFlashPlan plan, MtkGptSnapshot snapshot)
    {
        _session = session;
        _plan = plan;
        Snapshot = snapshot;
    }

    /// <summary>Gets the exact GPT observation used for resolving names; execution revalidates it.</summary>
    public MtkGptSnapshot Snapshot { get; }
    /// <summary>Gets the read-only physical operations to preview, excluding sparse holes.</summary>
    public IReadOnlyList<MtkFlashOperation> Operations => _plan.Operations;
    /// <summary>Gets actual planned write/erase bytes, excluding optional readback and sparse holes.</summary>
    public long TotalBytes => _plan.TotalBytes;
    /// <summary>Gets the communication plan's one-shot lifecycle state.</summary>
    public MtkFlashPlanState State => _plan.State;

    /// <summary>Executes only after the host's explicit confirmation; no retry or rollback is attempted.</summary>
    /// <remarks>Progress callbacks must not synchronously wait on this preparation or its session.</remarks>
    public Task<MtkFlashPlanResult> ExecuteAsync(IProgress<MtkFlashPlanProgress>? progress = null,
        int eraseTimeoutMs = 120000, CancellationToken cancellationToken = default) =>
        _session.ExecuteXFlashPlanAsync(_plan, progress, eraseTimeoutMs, cancellationToken);

    /// <summary>Releases source handles and the session. Disposal while the plan is running is rejected.</summary>
    public void Dispose()
    {
        // Do not close the transport if the plan rejects disposal during an active transfer.
        _plan.Dispose();
        _session.Dispose();
    }
}
