namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>The current phase of one executable range.</summary>
public enum MtkFlashPhase
{
    /// <summary>Source bytes are being written.</summary>
    Writing,
    /// <summary>The target is performing an erase; byte progress is unknown until completion.</summary>
    Erasing,
    /// <summary>Written bytes are being compared with readback.</summary>
    Verifying,
    /// <summary>The operation and any requested verification completed successfully.</summary>
    OperationCompleted
}

/// <summary>Phase-local byte progress; full transferred bytes alone do not imply final command success.</summary>
/// <param name="OperationIndex">The zero-based executable range index.</param>
/// <param name="OperationCount">The number of executable ranges.</param>
/// <param name="StepIndex">The originating input step index.</param>
/// <param name="Phase">The current phase.</param>
/// <param name="ProcessedBytes">The phase's processed bytes.</param>
/// <param name="TotalBytes">The phase's total bytes.</param>
public sealed record MtkFlashPlanProgress(int OperationIndex, int OperationCount, int StepIndex,
    MtkFlashPhase Phase, long ProcessedBytes, long TotalBytes);

/// <summary>Returned only after the complete plan and its requested readbacks succeed.</summary>
/// <param name="CompletedOperations">The number of completed physical operations.</param>
/// <param name="WrittenBytes">The total written bytes, excluding sparse holes.</param>
/// <param name="ErasedBytes">The total ranges acknowledged as erased.</param>
/// <param name="VerifiedBytes">The total bytes matched by readback.</param>
public sealed record MtkFlashPlanResult(int CompletedOperations, long WrittenBytes, long ErasedBytes, long VerifiedBytes);