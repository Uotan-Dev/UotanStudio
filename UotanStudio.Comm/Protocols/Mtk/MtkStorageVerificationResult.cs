namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Byte comparison evidence from a completely received storage range.</summary>
/// <param name="ComparedBytes">The number of bytes compared after successful protocol completion.</param>
/// <param name="FirstMismatchOffset">The first differing byte relative to the requested range, or null if identical.</param>
public sealed record MtkStorageVerificationResult(long ComparedBytes, long? FirstMismatchOffset)
{
    /// <summary>Gets whether all bytes in the requested range matched the source.</summary>
    public bool Matches => FirstMismatchOffset is null;
}