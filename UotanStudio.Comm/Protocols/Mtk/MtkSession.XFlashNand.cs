namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Reads an explicitly selected NAND page stream with no host-side ECC, randomization or bad-block remapping.
    /// <para>读取显式选定的 NAND 页流，不在主机执行 ECC、随机化或坏块重映射。</para></summary>
    /// <remarks>The profile must match the selected DA's eight-word extension and length convention.
    /// Progress counts stream bytes, not addressed main-area bytes. Partial output on failure is not rolled back.
    /// <see cref="XFlashStorageTransferTimeoutMs"/> bounds the cooperative exchange.
    /// <para>配置必须匹配所选 DA 的八字扩展与长度约定。进度计量流字节，而不是寻址的主区字节。
    /// <see cref="XFlashStorageTransferTimeoutMs"/> 限制协作式交换的时间。
    /// 失败时不会回滚部分输出。</para></remarks>
    /// <param name="offset">The page-aligned main-area byte address. <para>按页对齐的主区字节地址。</para></param>
    /// <param name="mainLength">The positive page-aligned main-area span, excluding spare bytes.
    /// <para>正数且按页对齐的主区跨度，不含备用字节。</para></param>
    /// <param name="options">The explicit address, data-mode and length-unit profile.
    /// <para>显式的地址、数据模式和长度单位配置。</para></param>
    /// <param name="destination">A writable caller-owned stream that remains open.
    /// <para>保持打开的调用方所有可写流。</para></param>
    /// <param name="progress">Optional cumulative stream bytes written and acknowledged.
    /// <para>可选的累计已写入并确认流字节数。</para></param>
    /// <param name="cancellationToken">Cancellation for the gate, stream and device exchange.
    /// <para>操作门、流和设备交换的取消令牌。</para></param>
    /// <returns>A task completed only after the exact selected stream length and final status are received.
    /// <para>仅在收到选定流的精确长度和最终状态后完成的任务。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The profile, selected range or computed stream length is invalid.
    /// <para>配置、选定范围或计算的流长度无效。</para></exception>
    /// <exception cref="InvalidOperationException">No NAND-ready session is available.
    /// <para>没有 NAND 就绪会话。</para></exception>
    /// <exception cref="MtkProtocolException">The data or completion violates the selected exchange; the session faults.
    /// <para>数据或完成状态违反选定交换；会话失效。</para></exception>
    /// <exception cref="TimeoutException">The cooperative exchange deadline expired.
    /// <para>协作式交换截止时间已到。</para></exception>
    public Task ReadXFlashNandStorageAsync(long offset, long mainLength, MtkXFlashNandTransferOptions options,
        Stream destination, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        TransferXFlashNandAsync(false, offset, mainLength, options, destination, progress, cancellationToken);

    /// <summary>Writes an exact NAND page stream without padding, conversion or automatic erase.
    /// <para>精确写入 NAND 页流，不执行填充、转换或自动擦除。</para></summary>
    /// <remarks>Source records follow the selected page/spare mode. Each packet contains whole records
    /// within the negotiated bound. The agent owns ECC, randomization and bad-block handling.
    /// No command is replayed after a failure or an uncertain completion.
    /// <para>源记录遵循选定的页与备用区模式。每包在协商上限内包含完整记录。
    /// ECC、随机化和坏块处理由代理负责。失败或完成状态不确定后不重放命令。</para></remarks>
    /// <param name="offset">The page-aligned main-area byte address. <para>按页对齐的主区字节地址。</para></param>
    /// <param name="mainLength">The positive page-aligned main-area span, excluding spare bytes.
    /// <para>正数且按页对齐的主区跨度，不含备用字节。</para></param>
    /// <param name="options">The explicit address, data-mode and length-unit profile.
    /// <para>显式的地址、数据模式和长度单位配置。</para></param>
    /// <param name="source">A readable caller-owned stream at its first selected record; remains open.
    /// <para>定位在首个选定记录处的调用方所有可读流；保持打开。</para></param>
    /// <param name="progress">Optional cumulative stream bytes acknowledged by the agent.
    /// <para>可选的累计已被代理确认的流字节数。</para></param>
    /// <param name="cancellationToken">Cancellation for the gate, stream and device exchange.
    /// <para>操作门、流和设备交换的取消令牌。</para></param>
    /// <returns>A task completed only after the agent accepts all packets and the final completion.
    /// <para>仅在代理接受所有包和最终完成状态后结束的任务。</para></returns>
    /// <exception cref="ArgumentException">The source is unreadable or its available length is insufficient.
    /// <para>源不可读或其可用长度不足。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The profile, range or record framing is invalid.
    /// <para>配置、范围或记录分帧无效。</para></exception>
    /// <exception cref="EndOfStreamException">The source ended before the requested stream was transferred.
    /// <para>源在完成请求的流传输前结束。</para></exception>
    /// <exception cref="InvalidOperationException">No NAND-ready session is available.
    /// <para>没有 NAND 就绪会话。</para></exception>
    /// <exception cref="MtkProtocolException">The agent rejected a packet or completion; the session faults.
    /// <para>代理拒绝数据包或完成状态；会话失效。</para></exception>
    /// <exception cref="TimeoutException">The cooperative exchange deadline expired.
    /// <para>协作式交换截止时间已到。</para></exception>
    public Task WriteXFlashNandStorageAsync(long offset, long mainLength, MtkXFlashNandTransferOptions options,
        Stream source, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        TransferXFlashNandAsync(true, offset, mainLength, options, source, progress, cancellationToken);

    /// <summary>Requests an explicit NAND erase action over complete erase blocks and waits for final confirmation.
    /// <para>在完整擦除块范围内请求显式 NAND 擦除动作，并等待最终确认。</para></summary>
    /// <remarks>Force erase and bad-block marking occur only when explicitly selected.
    /// Cancellation or timeout cannot undo a device-accepted action; such sessions are faulted.
    /// <para>仅在显式选择时才执行强制擦除或坏块标记。
    /// 取消或超时无法撤销设备已接受的动作；此类会话会失效。</para></remarks>
    /// <param name="offset">The erase-block-aligned main-area address. <para>按擦除块对齐的主区地址。</para></param>
    /// <param name="mainLength">The positive erase-block-aligned main-area span.
    /// <para>正数且按擦除块对齐的主区跨度。</para></param>
    /// <param name="policy">The caller-selected NAND address and extension policy.
    /// <para>调用方选定的 NAND 地址与扩展策略。</para></param>
    /// <param name="mode">The explicit normal, forced or bad-block-marking action.
    /// <para>显式的普通、强制或坏块标记动作。</para></param>
    /// <param name="timeoutMs">The whole exchange deadline after acquiring the gate, from 1 ms to 24 hours.
    /// <para>取得操作门后整次交换的截止时间，范围为 1 毫秒至 24 小时。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and all device exchanges.
    /// <para>等待与所有设备交换的取消令牌。</para></param>
    /// <returns>A task completed after the explicit format-complete status.
    /// <para>收到显式格式化完成状态后结束的任务。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The profile, action, block range or timeout is invalid.
    /// <para>配置、动作、块范围或超时无效。</para></exception>
    /// <exception cref="InvalidOperationException">No NAND-ready session is available.
    /// <para>没有 NAND 就绪会话。</para></exception>
    /// <exception cref="MtkProtocolException">The device did not confirm the selected action.
    /// <para>设备未确认选定动作。</para></exception>
    /// <exception cref="TimeoutException">The completion deadline expired; the device action may still be running.
    /// <para>完成截止时间已到；设备动作可能仍在执行。</para></exception>
    public async Task EraseXFlashNandStorageAsync(long offset, long mainLength, MtkXFlashNandPolicy policy,
        MtkXFlashNandEraseMode mode, int timeoutMs = 120000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeoutMs, 86400000);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var storage = RequireXFlashNandStorage();
            var parameters = policy.CreateParameters(storage.Nand!, offset, mainLength, mainLength, (uint)mode, true);
            await EraseXFlashRangeAsync(parameters, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task TransferXFlashNandAsync(bool write, long offset, long mainLength, MtkXFlashNandTransferOptions options,
        Stream stream, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stream);
        options.Validate();
        if (write ? !stream.CanRead : !stream.CanWrite)
        {
            throw new ArgumentException("The NAND stream does not support the selected direction.", nameof(stream));
        }
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var storage = RequireXFlashNandStorage();
            var geometry = storage.Nand!;
            var length = options.GetTransferLength(geometry, mainLength);
            var commandLength = options.LengthUnit == MtkXFlashNandLengthUnit.MainAreaBytes ? mainLength : length;
            var parameters = options.Policy.CreateParameters(geometry, offset, mainLength, commandLength, (uint)options.Mode, false);
            if (write && stream.CanSeek && stream.Length - stream.Position < length)
            {
                throw new ArgumentException("The NAND source is shorter than the selected page stream.", nameof(stream));
            }
            var recordSize = options.GetRecordSize(geometry);
            var packetLength = storage.WritePacketLength / recordSize * recordSize;
            if ((write && packetLength == 0) || (!write && length < 5))
            {
                throw new ArgumentOutOfRangeException(nameof(options), "The selected NAND page stream cannot fit the negotiated framing.");
            }
            await RunXFlashStorageTransferAsync(token => write
                ? WriteXFlashRangeAsync(parameters, packetLength, length, stream, progress, token)
                : ReadXFlashRangeAsync(parameters, storage.ReadPacketLength, length, stream, progress, token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private MtkXFlashStorageInfo RequireXFlashNandStorage()
    {
        RequireState(MtkSessionState.XFlashStorageReady);
        var storage = XFlashStorage;
        if (storage is not { Kind: MtkXFlashStorageKind.Nand, Nand: not null })
        {
            throw new InvalidOperationException("The session has no discovered NAND geometry.");
        }
        return storage;
    }
}