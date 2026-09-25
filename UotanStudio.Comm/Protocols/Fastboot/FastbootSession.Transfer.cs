using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Fastboot.Images;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Fastboot;

public sealed partial class FastbootSession
{
    /// <summary>
    /// Negotiates and caches the maximum download size supported by the device.
    /// Queries <c>max-download-size</c> via getvar, clamps against the wire-level ceiling
    /// (<see cref="FastbootConstants.SparseMaxDownloadSize"/>) and falls back to
    /// <see cref="FastbootConstants.FallbackMaxDownloadSize"/> when the device does not
    /// report a usable value.
    /// <para>协商并缓存设备支持的最大下载大小。通过 getvar 查询 <c>max-download-size</c>，
    /// 按线协议上限（<see cref="FastbootConstants.SparseMaxDownloadSize"/>）钳制；
    /// 设备未上报可用值时回退到 <see cref="FastbootConstants.FallbackMaxDownloadSize"/>。</para>
    /// </summary>
    /// <returns>The negotiated maximum in bytes, clamped to the wire ceiling and <see cref="uint.MaxValue"/>, cached after the first query.
    /// <para>字节计价的协商上限，按线协议上限与 <see cref="uint.MaxValue"/> 钳制，首次查询后缓存。</para></returns>
    public long GetMaxDownloadSize()
    {
        if (_maxDownloadSize is long cached)
        {
            return cached;
        }

        string? sizeStr = null;
        try { sizeStr = GetVariable(FastbootConstants.VarMaxDownloadSize); }
        catch { }

        long negotiated;
        if (string.IsNullOrEmpty(sizeStr))
        {
            negotiated = Math.Min(FastbootConstants.SparseMaxDownloadSize, FastbootConstants.FallbackMaxDownloadSize);
        }
        else
        {
            var trimmed = sizeStr.Trim();
            var parsedSize = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : -1
                : long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : -1;

            negotiated = parsedSize <= 0
                ? Math.Min(FastbootConstants.SparseMaxDownloadSize, FastbootConstants.FallbackMaxDownloadSize)
                : Math.Min(Math.Min(parsedSize, FastbootConstants.SparseMaxDownloadSize), uint.MaxValue);
        }

        _maxDownloadSize = negotiated;
        return negotiated;
    }

    /// <summary>
    /// Downloads data to the device: sends download:&lt;hex-size&gt;, streams data, and awaits OKAY/FAIL.
    /// Payloads exceeding the negotiated max download size are rejected.
    /// <para>下载数据到设备：发送 download:&lt;hex-size&gt;，流式传输数据，等待 OKAY/FAIL。
    /// 超过协商的最大下载大小的负载会被拒绝。</para>
    /// </summary>
    /// <param name="stream">
    /// Payload source, read in transfer-chunk sized increments up to <paramref name="length"/> bytes; the caller keeps ownership.
    /// <para>负载来源，按传输分块大小递增读取至多 <paramref name="length"/> 字节；所有权由调用方保留。</para>
    /// </param>
    /// <param name="length">
    /// Exact payload size in bytes; must be between 1 and <see cref="uint.MaxValue"/> and no more than the negotiated max-download-size.
    /// <para>精确负载字节数；必须在 1 到 <see cref="uint.MaxValue"/> 之间，且不超过协商的 max-download-size。</para>
    /// </param>
    /// <param name="progress">Receives the cumulative sent byte count after each chunk; may be <see langword="null"/>. <para>每个分块发送后接收累计字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Checked before every chunk read; cancelling aborts the download mid-payload. <para>每次读取分块前检查；取消会在传输中途终止下载。</para></param>
    /// <returns>The terminal device response after the payload. A size-mismatch DATA reply is returned with its text rewritten and no payload sent.
    /// <para>负载传输后的设备终态响应。大小不匹配的 DATA 响应会在改写文本后返回且不发送负载。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not in 1..<see cref="uint.MaxValue"/>. <para><paramref name="length"/> 不在 1 到 <see cref="uint.MaxValue"/> 范围内。</para></exception>
    /// <exception cref="FastbootException">The payload exceeds the negotiated max-download-size, or the transport short-wrote a chunk. <para>负载超过协商上限，或传输层短写。</para></exception>
    /// <exception cref="EndOfStreamException">The source stream ended before <paramref name="length"/> bytes were read. <para>源流在读取满 <paramref name="length"/> 字节前提前结束。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed. <para>会话已释放。</para></exception>
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
        if (rawResponse.State != FastbootResponseState.Data || rawResponse.DataSize != length)
        {
            rawResponse.Text = $"download size mismatch: requested {length}, device accepted {rawResponse.DataSize}";
            return rawResponse;
        }

        var chunkBuffer = ArrayPool<byte>.Shared.Rent(FastbootConstants.TransferChunkSize);
        try
        {
            long bytesWritten = 0;
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
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuffer);
        }

        return ReceiveResponse();
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

    /// <summary>
    /// Executes a command that yields a DATA response, then receives the announced payload
    /// into the specified stream and awaits the trailing OKAY/FAIL.
    /// Used by fetch/upload/get_staged Style commands.
    /// <para>执行会产生 DATA 响应的命令，将声明的负载接收到指定流，并等待随后的 OKAY/FAIL。
    /// 供 fetch/upload/get_staged 类命令使用。</para>
    /// </summary>
    /// <param name="command">Raw command sent first (e.g. <c>upload:&lt;name&gt;</c>); it must answer with a DATA frame. <para>首先发送的原始命令（如 <c>upload:&lt;name&gt;</c>）；必须返回 DATA 帧。</para></param>
    /// <param name="output">Stream the payload is appended to at its current position; the caller keeps ownership. <para>在当前位置追加载负载的流；所有权由调用方保留。</para></param>
    /// <param name="progress">Receives the cumulative received byte count after each chunk; may be <see langword="null"/>. <para>每个分块接收后接收累计字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Checked before every transport read; cancelling aborts the upload mid-payload. <para>每次传输读取前检查；取消会在传输中途终止上传。</para></param>
    /// <returns>The trailing OKAY/FAIL response after the payload. <para>负载之后的 OKAY/FAIL 终态响应。</para></returns>
    /// <exception cref="FastbootException">The command did not answer with a DATA response. <para>命令未返回 DATA 响应。</para></exception>
    /// <exception cref="EndOfStreamException">The device stopped sending before the announced payload size. <para>设备在声明的负载大小传完前停止发送。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed. <para>会话已释放。</para></exception>
    public FastbootResponse UploadData(string command, Stream output, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
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
    /// <para>将 DATA 帧声明的精确负载读取到输出流。</para>
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
                var read = Transport.Read(chunkBuffer, 0, toRead, _timeoutMs);
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
    /// Uploads a file from the device following an upload: command and writes it to the
    /// specified stream.
    /// <para>执行 upload: 命令从设备上传文件并写入指定流。</para>
    /// </summary>
    /// <param name="filename">Device-side file path appended after <c>upload:</c>. <para>追加在 <c>upload:</c> 之后的设备侧文件路径。</para></param>
    /// <param name="output">Stream the file content is appended to at its current position; the caller keeps ownership. <para>在当前位置追加文件内容的流；所有权由调用方保留。</para></param>
    /// <param name="progress">Receives the cumulative received byte count after each chunk; may be <see langword="null"/>. <para>每个分块接收后接收累计字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Checked before every transport read; cancelling aborts the upload mid-payload. <para>每次传输读取前检查；取消会在传输中途终止上传。</para></param>
    /// <returns>The trailing OKAY/FAIL response after the payload. <para>负载之后的 OKAY/FAIL 终态响应。</para></returns>
    /// <exception cref="FastbootException">The <c>upload:</c> command did not answer with a DATA response. <para><c>upload:</c> 命令未返回 DATA 响应。</para></exception>
    /// <exception cref="EndOfStreamException">The device stopped sending before the announced payload size. <para>设备在声明的负载大小传完前停止发送。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
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

    /// <summary>
    /// Fetches a partition image from the device and writes it to a stream, automatically
    /// fetching in chunks bounded by max-fetch-size when the partition is larger.
    /// <para>从设备获取分区镜像并写入流；分区超过 max-fetch-size 时自动分块获取。</para>
    /// </summary>
    /// <param name="partition">The partition to fetch (e.g. "boot", "system"). <para>要获取的分区（如 "boot"、"system"）。</para></param>
    /// <param name="output">The stream to write the fetched data to. <para>写入获取数据的流。</para></param>
    /// <param name="offset">Byte offset within the partition to start fetching. <para>分区内开始获取的字节偏移。</para></param>
    /// <param name="size">Number of bytes to fetch, or -1 for the entire partition. <para>要获取的字节数，-1 表示整个分区。</para></param>
    /// <param name="progress">Receives the cumulative fetched byte count. <para>接收累计已获取字节数。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A success response when the full range was written; in chunked mode the first non-success chunk response is returned early.
    /// <para>完整区间写入成功时返回成功响应；分块模式下提前返回第一个非成功分块的响应。</para>
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="partition"/> is null or empty. <para><paramref name="partition"/> 为空。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative. <para><paramref name="offset"/> 为负数。</para></exception>
    /// <exception cref="FastbootException">A chunked <c>fetch:</c> command did not answer with a DATA response. <para>分块 <c>fetch:</c> 命令未返回 DATA 响应。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed. <para>会话已释放。</para></exception>
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