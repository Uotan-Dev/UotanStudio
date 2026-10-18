using System.Buffers;
using System.Globalization;

namespace UotanStudio.Comm.Protocols.Fastboot;

public sealed partial class FastbootSession
{
    /// <summary>
    /// Negotiates and caches the maximum download size supported by the device.
    /// <para>协商并缓存设备支持的最大下载大小。</para>
    /// </summary>
    /// <remarks>
    /// <para>Queries <c>max-download-size</c> via getvar and clamps against the wire-level ceiling
    /// (<see cref="FastbootConstants.SparseMaxDownloadSize"/>). Only a usable, device-confirmed number is
    /// cached: a FAIL reply, a timeout, a transport error or an unparsable/zero answer yields
    /// <see cref="FastbootConstants.FallbackMaxDownloadSize"/> without caching it, and any such answer is
    /// evicted from the session getvar cache, so the next call re-negotiates instead of pinning a device
    /// whose real limit is far smaller to the fallback for the rest of the session. A value already
    /// collected by <see cref="GetAllVariables"/> is reused from that cache.</para>
    /// <para>通过 getvar 查询 <c>max-download-size</c>，并按线协议上限
    /// （<see cref="FastbootConstants.SparseMaxDownloadSize"/>）钳制。只有设备确认且可用的数值才会缓存：
    /// FAIL 应答、超时、传输错误或无法解析/为零的应答会返回
    /// <see cref="FastbootConstants.FallbackMaxDownloadSize"/> 但不写入缓存，且此类应答会从会话 getvar
    /// 缓存中逐出，因此下一次调用会重新协商，而不会把真实上限远小于兜底值的设备在整个会话内钉死在兜底值上。
    /// <see cref="GetAllVariables"/> 已采集到的值会从该缓存中复用。</para>
    /// </remarks>
    /// <returns>The negotiated maximum in bytes, clamped to the wire ceiling and <see cref="uint.MaxValue"/>.
    /// A device-confirmed usable answer is cached; anything else returns the fallback and is re-queried next time.
    /// <para>字节计价的协商上限，按线协议上限与 <see cref="uint.MaxValue"/> 钳制。设备确认的可用应答会被缓存；
    /// 其余情况返回兜底值并在下次调用时重新查询。</para></returns>
    public long GetMaxDownloadSize()
    {
        if (_maxDownloadSize is long cached)
        {
            return cached;
        }

        var fallback = Math.Min(FastbootConstants.SparseMaxDownloadSize, FastbootConstants.FallbackMaxDownloadSize);
        string sizeStr;
        try
        {
            var query = GetVarWithResult(FastbootConstants.VarMaxDownloadSize);
            if (!query.Success)
            {
                return fallback;
            }

            sizeStr = query.Value;
        }
        catch
        {
            return fallback;
        }

        var trimmed = sizeStr.Trim();
        var parsedSize = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : -1
            : long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : -1;

        if (parsedSize <= 0)
        {
            _varCache.Remove(FastbootConstants.VarMaxDownloadSize);
            return fallback;
        }

        var negotiated = Math.Min(Math.Min(parsedSize, FastbootConstants.SparseMaxDownloadSize), uint.MaxValue);
        _maxDownloadSize = negotiated;
        return negotiated;
    }

    /// <summary>
    /// Drops the negotiated max-download-size and its cached getvar answer so the next
    /// <see cref="GetMaxDownloadSize"/> call really does re-query the device. Used after a transport
    /// reset, which re-enumerates the device: clearing only the negotiated field would let the session
    /// getvar cache hand back the pre-reset answer.
    /// <para>丢弃已协商的 max-download-size 及其缓存的 getvar 应答，使下一次
    /// <see cref="GetMaxDownloadSize"/> 调用确实重新查询设备。在传输重置（设备会重新枚举）之后使用：
    /// 只清协商字段会让会话 getvar 缓存把重置前的应答原样交回来。</para>
    /// </summary>
    private void InvalidateMaxDownloadSize()
    {
        _maxDownloadSize = null;
        _varCache.Remove(FastbootConstants.VarMaxDownloadSize);
    }

    /// <summary>Downloads data to the device: sends download:&lt;hex-size&gt;, streams data, and awaits OKAY/FAIL. Payloads exceeding the negotiated max download size are rejected.
    /// <para>下载数据到设备：发送 download:&lt;hex-size&gt;，流式传输数据，等待 OKAY/FAIL。 超过协商的最大下载大小的负载会被拒绝。</para></summary>
    /// <param name="stream">Payload source, read in transfer-chunk sized increments up to <paramref name="length"/> bytes; the caller keeps ownership.
    /// <para>负载来源，按传输分块大小递增读取至多 <paramref name="length"/> 字节；所有权由调用方保留。</para></param>
    /// <param name="length">Exact payload size in bytes; must be between 1 and <see cref="uint.MaxValue"/> and no more than the negotiated max-download-size.
    /// <para>精确负载字节数；必须在 1 到 <see cref="uint.MaxValue"/> 之间，且不超过协商的 max-download-size。</para></param>
    /// <param name="progress">Receives the cumulative sent byte count after each chunk; may be <see langword="null"/>.
    /// <para>每个分块发送后接收累计字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Checked before every chunk read; cancelling aborts the download mid-payload.
    /// <para>每次读取分块前检查；取消会在传输中途终止下载。</para></param>
    /// <returns>The terminal device response after the payload. A DATA reply announcing fewer bytes than requested is zero-filled up to the announced size, drained, and returned with its text rewritten; a DATA reply announcing more, or a terminal reply that never arrives, resets the transport before returning, because the bootloader's download state machine can no longer be satisfied from this side.
    /// <para>负载传输后的设备终态响应。DATA 应答声明的字节数少于请求时，先按声明尺寸补零并排空，再返回 改写文本后的响应；DATA 应答声明得更多，或终态应答始终未到达时，先重置传输再返回，因为 bootloader 的下载状态机已无法从本端满足。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not in 1..<see cref="uint.MaxValue"/>.
    /// <para><paramref name="length"/> 不在 1 到 <see cref="uint.MaxValue"/> 范围内。</para></exception>
    /// <exception cref="FastbootException">The payload exceeds the negotiated max-download-size, or the transport short-wrote a chunk.
    /// <para>负载超过协商上限，或传输层短写。</para></exception>
    /// <exception cref="EndOfStreamException">The source stream ended before <paramref name="length"/> bytes were read.
    /// <para>源流在读取满 <paramref name="length"/> 字节前提前结束。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已释放。</para></exception>
    public FastbootResponse DownloadData(Stream stream, long length, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (length is <= 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var maxDownloadSize = GetMaxDownloadSize();
        if (length > maxDownloadSize)
        {
            throw new FastbootException(FastbootResponseState.Fail,
                $"download payload {length} exceeds max-download-size {maxDownloadSize}");
        }

        SendCommand(FastbootConstants.CommandDownload + length.ToString("x8"));

        var rawResponse = ReceiveResponse();
        if (rawResponse.State != FastbootResponseState.Data)
        {
            return rawResponse;
        }

        if (rawResponse.DataSize > length)
        {
            ResetTransportAfterAbandon();
            rawResponse.Text = $"download size mismatch: requested {length}, device accepted {rawResponse.DataSize}";
            return rawResponse;
        }

        _pendingDownloadBytes = rawResponse.DataSize;
        if (rawResponse.DataSize != length)
        {
            AbortDownload(sent: 0);
            rawResponse.Text = $"download size mismatch: requested {length}, device accepted {rawResponse.DataSize}";
            return rawResponse;
        }

        var chunkBuffer = ArrayPool<byte>.Shared.Rent(FastbootConstants.TransferChunkSize);
        long bytesWritten = 0;
        try
        {
            while (bytesWritten < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(FastbootConstants.TransferChunkSize, length - bytesWritten);
                var read = stream.Read(chunkBuffer, 0, toRead);
                if (read <= 0)
                {
                    throw new EndOfStreamException($"Stream ended early at {bytesWritten}/{length}");
                }

                var written = Transport.Write(chunkBuffer, 0, read, _timeoutMs);
                if (written != read)
                {
                    throw new FastbootException(FastbootResponseState.Fail, $"Short write: {written}/{read}");
                }

                bytesWritten += written;
                progress?.Report(bytesWritten);
            }
        }
        catch
        {
            AbortDownload(bytesWritten);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuffer);
        }

        var terminal = ReceiveResponse();
        _pendingDownloadBytes = 0;
        if (terminal.State is FastbootResponseState.Timeout)
        {
            ResetTransportAfterAbandon();
        }

        return terminal;
    }

    /// <summary>
    /// Completes an abandoned download the way AOSP <c>AbortDownload</c> does: zero-fills the bytes
    /// the device still expects, then consumes the trailing terminal frame so the bootloader's
    /// download state machine returns to idle and the next command does not read a stale reply. A
    /// bootloader that never acknowledges (or a transport that cannot write the padding) leaves the
    /// session unrecoverable through the protocol alone, so the transport is reset and the session
    /// caches describing the device's identity are dropped. A session already being disposed skips the
    /// recovery entirely: this runs on the failure path, and its own disposal error must not mask the
    /// exception being reported to the caller.
    /// <para>按 AOSP <c>AbortDownload</c> 的语义完成一次被弃单的下载：把设备仍在等待的字节补零发完，
    /// 随后消费尾部的终止帧，使 bootloader 的下载状态机回到空闲，下一条命令不会读到过期应答。
    /// 若 bootloader 始终不确认（或传输无法写入填充），仅靠协议已无法恢复会话，因此重置传输并丢弃
    /// 描述设备身份的会话缓存。已在释放中的会话会完全跳过恢复：本方法运行在失败路径上，它自己的释放
    /// 错误不得掩盖正要上报给调用方的异常。</para>
    /// </summary>
    /// <param name="sent">Payload bytes already written for this download. <para>本次下载已写入的负载字节数。</para></param>
    private void AbortDownload(long sent)
    {
        var remaining = Math.Max(0, _pendingDownloadBytes - sent);
        _pendingDownloadBytes = 0;
        if (_disposed)
        {
            return;
        }

        if (remaining > 0 && !ZeroFill(remaining))
        {
            ResetTransportAfterAbandon();
            return;
        }

        var drain = ReceiveResponse(Math.Min(_timeoutMs, AbortDrainTimeoutMs));
        if (drain.State is not (FastbootResponseState.Success or FastbootResponseState.Fail or FastbootResponseState.Unknown))
        {
            ResetTransportAfterAbandon();
        }
    }

    /// <summary>
    /// Writes <paramref name="count"/> zero bytes to the transport in bounded transfer chunks.
    /// <para>按有界传输分块向传输写入 <paramref name="count"/> 个零字节。</para>
    /// </summary>
    /// <returns><see langword="true"/> when every byte was accepted; <see langword="false"/> on a short write or a transport failure.
    /// <para>全部字节都被接受时为 <see langword="true"/>；短写或传输失败时为 <see langword="false"/>。</para></returns>
    private bool ZeroFill(long count)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(FastbootConstants.TransferChunkSize);
        try
        {
            Array.Clear(buffer, 0, FastbootConstants.TransferChunkSize);
            var remaining = count;
            while (remaining > 0)
            {
                var toWrite = (int)Math.Min(FastbootConstants.TransferChunkSize, remaining);
                long written;
                try
                {
                    written = Transport.Write(buffer, 0, toWrite, _timeoutMs);
                }
                catch (Exception)
                {
                    return false;
                }

                if (written != toWrite)
                {
                    return false;
                }

                remaining -= written;
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Recovers the session after an abandoned download could not be drained over the protocol:
    /// resets the transport (a reconnect for TCP/UDP, a port reset for USB), drops the retained
    /// response bytes so stale payload is never attributed to the next command, and invalidates the
    /// negotiated max-download-size because a re-enumerated device may report a different limit. A
    /// reset that itself fails is swallowed: this runs on the recovery path of an already-failing
    /// operation, and its own error must not mask the error being reported to the caller.
    /// <para>在协议层无法排空的弃单下载之后恢复会话：重置传输（TCP/UDP 为重连，USB 为端口重置），丢弃
    /// 保留的响应字节以免过期负载被算到下一条命令头上，并使已协商的 max-download-size 失效，因为重新
    /// 枚举的设备可能上报不同上限。重置自身失败会被吞掉：本方法运行在一次已失败操作的恢复路径上，
    /// 它自己的错误不得掩盖正要上报给调用方的错误。</para>
    /// </summary>
    private void ResetTransportAfterAbandon()
    {
        _pendingDownloadBytes = 0;
        _pendingBytes.Clear();
        InvalidateMaxDownloadSize();
        try
        {
            Transport.Reset();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Downloads a byte array to the device in chunks.
    /// <para>将字节数组分块下载到设备。</para>
    /// </summary>
    /// <param name="data">Payload bytes; the array is wrapped in a stream and never mutated. Empty arrays are rejected by the size check. <para>负载字节；数组被包装为流且不会被修改。空数组会被大小检查拒绝。</para></param>
    /// <param name="progress">Receives the cumulative sent byte count after each chunk; may be <see langword="null"/>. <para>每个分块发送后接收累计字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Checked before every chunk read; cancelling aborts the download mid-payload. <para>每次读取分块前检查；取消会在传输中途终止下载。</para></param>
    /// <returns>The terminal device response after the payload. <para>负载传输后的设备终态响应。</para></returns>
    /// <exception cref="FastbootException">The payload exceeds the negotiated max-download-size, or the transport short-wrote a chunk. <para>负载超过协商上限，或传输层短写。</para></exception>
    public FastbootResponse DownloadData(byte[] data, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream(data);
        return DownloadData(ms, data.Length, progress, cancellationToken);
    }

    /// <summary>
    /// Downloads a file to the device from disk.
    /// <para>从磁盘下载文件到设备。</para>
    /// </summary>
    /// <param name="filePath">Existing file opened read-only; its full length is the download size. <para>以只读方式打开的现有文件；其完整长度即下载大小。</para></param>
    /// <param name="progress">Receives the cumulative sent byte count after each chunk; may be <see langword="null"/>. <para>每个分块发送后接收累计字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Checked before every chunk read; cancelling aborts the download mid-payload. <para>每次读取分块前检查；取消会在传输中途终止下载。</para></param>
    /// <returns>The terminal device response after the payload. <para>负载传输后的设备终态响应。</para></returns>
    /// <exception cref="FileNotFoundException">The file does not exist. <para>文件不存在。</para></exception>
    /// <exception cref="FastbootException">The file exceeds the negotiated max-download-size, or the transport short-wrote a chunk. <para>文件超过协商上限，或传输层短写。</para></exception>
    public FastbootResponse DownloadFile(string filePath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var fs = File.OpenRead(filePath);
        return DownloadData(fs, fs.Length, progress, cancellationToken);
    }

    /// <summary>Executes a command that yields a DATA response, then receives the announced payload into the specified stream and awaits the trailing OKAY/FAIL. Used by fetch/upload/get_staged Style commands.
    /// <para>执行会产生 DATA 响应的命令，将声明的负载接收到指定流，并等待随后的 OKAY/FAIL。 供 fetch/upload/get_staged 类命令使用。</para></summary>
    /// <param name="command">Raw command sent first (e.g. <c>upload:&lt;name&gt;</c>); it must answer with a DATA frame.
    /// <para>首先发送的原始命令（如 <c>upload:&lt;name&gt;</c>）；必须返回 DATA 帧。</para></param>
    /// <param name="output">Stream the payload is appended to at its current position; the caller keeps ownership.
    /// <para>在当前位置追加载负载的流；所有权由调用方保留。</para></param>
    /// <param name="progress">Receives the cumulative received byte count after each chunk; may be <see langword="null"/>.
    /// <para>每个分块接收后接收累计字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Checked before every transport read; cancelling aborts the upload mid-payload.
    /// <para>每次传输读取前检查；取消会在传输中途终止上传。</para></param>
    /// <returns>The trailing OKAY/FAIL response after the payload.
    /// <para>负载之后的 OKAY/FAIL 终态响应。</para></returns>
    /// <exception cref="FastbootException">The command did not answer with a DATA response.
    /// <para>命令未返回 DATA 响应。</para></exception>
    /// <exception cref="EndOfStreamException">The device stopped sending before the announced payload size.
    /// <para>设备在声明的负载大小传完前停止发送。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已释放。</para></exception>
    public FastbootResponse UploadData(string command, Stream output, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var response = ExecuteCommand(command);
        if (response.State != FastbootResponseState.Data)
        {
            throw new FastbootException(response.State, $"Unexpected response for upload: expected DATA, got {response.State}");
        }

        ReadDataPayload(response.DataSize, output, progress, cancellationToken);
        return ReceiveResponse();
    }

    /// <summary>
    /// Uploads data from the device following an already-received DATA response.
    /// <para>在已收到的 DATA 响应之后从设备接收数据负载。</para>
    /// </summary>
    /// <param name="output">Stream the payload is appended to at its current position; the caller keeps ownership. <para>在当前位置追加载负载的流；所有权由调用方保留。</para></param>
    /// <param name="size">Exact payload size announced by the preceding DATA frame. <para>先前 DATA 帧声明的精确负载大小。</para></param>
    /// <param name="progress">Receives the cumulative received byte count after each chunk; may be <see langword="null"/>. <para>每个分块接收后接收累计字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Checked before every transport read; cancelling aborts the upload mid-payload. <para>每次传输读取前检查；取消会在传输中途终止上传。</para></param>
    /// <returns>The trailing OKAY/FAIL response after the payload. <para>负载之后的 OKAY/FAIL 终态响应。</para></returns>
    /// <exception cref="EndOfStreamException">The device stopped sending before <paramref name="size"/> bytes arrived. <para>设备在 <paramref name="size"/> 字节传完前停止发送。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed. <para>会话已释放。</para></exception>
    public FastbootResponse UploadData(Stream output, long size, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ReadDataPayload(size, output, progress, cancellationToken);
        return ReceiveResponse();
    }

    /// <summary>
    /// Reads the exact payload size announced by a DATA frame into the output stream.
    /// <para>将 DATA 帧声明的精确负载读取到输出流。DATA 帧与负载在同一传输读取中合并送达时
    /// （UDP 数据报等），帧解析保留在 <c>_pendingBytes</c> 中的超额字节会先于传输读取被消耗。</para>
    /// </summary>
    private void ReadDataPayload(long size, Stream output, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var chunkBuffer = ArrayPool<byte>.Shared.Rent(FastbootConstants.TransferChunkSize);
        try
        {
            long bytesRead = 0;
            while (bytesRead < size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(FastbootConstants.TransferChunkSize, size - bytesRead);
                var read = TakePendingBytes(chunkBuffer, 0, toRead);
                if (read < toRead)
                {
                    read += Transport.Read(chunkBuffer, read, toRead - read, _timeoutMs);
                }

                if (read <= 0)
                {
                    throw new EndOfStreamException($"Device EOF at {bytesRead}/{size}");
                }

                output.Write(chunkBuffer, 0, read);
                bytesRead += read;
                progress?.Report(bytesRead);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuffer);
        }
    }

    /// <summary>
    /// Consumes up to <paramref name="count"/> bytes previously retained by the response parser
    /// (bytes the transport delivered past a terminal frame) into <paramref name="target"/>, so
    /// coalesced payload bytes are never stranded in the pending buffer.
    /// <para>把响应解析器此前保留的超额字节（传输在终止帧之后送达的部分）最多
    /// <paramref name="count"/> 个移入 <paramref name="target"/>，合并送达的负载字节因此
    /// 不会滞留在待解析缓冲区中。</para>
    /// </summary>
    private int TakePendingBytes(byte[] target, int offset, int count)
    {
        var take = Math.Min(count, _pendingBytes.Count);
        if (take <= 0)
        {
            return 0;
        }

        _pendingBytes.Span[..take].CopyTo(target.AsSpan(offset, take));
        _pendingBytes.RemoveFromStart(take);
        return take;
    }

    /// <summary>Uploads a file from the device following an upload: command and writes it to the specified stream.
    /// <para>执行 upload: 命令从设备上传文件并写入指定流。</para></summary>
    /// <param name="filename">Device-side file path appended after <c>upload:</c>.
    /// <para>追加在 <c>upload:</c> 之后的设备侧文件路径。</para></param>
    /// <param name="output">Stream the file content is appended to at its current position; the caller keeps ownership.
    /// <para>在当前位置追加文件内容的流；所有权由调用方保留。</para></param>
    /// <param name="progress">Receives the cumulative received byte count after each chunk; may be <see langword="null"/>.
    /// <para>每个分块接收后接收累计字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Checked before every transport read; cancelling aborts the upload mid-payload.
    /// <para>每次传输读取前检查；取消会在传输中途终止上传。</para></param>
    /// <returns>The trailing OKAY/FAIL response after the payload.
    /// <para>负载之后的 OKAY/FAIL 终态响应。</para></returns>
    /// <exception cref="FastbootException">The <c>upload:</c> command did not answer with a DATA response.
    /// <para><c>upload:</c> 命令未返回 DATA 响应。</para></exception>
    /// <exception cref="EndOfStreamException">The device stopped sending before the announced payload size.
    /// <para>设备在声明的负载大小传完前停止发送。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public FastbootResponse UploadToStream(string filename, Stream output, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        => UploadData(FastbootConstants.CommandUpload + filename, output, progress, cancellationToken);

    /// <summary>
    /// Uploads a file from the device and saves it to the specified output path.
    /// <para>从设备上传文件并保存到指定输出路径。</para>
    /// </summary>
    /// <param name="filename">Device-side file path appended after <c>upload:</c>. <para>追加在 <c>upload:</c> 之后的设备侧文件路径。</para></param>
    /// <param name="outputPath">Local destination file, created or truncated before the transfer starts. <para>本地目标文件，在传输开始前创建或截断。</para></param>
    /// <returns>The trailing OKAY/FAIL response after the payload. <para>负载之后的 OKAY/FAIL 终态响应。</para></returns>
    /// <exception cref="IOException">The destination path cannot be created or written. <para>目标路径无法创建或写入。</para></exception>
    /// <exception cref="FastbootException">The <c>upload:</c> command did not answer with a DATA response. <para><c>upload:</c> 命令未返回 DATA 响应。</para></exception>
    public FastbootResponse Upload(string filename, string outputPath)
    {
        using var fs = File.Create(outputPath);
        return UploadToStream(filename, fs);
    }

    /// <summary>
    /// Uploads a file from the device and returns its contents as a byte array.
    /// <para>从设备上传文件并以字节数组形式返回其内容。</para>
    /// </summary>
    /// <param name="filename">Device-side file path appended after <c>upload:</c>. <para>追加在 <c>upload:</c> 之后的设备侧文件路径。</para></param>
    /// <returns>The complete file content buffered in memory. <para>缓冲在内存中的完整文件内容。</para></returns>
    /// <exception cref="FastbootException">The command did not answer with DATA, or the transfer did not end in OKAY. <para>命令未返回 DATA，或传输未以 OKAY 结束。</para></exception>
    /// <exception cref="EndOfStreamException">The device stopped sending before the announced payload size. <para>设备在声明的负载大小传完前停止发送。</para></exception>
    public byte[] UploadToBytes(string filename)
    {
        using var ms = new MemoryStream();
        var response = UploadData(FastbootConstants.CommandUpload + filename, ms);
        return response.State != FastbootResponseState.Success
            ? throw new FastbootException(response.State, "upload failed: " + response.Text)
            : ms.ToArray();
    }

    /// <summary>Fetches a partition image from the device and writes it to a stream, automatically fetching in chunks bounded by max-fetch-size when the partition is larger.
    /// <para>从设备获取分区镜像并写入流；分区超过 max-fetch-size 时自动分块获取。</para></summary>
    /// <param name="partition">The partition to fetch (e.g. "boot", "system").
    /// <para>要获取的分区（如 "boot"、"system"）。</para></param>
    /// <param name="output">The stream to write the fetched data to.<para>写入获取数据的流。</para></param>
    /// <param name="offset">Byte offset within the partition to start fetching.
    /// <para>分区内开始获取的字节偏移。</para></param>
    /// <param name="size">Number of bytes to fetch, or -1 for the entire partition.
    /// <para>要获取的字节数，-1 表示整个分区。</para></param>
    /// <param name="progress">Receives the cumulative fetched byte count.
    /// <para>接收累计已获取字节数。</para></param>
    /// <param name="cancellationToken">A cancellation token.<para>取消令牌。</para></param>
    /// <returns>A success response when the full range was written; in chunked mode the first non-success chunk response is returned early.
    /// <para>完整区间写入成功时返回成功响应；分块模式下提前返回第一个非成功分块的响应。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="partition"/> is null or empty.
    /// <para><paramref name="partition"/> 为空。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.
    /// <para><paramref name="offset"/> 为负数。</para></exception>
    /// <exception cref="FastbootException">A chunked <c>fetch:</c> command did not answer with a DATA response.
    /// <para>分块 <c>fetch:</c> 命令未返回 DATA 响应。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.
    /// <para>会话已释放。</para></exception>
    public FastbootResponse FetchToStream(string partition, Stream output, long offset = 0, long size = -1, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(partition))
        {
            throw new ArgumentException("Partition name must not be empty.", nameof(partition));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var targetPartition = ResolvePartitionWithSlot(partition);

        if (size == -1)
        {
            size = GetPartitionSizeLong(targetPartition);
        }

        var maxFetchSize = GetMaxFetchSize();
        if (size > 0 && maxFetchSize > 0 && size > maxFetchSize)
        {
            long fetched = 0;
            while (fetched < size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = Math.Min(maxFetchSize, size - fetched);
                var res = UploadData($"{FastbootConstants.CommandFetch}{targetPartition}:{offset + fetched:x}:{chunk:x}", output,
                    cancellationToken: cancellationToken);
                if (res.State != FastbootResponseState.Success)
                {
                    return res;
                }

                fetched += chunk;
                progress?.Report(fetched);
            }
            return new FastbootResponse { State = FastbootResponseState.Success };
        }

        var finalCommand = $"{FastbootConstants.CommandFetch}{targetPartition}:{offset:x}";
        if (size >= 0)
        {
            finalCommand += $":{size:x}";
        }

        return UploadData(finalCommand, output, progress, cancellationToken);
    }

    /// <summary>
    /// Fetches a partition image (optionally a portion of it) from the device and saves it
    /// to a file.
    /// <para>从设备获取分区镜像（可选其中一部分）并保存到文件。</para>
    /// </summary>
    /// <param name="partition">The partition to fetch. <para>要获取的分区。</para></param>
    /// <param name="outputPath">Path to save the fetched image. <para>保存获取镜像的路径。</para></param>
    /// <param name="offset">Byte offset within the partition to start fetching. <para>分区内开始获取的字节偏移。</para></param>
    /// <param name="size">Number of bytes to fetch, or -1 for the entire partition. <para>要获取的字节数，-1 表示整个分区。</para></param>
    /// <returns>The response from <see cref="FetchToStream"/>. <para>来自 <see cref="FetchToStream"/> 的响应。</para></returns>
    /// <exception cref="IOException">The destination path cannot be created or written. <para>目标路径无法创建或写入。</para></exception>
    public FastbootResponse Fetch(string partition, string outputPath, long offset = 0, long size = -1)
    {
        using var fs = File.Create(outputPath);
        return FetchToStream(partition, fs, offset, size);
    }

}