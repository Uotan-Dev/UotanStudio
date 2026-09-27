namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Reads an ordinary non-IoT Legacy NOR byte range with per-packet checksum validation.
    /// <para>按包校验校验和后读取非 IoT Legacy NOR 普通字节范围。</para></summary>
    /// <remarks>No eMMC partition switch or OTP access occurs. Failed output may be partial.
    /// <para>不执行 eMMC 分区切换或 OTP 访问。失败时输出可能不完整。</para></remarks>
    /// <param name="offset">The nonnegative byte offset.
    /// <para>非负字节偏移。</para></param>
    /// <param name="length">The positive byte count within the discovered NOR capacity.
    /// <para>已发现 NOR 容量内的正数字节数。</para></param>
    /// <param name="destination">The caller-owned writable stream.
    /// <para>调用方持有的可写流。</para></param>
    /// <param name="timeoutMs">The whole-transfer deadline from 1 ms through 24 hours.
    /// <para>1 毫秒至 24 小时的完整传输截止时间。</para></param>
    /// <param name="progress">Cumulative verified and acknowledged bytes.
    /// <para>累计已校验并确认的字节数。</para></param>
    /// <param name="cancellationToken">Cancellation for the operation.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task completing after the final packet acknowledgement.
    /// <para>最终数据包确认后完成的任务。</para></returns>
    public Task ReadLegacyNorStorageAsync(long offset, long length, Stream destination,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        TransferLegacyStorageAsync(false, 8, offset, length, destination, timeoutMs, progress, cancellationToken, true);

    /// <summary>Writes an explicitly selected non-IoT Legacy NOR range using the generic 0x62 command.
    /// <para>使用通用 0x62 命令写入显式选择的非 IoT Legacy NOR 范围。</para></summary>
    /// <remarks>This profile accepts only 512-byte aligned writes. No padding, erase, retry or readback is implied.
    /// <para>本配置仅接受按 512 字节对齐的写入。不隐含填充、擦除、重试或回读。</para></remarks>
    /// <param name="offset">The nonnegative byte offset aligned to 512.
    /// <para>按 512 对齐的非负字节偏移。</para></param>
    /// <param name="length">The positive byte count aligned to 512.
    /// <para>按 512 对齐的正数字节数。</para></param>
    /// <param name="source">The caller-owned readable stream at the first selected byte.
    /// <para>定位于首个选定字节的调用方可读流。</para></param>
    /// <param name="timeoutMs">The whole-transfer deadline from 1 ms through 24 hours.
    /// <para>1 毫秒至 24 小时的完整传输截止时间。</para></param>
    /// <param name="progress">Cumulative bytes acknowledged by the agent.
    /// <para>累计已被代理确认的字节数。</para></param>
    /// <param name="cancellationToken">Cancellation for the operation.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task completing after the final CONT acknowledgement.
    /// <para>最终 CONT 确认后完成的任务。</para></returns>
    public Task WriteLegacyNorStorageAsync(long offset, long length, Stream source,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        TransferLegacyStorageAsync(true, 8, offset, length, source, timeoutMs, progress, cancellationToken, true);
}