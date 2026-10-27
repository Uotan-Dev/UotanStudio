using UotanStudio.Comm.Protocols.Common;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Reads an XML storage range to a sibling staging file and publishes only after full protocol success.
    /// <para>将 XML 存储范围读取到同目录的临时暂存文件，仅在协议完全成功后发布。</para></summary>
    /// <remarks>The existing destination is preserved on transfer failure. Its directory must already
    /// exist. Full byte progress is not final handshake or publication success.</remarks>
    /// <param name="regionName">The exact discovered XML physical-region token.
    /// <para>已发现的精确 XML 物理区域令牌。</para></param>
    /// <param name="offset">The nonnegative block-aligned byte offset.<para>非负、块对齐的字节偏移。</para></param>
    /// <param name="length">The positive block-aligned byte count.<para>正数、块对齐的字节数。</para></param>
    /// <param name="destinationPath">The file to publish after a complete read.
    /// <para>读取完整后才发布的文件路径。</para></param>
    /// <param name="timeoutMs">Cooperative protocol deadline, from one millisecond through 24 hours.
    /// <para>协议协作式截止时间，范围从 1 毫秒到 24 小时。</para></param>
    /// <param name="progress">Optional cumulative bytes delivered and acknowledged.
    /// <para>可选的累计已交付并已确认字节数。</para></param>
    /// <param name="cancellationToken">Cancellation for the read and publication preparation.
    /// <para>用于读取与发布准备的取消令牌。</para></param>
    /// <returns>A task that completes once the staging file replaced the destination.
    /// <para>在暂存文件替换目标文件后完成的异步任务。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="destinationPath"/> is blank.
    /// <para><paramref name="destinationPath"/> 为空白。</para></exception>
    /// <exception cref="InvalidOperationException">XML storage geometry is unknown or the session stage is wrong.
    /// <para>XML 存储几何信息未知或会话阶段不符。</para></exception>
    /// <exception cref="MtkProtocolException">The XML read exchange failed.
    /// <para>XML 读取交换失败。</para></exception>
    /// <exception cref="IOException">The staging file cannot be created, the staged length is wrong, or publication failed.
    /// <para>无法创建暂存文件、暂存长度不符或发布失败。</para></exception>
    /// <exception cref="TimeoutException">The protocol deadline expired; the session faults.
    /// <para>协议截止时间到期；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled the read or publication preparation.
    /// <para>调用方取消了读取或发布准备。</para></exception>
    public Task ReadXmlStorageToFileAsync(string regionName, long offset, long length, string destinationPath,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        => StorageFilePublisher.PublishAsync(destinationPath, length,
            output => ReadXmlStorageAsync(regionName, offset, length, output, timeoutMs, progress, cancellationToken), cancellationToken);

    /// <summary>Compares a complete XML read with a caller-owned reference stream without stopping at the first mismatch.
    /// <para>将一次完整的 XML 读取与调用方拥有的数据流逐字节比较，遇到首个不匹配后不停止。</para></summary>
    /// <remarks>A result requires final protocol success. This is a separate read, not an atomic
    /// write-and-verify transaction.</remarks>
    /// <param name="regionName">The exact discovered XML physical-region token.
    /// <para>已发现的精确 XML 物理区域令牌。</para></param>
    /// <param name="offset">The nonnegative block-aligned device byte offset.
    /// <para>非负、块对齐的设备字节偏移。</para></param>
    /// <param name="length">The positive block-aligned byte count.<para>正数、块对齐的字节数。</para></param>
    /// <param name="expected">Readable reference positioned at the first expected byte.
    /// <para>定位在首个期望字节处的可读数据流。</para></param>
    /// <param name="timeoutMs">Cooperative protocol deadline, from one millisecond through 24 hours.
    /// <para>协议协作式截止时间，范围从 1 毫秒到 24 小时。</para></param>
    /// <param name="progress">Optional cumulative compared and acknowledged bytes.
    /// <para>可选的累计已比较并已确认字节数。</para></param>
    /// <param name="cancellationToken">Cancellation for the full read and comparison.
    /// <para>用于完整读取与比较的取消令牌。</para></param>
    /// <returns>Comparison evidence including the first relative mismatch offset.
    /// <para>比较证据，包含首个相对不匹配偏移。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="expected"/> is null.
    /// <para><paramref name="expected"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not positive.
    /// <para><paramref name="length"/> 不是正数。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="expected"/> is unreadable or shorter than the selected range.
    /// <para><paramref name="expected"/> 不可读或短于选定范围。</para></exception>
    /// <exception cref="InvalidOperationException">XML storage geometry is unknown or the session stage is wrong.
    /// <para>XML 存储几何信息未知或会话阶段不符。</para></exception>
    /// <exception cref="MtkProtocolException">The XML read exchange failed, or the stream ended early.
    /// <para>XML 读取交换失败，或数据流提前结束。</para></exception>
    /// <exception cref="TimeoutException">The protocol deadline expired; the session faults.
    /// <para>协议截止时间到期；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled the read or comparison.
    /// <para>调用方取消了读取或比较。</para></exception>
    public async Task<MtkStorageVerificationResult> VerifyXmlStorageAsync(string regionName, long offset, long length,
        Stream expected, int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (!expected.CanRead || (expected.CanSeek && expected.Length - expected.Position < length))
        {
            throw new ArgumentException("The XML reference stream is unreadable or shorter than the selected range.", nameof(expected));
        }
        using var comparison = new StorageComparisonStream(expected);
        await ReadXmlStorageAsync(regionName, offset, length, comparison, timeoutMs, progress, cancellationToken).ConfigureAwait(false);
        return new MtkStorageVerificationResult(comparison.ComparedBytes, comparison.FirstMismatchOffset);
    }
}