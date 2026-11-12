namespace UotanStudio.Comm.Transports.Abstractions;

/// <summary>
/// Optional read-stream push-back for transports that buffer replayable evidence: bytes the caller read past
/// its own framing boundary are returned to the head of the read stream, so the next read replays them first
/// instead of the framing losing them.
/// <para>为缓冲可回放证据的传输提供的可选读流回推：调用方读过自身组帧边界之外的字节会归还到读流头部，使
/// 下一次读取先回放这些字节，而不是让组帧丢失它们。</para>
/// </summary>
public interface ICommTransportPushBack
{
    /// <summary>
    /// Returns bytes to the head of the read stream. The transport must replay them before any subsequently
    /// received byte, and must keep them subject to its own evidence-freshness rules.
    /// <para>把字节归还到读流头部。传输必须在任何后续到达的字节之前回放它们，并让其继续服从自身的证据
    /// 新鲜度规则。</para>
    /// </summary>
    /// <param name="bytes">The bytes to replay first. <para>要先回放的字节。</para></param>
    void PushBack(ReadOnlySpan<byte> bytes);
}