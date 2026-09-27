using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Programs complete Legacy NAND blocks using the explicit SDK B3 raw-page profile.
    /// <para>通过显式 SDK B3 原始页配置编程完整的 Legacy NAND 块。</para></summary>
    /// <remarks>No implicit erase, padding, bad-block skipping, retransmission or readback is performed.
    /// A failed or cancelled exchange faults the session; programmed bytes cannot be rolled back.
    /// The source remains open and bytes beyond the selected span are not consumed.
    /// The SDK MT2694 transport variant is not supported by this profile.
    /// <para>不隐含执行擦除、填充、跳过坏块、重传或回读。交换失败或取消将使会话故障，已编程字节无法回滚。
    /// 源流保持打开，选定跨度以外的字节不会被消费。此配置不支持 SDK MT2694 传输变体。</para></remarks>
    /// <param name="offset">The main-area byte offset aligned to the erase-block size.
    /// <para>按擦除块大小对齐的主区字节偏移。</para></param>
    /// <param name="mainLength">The positive main-area span of complete blocks, excluding spare bytes.
    /// <para>正数完整块主区跨度，不含备用字节。</para></param>
    /// <param name="source">The readable stream positioned at the first selected page record.
    /// <para>定位到首个选定页记录的可读流。</para></param>
    /// <param name="options">The explicit wire, address and page-layout profile.
    /// <para>显式线协议、寻址和页布局配置。</para></param>
    /// <param name="timeoutMs">The cooperative whole-operation deadline from 1 ms through 24 hours.
    /// <para>1 毫秒至 24 小时的完整操作协作式截止时间。</para></param>
    /// <param name="progress">Cumulative source bytes acknowledged by the agent, including selected spare bytes.
    /// <para>累计已被代理确认的源字节数，包含所选备用字节。</para></param>
    /// <param name="cancellationToken">Cancellation for source reads and the complete device exchange.
    /// <para>源读取和完整设备交换的取消令牌。</para></param>
    /// <returns>A task completing after the final page receives its CONT acknowledgement.
    /// <para>最后一页收到 CONT 确认后完成的任务。</para></returns>
    public async Task WriteLegacyNandStorageAsync(long offset, long mainLength, Stream source,
        MtkLegacyNandWriteOptions options, int timeoutMs = 120000, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        if (!source.CanRead)
        {
            throw new ArgumentException("The NAND source must be readable.", nameof(source));
        }
        options.Validate();
        ValidateXmlSetupTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.LegacyStorageReady);
            var geometry = LegacyStorage!.Nand ?? throw new NotSupportedException("The Legacy session is not initialized for NAND.");
            if (DeviceInfo?.HardwareCode == 0x2694)
            {
                throw new NotSupportedException("The SDK MT2694 NAND transport requires a different profile.");
            }
            var length = options.GetTransferLength(geometry, mainLength);
            var header = options.CreateHeader(geometry, offset, mainLength);
            if (source.CanSeek && (source.Position < 0 || source.Position > source.Length || length > source.Length - source.Position))
            {
                throw new EndOfStreamException("The NAND source is shorter than the requested page records.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var buffer = new byte[options.GetRecordSize(geometry)];
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            try
            {
                await WriteAsync(header, _timeoutMs, token).ConfigureAwait(false);
                await RequireLegacyNandWriteAckOwnedAsync(0x5A, "header", token).ConfigureAwait(false);
                var checksum = new byte[2];
                for (long transferred = 0; transferred < length;)
                {
                    await source.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
                    BinaryPrimitives.WriteUInt16BigEndian(checksum, LegacySum(buffer));
                    await WriteAsync([0x5A], _timeoutMs, token).ConfigureAwait(false);
                    await WriteAsync(buffer, _timeoutMs, token).ConfigureAwait(false);
                    await WriteAsync(checksum, _timeoutMs, token).ConfigureAwait(false);
                    await RequireLegacyNandWriteAckOwnedAsync(0x69, "page", token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    ThrowIfDisposed();
                    transferred += buffer.Length;
                    progress?.Report(transferred);
                }
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultLegacy();
                throw new TimeoutException("The Legacy NAND program deadline expired; the target result is unknown.", error);
            }
            catch
            {
                FaultLegacy();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task RequireLegacyNandWriteAckOwnedAsync(byte expected, string stage, CancellationToken token)
    {
        var response = (await ReadAsync(1, _timeoutMs, token).ConfigureAwait(false))[0];
        if (response == expected)
        {
            return;
        }
        var status = response == 0xA5
            ? BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(4, _timeoutMs, token).ConfigureAwait(false))
            : (uint)response;
        throw new MtkProtocolException(
            $"Legacy NAND program {stage} returned 0x{response:X2}; no retry or skipped block was accepted.", status);
    }
}