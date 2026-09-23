namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Reports an MTK protocol failure without hiding the target's original status code.</summary>
/// <param name="message">A description of the failed exchange.</param>
/// <param name="status">The optional target status, in host byte order.</param>
public sealed class MtkProtocolException(string message, uint? status = null) : IOException(message)
{
    /// <summary>Gets the original target status, when the failure was a target rejection.</summary>
    public uint? Status { get; } = status;
}