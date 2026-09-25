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
    public long GetMaxDownloadSize()
    {
        if (_maxDownloadSize is long cached)
        {
            return cached;
        }

        string? sizeStr = null;
        try { sizeStr = GetVariable(FastbootConstants.VarMaxDownloadSize); }
        catch { /* transport errors fall back to the conservative default */ }

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
    public FastbootResponse DownloadData(byte[] data, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream(data);
        return DownloadData(ms, data.Length, progress, cancellationToken);
    }

    /// <summary>
    /// Downloads a file to the device from disk.
    /// <para>从磁盘下载文件到设备。</para>
    /// </summary>
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
    public FastbootResponse UploadToStream(string filename, Stream output, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        => UploadData(FastbootConstants.CommandUpload + filename, output, progress, cancellationToken);

    /// <summary>
    /// Uploads a file from the device and saves it to the specified output path.
    /// <para>从设备上传文件并保存到指定输出路径。</para>
    /// </summary>
    public FastbootResponse Upload(string filename, string outputPath)
    {
        using var fs = File.Create(outputPath);
        return UploadToStream(filename, fs);
    }

    /// <summary>
    /// Uploads a file from the device and returns its contents as a byte array.
    /// <para>从设备上传文件并以字节数组形式返回其内容。</para>
    /// </summary>
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
    public FastbootResponse Fetch(string partition, string outputPath, long offset = 0, long size = -1)
    {
        using var fs = File.Create(outputPath);
        return FetchToStream(partition, fs, offset, size);
    }

}