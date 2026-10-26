using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Reads an explicit Legacy NAND raw-page range using the SDK 64-bit readback protocol.
    /// <para>使用 SDK 64 位读回协议读取显式的 Legacy NAND 原始页范围。</para></summary>
    /// <remarks>The zero container-length profile has no per-packet device status prefix. Page geometry, record size, total length and each checksum must match before output is acknowledged. No 32-bit fallback, retransmission, bad-block remapping or spare-area conversion is performed. Output may be partial on failure; file publishers must use temporary files.
    /// <para>零容器长度配置没有逐包设备状态前缀。确认输出前必须匹配页几何、记录大小、总长度与各包校验和。 不执行 32 位协议回退、重传、坏块重映射或备用区转换。失败时输出可能不完整；文件发布方必须使用临时文件。</para></remarks>
    /// <param name="offset">The page-aligned main-area byte offset.<para>按页对齐的主区字节偏移。</para></param>
    /// <param name="mainLength">The positive page-aligned main-area span, excluding spare bytes.
    /// <para>正数且按页对齐的主区跨度，不含备用字节。</para></param>
    /// <param name="destination">The caller-owned writable stream, which remains open.
    /// <para>调用方持有且保持打开的可写流。</para></param>
    /// <param name="options">The explicit wire, address, length and page-layout profile.
    /// <para>显式的线协议、寻址、长度和页布局配置。</para></param>
    /// <param name="timeoutMs">The cooperative whole-transfer deadline from 1 ms through 24 hours.
    /// <para>1 毫秒至 24 小时的完整传输协作式截止时间。</para></param>
    /// <param name="progress">Cumulative stream bytes published and acknowledged, including selected spare bytes.
    /// <para>累计已发布并确认的流字节数，包含所选备用字节。</para></param>
    /// <param name="cancellationToken">Cancellation for the complete exchange and destination writes.
    /// <para>完整交换过程与目标写入的取消令牌。</para></param>
    /// <returns>A task completing after the exact reported total is verified and the last packet acknowledged.
    /// <para>精确上报总量经校验且最后一个包确认后完成的任务。</para></returns>
    public async Task ReadLegacyNandStorageAsync(long offset, long mainLength, Stream destination,
        MtkLegacyNandReadOptions options, int timeoutMs = 120000, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The NAND destination must be writable.", nameof(destination));
        }
        options.Validate();
        ValidateXmlSetupTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.LegacyStorageReady);
            var geometry = LegacyStorage!.Nand ?? throw new NotSupportedException("The Legacy session is not initialized for NAND.");
            var length = options.GetTransferLength(geometry, mainLength);
            var recordSize = options.GetRecordSize(geometry);
            var header = options.CreateHeader(geometry, offset, mainLength, length);
            var packetLength = recordSize * options.PagesPerPacket;
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            try
            {
                await WriteAsync(header, _timeoutMs, token).ConfigureAwait(false);
                await RequireLegacyByteAsync(0x5A, "NAND readback header", token).ConfigureAwait(false);
                var parameters = await ReadAsync(12, _timeoutMs, token).ConfigureAwait(false);
                if (BinaryPrimitives.ReadUInt32BigEndian(parameters) != geometry.PageSize
                    || BinaryPrimitives.ReadUInt32BigEndian(parameters.AsSpan(4)) != geometry.SpareSize
                    || BinaryPrimitives.ReadUInt32BigEndian(parameters.AsSpan(8)) != recordSize)
                {
                    throw new MtkProtocolException("The Legacy NAND readback geometry or page record size does not match the selected layout.");
                }
                var pages = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(pages, (uint)options.PagesPerPacket);
                await WriteAsync(pages, _timeoutMs, token).ConfigureAwait(false);
                var total = BinaryPrimitives.ReadUInt64BigEndian(await ReadAsync(8, _timeoutMs, token).ConfigureAwait(false));
                if (total != (ulong)length)
                {
                    throw new MtkProtocolException("The Legacy NAND total readback length does not match the requested page span.");
                }
                var packet = new byte[packetLength];
                var checksumBytes = new byte[2];
                for (long transferred = 0; transferred < length;)
                {
                    var count = (int)Math.Min(length - transferred, packetLength);
                    await ReadIntoAsync(packet, count, _timeoutMs, token).ConfigureAwait(false);
                    await ReadIntoAsync(checksumBytes, checksumBytes.Length, _timeoutMs, token).ConfigureAwait(false);
                    var checksum = BinaryPrimitives.ReadUInt16BigEndian(checksumBytes);
                    if (checksum != LegacySum(packet.AsSpan(0, count)))
                    {
                        throw new MtkProtocolException("The Legacy NAND packet checksum does not match.");
                    }
                    await destination.WriteAsync(packet.AsMemory(0, count), token).ConfigureAwait(false);
                    await WriteAsync(Acknowledge, Acknowledge.Length, _timeoutMs, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    ThrowIfDisposed();
                    transferred += count;
                    progress?.Report(transferred);
                }
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultLegacy();
                throw new TimeoutException("The Legacy NAND readback deadline expired; reconnect without replaying the exchange.", error);
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
}