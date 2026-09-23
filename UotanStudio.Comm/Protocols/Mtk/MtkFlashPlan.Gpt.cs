namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>An explicit operation within an exactly named GPT partition; no slot inference or implicit erase.</summary>
/// <param name="Kind">Raw, sparse or erase operation.</param>
/// <param name="PartitionName">Exact case-sensitive GPT name; ambiguous names are rejected.</param>
/// <param name="Offset">Block-aligned byte offset relative to the named partition.</param>
/// <param name="Length">Exact raw/erase length or sparse target capacity, bounded by the named partition.</param>
/// <param name="SourcePath">Raw/sparse source path; null for erase.</param>
/// <param name="SourceOffset">Raw source byte offset; zero for sparse/erase.</param>
/// <param name="Verify">Whether written extents require readback; invalid for erase.</param>
public sealed record MtkPartitionFlashStep(MtkFlashStepKind Kind, string PartitionName, long Offset, long Length,
    string? SourcePath = null, long SourceOffset = 0, bool Verify = false);

public sealed partial class MtkFlashPlan
{
    /// <summary>Gets the GPT observation that must be revalidated under the execution lease, or null for explicit physical plans.</summary>
    public MtkGptSnapshot? GptSnapshot { get; private set; }

    /// <summary>Resolves named ranges and reuses whole-plan source/sparse preflight without device I/O.</summary>
    /// <param name="snapshot">A validated observation from the intended session and physical region.</param>
    /// <param name="steps">One to 256 explicitly ordered partition-relative steps.</param>
    /// <param name="cancellationToken">The preflight cancellation token.</param>
    /// <returns>A one-shot plan that requires fresh matching GPT metadata before any destructive operation.</returns>
    public static MtkFlashPlan Prepare(MtkGptSnapshot snapshot, IReadOnlyList<MtkPartitionFlashStep> steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count is 0 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), "A flash plan requires between one and 256 steps.");
        }
        var selected = steps.ToArray();
        var physical = new MtkFlashStep[selected.Length];
        for (var index = 0; index < selected.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = selected[index] ?? throw new ArgumentException("A flash step is null.", nameof(steps));
            var partition = snapshot.Table.FindPartition(step.PartitionName);
            if (step.Offset < 0 || step.Length <= 0 || step.Offset > partition.ByteLength
                || step.Length > partition.ByteLength - step.Offset)
            {
                throw new ArgumentOutOfRangeException(nameof(steps), "A requested range exceeds its named GPT partition.");
            }
            physical[index] = new MtkFlashStep(step.Kind, snapshot.PartitionId,
                checked(partition.ByteOffset + step.Offset), step.Length, step.SourcePath, step.SourceOffset, step.Verify);
        }
        var plan = Prepare(snapshot.Storage, physical, cancellationToken);
        plan.GptSnapshot = snapshot;
        return plan;
    }
}