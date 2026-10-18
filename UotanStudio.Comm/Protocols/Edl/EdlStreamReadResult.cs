namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>Result of one streamed EDL partition read.
/// <para>单次 EDL 分区流式读取的结果。</para>The hash is measured over exactly the bytes written to the destination stream; the host publishes the destination atomically (for example temp-file plus rename) only after this result returns, because a cancelled or failed read leaves the destination partially written and unswapped.<para>哈希针对实际写入目标流的字节计量；宿主只在本结果返回后才原子发布目标（如临时文件加 重命名），因为被取消或失败的读取会留下部分写入、未替换的目标。</para></summary>
/// <remarks>Creates a read result.<para>创建读取结果。</para></remarks>
/// <param name="BytesWritten">The exact number of bytes appended to the destination.
/// <para>追加到目标的准确字节数。</para></param>
/// <param name="ExpectedBytes">The declared total byte length of the read range.
/// <para>声明的读取范围总字节长度。</para></param>
/// <param name="Sha256Hex">The lowercase SHA-256 digest over the written bytes.
/// <para>写入字节的十六进制小写 SHA-256 摘要。</para></param>
/// <param name="Response">The final device acknowledgement.<para>设备最终确认响应。</para></param>
public sealed record EdlStreamReadResult(long BytesWritten, long ExpectedBytes, string Sha256Hex,
    FirehoseResponse Response)
{
    /// <summary>
    /// Gets whether the device acknowledged the transfer and the destination received the complete
    /// declared length.
    /// <para>获取设备是否确认了传输，且目标是否收到完整的声明长度。</para>
    /// </summary>
    /// <value><see langword="true"/> only when the final response is a normal-mode ACK, the expected
    /// length was fully transferred, and the acknowledgement confirms it.
    /// <para>仅当最终响应为普通模式 ACK、声明长度已完整传输且该确认予以证实时为
    /// <see langword="true"/>。</para></value>
    public bool DeviceAcknowledged => Response is { IsAck: true, IsRawMode: false }
        && BytesWritten == ExpectedBytes;
}