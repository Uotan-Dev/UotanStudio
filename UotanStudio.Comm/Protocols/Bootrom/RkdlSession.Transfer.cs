using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Bootrom;

public sealed partial class RkdlSession
{
    /// <summary>
    /// Writes the given GPT image to the device. The image may be a 34-sector master table
    /// only (the backup table is then generated from it) or a 67-sector master+backup image
    /// (34 + 33 sectors, as loaded by rkdeveloptool load_gpt_buffer). The tables are updated
    /// for the device's total sector count and written at LBA 0 / LBA (total - 33).
    /// <para>将给定 GPT 镜像写入设备。镜像可以是仅 34 扇区的主表（此时据此生成备份表），
    /// 也可以是 67 扇区的主+备镜像（34 + 33 扇区，如同 rkdeveloptool load_gpt_buffer 加载的
    /// 文件）。表会按设备总扇区数更新，并写入 LBA 0 / LBA（总扇区 - 33）。</para>
    /// </summary>
    /// <param name="gptData">The GPT image bytes. <para>GPT 镜像字节。</para></param>
    /// <param name="progress">Optional progress callback (bytes written). <para>可选进度回调（已写入字节数）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <exception cref="RkdlException">The image is smaller than the 34-sector primary table, the
    /// device-reported sector count is not greater than <see cref="RkdlGpt.BackupSectors"/> or
    /// exceeds <see cref="uint.MaxValue"/>, or a WRITE_LBA transaction failed. The sector count is
    /// validated as a <see cref="long"/> before the unsigned cast, so a malformed device response
    /// is rejected instead of wrapping into an LBA that would overwrite user data.
    /// <para>镜像小于 34 扇区主表、设备报告的扇区数未大于 <see cref="RkdlGpt.BackupSectors"/>
    /// 或超过 <see cref="uint.MaxValue"/>，或 WRITE_LBA 事务失败。扇区数在无符号转型前按
    /// <see cref="long"/> 校验，因此畸形设备响应会被拒绝，而不会回绕成一个覆盖用户数据的 LBA。</para></exception>
    public void WriteGPT(byte[] gptData, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (gptData == null || gptData.Length < RkdlConstants.SectorSize * RkdlGpt.PrimarySectors)
        {
            throw new RkdlException(RkdlError.InvalidParameter, "GPT image must span at least 34 sectors.");
        }

        var sectorCount = RequireFlashGeometry().SectorCount;
        if (sectorCount <= RkdlGpt.BackupSectors || sectorCount > uint.MaxValue)
        {
            throw new RkdlException(RkdlError.FlashInfoFailed,
                $"Invalid flash sector count: {sectorCount} (must be greater than {RkdlGpt.BackupSectors} and at most {uint.MaxValue}).");
        }

        var totalSectors = (uint)sectorCount;

        var masterLen = RkdlConstants.SectorSize * RkdlGpt.PrimarySectors;
        var master = new byte[masterLen];
        Array.Copy(gptData, master, masterLen);

        byte[] backup;
        if (gptData.Length >= masterLen + (RkdlConstants.SectorSize * RkdlGpt.BackupSectors))
        {
            backup = new byte[RkdlConstants.SectorSize * RkdlGpt.BackupSectors];
            Array.Copy(gptData, masterLen, backup, 0, backup.Length);
        }
        else
        {
            backup = RkdlGpt.BuildBackupGpt(master);
        }

        RkdlGpt.UpdateDiskSize(master, backup, totalSectors);

        using (var masterStream = new MemoryStream(master, writable: false))
        {
            WriteLBA(0, masterStream, master.Length, progress, cancellationToken);
        }

        long backupLba = totalSectors - RkdlGpt.BackupSectors;
        using var backupStream = new MemoryStream(backup, writable: false);
        WriteLBA(backupLba, backupStream, backup.Length, null, cancellationToken);
    }

    /// <summary>
    /// Writes data to flash at the specified LBA address (in 512-byte sectors) using
    /// WRITE_LBA (0x15) CBW/CSW transactions, chunked by
    /// <see cref="RkdlConstants.TransferChunkSize"/>. Each chunk is filled from the stream with a
    /// read loop, so a short <see cref="Stream.Read(byte[], int, int)"/> never truncates a chunk,
    /// never repeats source bytes and never writes zero padding into the middle of the image; only
    /// the final chunk of a non sector-multiple <paramref name="length"/> is zero padded up to a
    /// sector boundary.
    /// <para>使用 WRITE_LBA（0x15）CBW/CSW 事务，将数据写入指定 LBA 地址（512 字节扇区），
    /// 按 <see cref="RkdlConstants.TransferChunkSize"/> 分块。每个分块都用读循环从流中填满，
    /// 因此 <see cref="Stream.Read(byte[], int, int)"/> 的短读既不会截断分块，也不会重复源字节或把补零写进镜像
    /// 中段；只有当 <paramref name="length"/> 不是扇区整数倍时，最后一个分块才会向上补零到扇区
    /// 边界。</para>
    /// </summary>
    /// <param name="address">The start LBA; must fit in an unsigned 32-bit value.
    /// <para>起始 LBA；必须能容纳于无符号 32 位值。</para></param>
    /// <param name="dataStream">The source stream read sequentially; not disposed by this call.
    /// <para>被顺序读取的源流；本调用不会释放它。</para></param>
    /// <param name="length">Total bytes to transfer from the stream; a trailing partial sector is
    /// zero padded on the wire. <para>需从流中转出的总字节数；尾部不完整扇区在线上补零。</para></param>
    /// <param name="progress">Optional receiver of cumulative source bytes written.
    /// <para>可选的累计已写出源字节接收器。</para></param>
    /// <param name="cancellationToken">Checked before each chunk; the token does not interrupt an
    /// in-flight synchronous bulk transfer. <para>每个分块前检查；令牌不会打断进行中的同步批量传输。</para></param>
    /// <returns>The number of source bytes transferred, equal to <paramref name="length"/> on success.
    /// <para>成功时转出的源字节数，等于 <paramref name="length"/>。</para></returns>
    /// <exception cref="RkdlException">The address is out of range, the byte length rounded up to
    /// 512-byte sectors leaves the addressed range beyond the device geometry, the device geometry
    /// is unusable because READ_FLASH_INFO reported an invalid sector count
    /// (<see cref="RkdlError.FlashInfoFailed"/>), a bulk write was short, or a CSW reported
    /// failure. <para>地址越界、字节长度向上取整为 512 字节扇区后所寻址范围超出设备几何、因
    /// READ_FLASH_INFO 报告无效扇区数导致设备几何不可用（<see cref="RkdlError.FlashInfoFailed"/>）、
    /// 批量写入过短或 CSW 报告失败。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="dataStream"/> is <c>null</c>.
    /// <para><paramref name="dataStream"/> 为 <c>null</c>。</para></exception>
    /// <exception cref="EndOfStreamException">The stream ended before <paramref name="length"/> bytes were read.
    /// <para>流在读满 <paramref name="length"/> 字节前结束。</para></exception>
    /// <exception cref="ObjectDisposedException">The session was disposed.
    /// <para>会话已被释放。</para></exception>
    public long WriteLBA(long address, Stream dataStream, long length, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (address is < 0 or > uint.MaxValue)
        {
            throw new RkdlException(RkdlError.InvalidParameter, $"LBA address out of range: {address}.");
        }

        if (dataStream == null)
        {
            throw new ArgumentNullException(nameof(dataStream));
        }

        var requiredSectors = length <= 0 ? 0L : checked((length - 1) / RkdlConstants.SectorSize + 1);
        ValidateLbaRange(address, requiredSectors);

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(RkdlConstants.TransferChunkSize);
        try
        {
            long totalWritten = 0;
            var currentSector = address;
            while (totalWritten < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(RkdlConstants.TransferChunkSize, length - totalWritten);

                var n = 0;
                while (n < toRead)
                {
                    var read = dataStream.Read(buffer, n, toRead - n);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException($"Stream ended at {totalWritten + n}/{length}");
                    }

                    n += read;
                }

                var isFinalChunk = totalWritten + n == length;
                var sectorBytes = isFinalChunk
                    ? ((n + RkdlConstants.SectorSize - 1) / RkdlConstants.SectorSize) * RkdlConstants.SectorSize
                    : n;
                if (sectorBytes > n)
                {
                    Array.Clear(buffer, n, sectorBytes - n);
                }

                var sectorCount = sectorBytes / RkdlConstants.SectorSize;

                var cbw = new RkdlCbw(RkdlCommand.WriteLba, RkdlFrame.DirectionOut, (uint)sectorBytes,
                    RkdlFrame.CbwcbLengthLong, address: (uint)currentSector, sectorLength: (ushort)sectorCount);
                WriteCbw(cbw);

                var written = Transport.Write(buffer, 0, sectorBytes, _timeoutMs);
                if (written != sectorBytes)
                {
                    throw new RkdlException(RkdlError.WriteFailed, $"Short write: {written}/{sectorBytes}.");
                }

                var csw = ReadCsw(cbw.Tag);
                if (!csw.IsSuccess)
                {
                    throw new RkdlException(RkdlError.WriteFailed, $"WRITE_LBA failed at LBA {currentSector}, CSW status = {csw.Status}.");
                }

                currentSector += sectorCount;
                totalWritten += n;
                progress?.Report(totalWritten);
            }
            return totalWritten;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>
    /// Reads data from flash at the specified LBA address (in 512-byte sectors) using
    /// READ_LBA (0x14) CBW/CSW transactions, chunked by
    /// <see cref="RkdlConstants.TransferChunkSize"/>. The output is written in full sectors.
    /// <para>使用 READ_LBA（0x14）CBW/CSW 事务，从指定 LBA 地址读取 flash 数据
    /// （512 字节扇区），按 <see cref="RkdlConstants.TransferChunkSize"/> 分块。
    /// 输出按完整扇区写入。</para>
    /// </summary>
    /// <param name="address">The start LBA; must fit in an unsigned 32-bit value.
    /// <para>起始 LBA；必须能容纳于无符号 32 位值。</para></param>
    /// <param name="outputStream">The destination stream written sequentially; not disposed by this call.
    /// <para>被顺序写入的目标流；本调用不会释放它。</para></param>
    /// <param name="length">Total bytes to read from flash; the final chunk is rounded up to full
    /// sectors on the wire and trimmed to <paramref name="length"/> in the output.
    /// <para>需从 flash 读取的总字节数；最后一个分块在线上按完整扇区向上取整，写入输出时裁剪至
    /// <paramref name="length"/>。</para></param>
    /// <param name="progress">Optional receiver of cumulative bytes written to the output stream.
    /// <para>可选的累计写入输出流字节数接收器。</para></param>
    /// <param name="cancellationToken">Checked before each chunk; the token does not interrupt an
    /// in-flight synchronous bulk transfer. <para>每个分块前检查；令牌不会打断进行中的同步批量传输。</para></param>
    /// <returns>The number of bytes written to <paramref name="outputStream"/>, equal to
    /// <paramref name="length"/> on success. <para>写入 <paramref name="outputStream"/> 的字节数，
    /// 成功时等于 <paramref name="length"/>。</para></returns>
    /// <exception cref="RkdlException">The address is out of range, the byte length rounded up to
    /// 512-byte sectors leaves the addressed range beyond the device geometry, the device geometry
    /// is unusable because READ_FLASH_INFO reported an invalid sector count
    /// (<see cref="RkdlError.FlashInfoFailed"/>), the device signaled EOF early, or a CSW reported
    /// failure. <para>地址越界、字节长度向上取整为 512 字节扇区后所寻址范围超出设备几何、因
    /// READ_FLASH_INFO 报告无效扇区数导致设备几何不可用（<see cref="RkdlError.FlashInfoFailed"/>）、
    /// 设备提前报告 EOF 或 CSW 报告失败。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="outputStream"/> is <c>null</c>.
    /// <para><paramref name="outputStream"/> 为 <c>null</c>。</para></exception>
    /// <exception cref="ObjectDisposedException">The session was disposed.
    /// <para>会话已被释放。</para></exception>
    public long ReadLBA(long address, Stream outputStream, long length, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (address is < 0 or > uint.MaxValue)
        {
            throw new RkdlException(RkdlError.InvalidParameter, $"LBA address out of range: {address}.");
        }

        if (outputStream == null)
        {
            throw new ArgumentNullException(nameof(outputStream));
        }

        var requiredSectors = length <= 0 ? 0L : checked((length - 1) / RkdlConstants.SectorSize + 1);
        ValidateLbaRange(address, requiredSectors);

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(RkdlConstants.TransferChunkSize);
        try
        {
            long totalRead = 0;
            var currentSector = address;
            while (totalRead < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(RkdlConstants.TransferChunkSize, length - totalRead);
                var sectorBytes = (toRead + RkdlConstants.SectorSize - 1) / RkdlConstants.SectorSize * RkdlConstants.SectorSize;
                var sectorCount = sectorBytes / RkdlConstants.SectorSize;

                var cbw = new RkdlCbw(RkdlCommand.ReadLba, RkdlFrame.DirectionIn, (uint)sectorBytes,
                    RkdlFrame.CbwcbLengthLong, address: (uint)currentSector, sectorLength: (ushort)sectorCount);
                WriteCbw(cbw);

                var n = 0;
                while (n < sectorBytes)
                {
                    var r = Transport.Read(buffer, n, sectorBytes - n, _timeoutMs);
                    if (r == 0)
                    {
                        throw new RkdlException(RkdlError.ReadFailed, $"Device EOF at {totalRead}/{length}.");
                    }

                    n += r;
                }

                var csw = ReadCsw(cbw.Tag);
                if (!csw.IsSuccess)
                {
                    throw new RkdlException(RkdlError.ReadFailed, $"READ_LBA failed at LBA {currentSector}, CSW status = {csw.Status}.");
                }

                var writeCount = Math.Min(n, toRead);
                outputStream.Write(buffer, 0, writeCount);
                currentSector += sectorCount;
                totalRead += writeCount;
                progress?.Report(totalRead);
            }
            return totalRead;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>
    /// Downloads a loader through the 0x0471/0x0472 vendor request, framing the payload in
    /// 4096-byte control transfers with a trailing CRC-16/CCITT (rkdeveloptool RKU_DeviceRequest).
    /// <para>通过 0x0471/0x0472 vendor 请求下载 loader，将负载按 4096 字节控制传输分帧，
    /// 尾部追加 CRC-16/CCITT（rkdeveloptool RKU_DeviceRequest）。</para>
    /// </summary>
    /// <param name="wIndex">The vendor request wIndex (0x0471 or 0x0472). <para>vendor 请求 wIndex（0x0471 或 0x0472）。</para></param>
    /// <param name="loaderData">The loader image data. <para>loader 镜像数据。</para></param>
    /// <returns>The total number of bytes transferred. <para>实际传输的字节总数。</para></returns>
    private int DeviceRequest(ushort wIndex, byte[] loaderData)
    {
        ThrowIfDisposed();
        if (loaderData == null || loaderData.Length == 0)
        {
            throw new RkdlException(RkdlError.InvalidParameter, "Loader data cannot be null or empty.");
        }

        if (loaderData.Length > RkdlConstants.MaxLoaderSize)
        {
            throw new RkdlException(RkdlError.LoaderTooLarge, $"Loader data too large: {loaderData.Length} > {RkdlConstants.MaxLoaderSize}.");
        }

        var usb = GetUsbSession();

        var dataLength = loaderData.Length;
        var sendPadPacket = false;
        switch (dataLength % RkdlConstants.LoaderChunkSize)
        {
            case RkdlConstants.LoaderChunkSize - 1:
                dataLength += 1;
                break;
            case RkdlConstants.LoaderChunkSize - 2:
                sendPadPacket = true;
                break;
            default:
                break;
        }

        var buffer = new byte[dataLength + 2];
        Array.Copy(loaderData, buffer, loaderData.Length);

        var crc = RkdlCrc.Crc16Ccitt(buffer.AsSpan(0, dataLength));
        buffer[dataLength] = (byte)(crc >> 8);
        buffer[dataLength + 1] = (byte)(crc & 0xFF);
        var totalLength = dataLength + 2;

        var offset = 0;
        while (offset < totalLength)
        {
            var chunk = Math.Min(RkdlConstants.LoaderChunkSize, totalLength - offset);
            var setupPacket = new UsbSetupPacket
            {
                RequestType = 0x40,
                Request = RkdlConstants.RkdlRequestDownloadBoot,
                Value = 0,
                Index = wIndex,
                Length = (ushort)chunk
            };
            var sent = usb.ControlTransfer(setupPacket, buffer, offset, chunk, _timeoutMs);
            if (sent != chunk)
            {
                throw new RkdlException(RkdlError.TransferFailed, $"Loader vendor request 0x{wIndex:X4} failed: {sent}/{chunk} bytes.");
            }

            offset += sent;
        }

        if (sendPadPacket)
        {
            var zero = new byte[1];
            var setupPacket = new UsbSetupPacket
            {
                RequestType = 0x40,
                Request = RkdlConstants.RkdlRequestDownloadBoot,
                Value = 0,
                Index = wIndex,
                Length = 1
            };
            var sent = usb.ControlTransfer(setupPacket, zero, 0, 1, _timeoutMs);
            if (sent != 1)
            {
                throw new RkdlException(RkdlError.TransferFailed, $"Loader padding packet failed: {sent} bytes.");
            }
        }

        return totalLength;
    }

    /// <summary>
    /// Runs a data-in CBW/CSW transaction: writes the CBW, reads the data phase, reads and
    /// validates the CSW, and returns the data bytes.
    /// <para>执行数据输入 CBW/CSW 事务：写入 CBW、读取数据阶段、读取并校验 CSW，返回数据字节。</para>
    /// </summary>
    /// <param name="opcode">The rockusb operation code. <para>rockusb 操作码。</para></param>
    /// <param name="transferLength">dwCBWTransferLength (data phase length). <para>dwCBWTransferLength（数据阶段长度）。</para></param>
    /// <param name="cbwcbLength">The CBWCB length (0x06 or 0x0A). <para>CBWCB 长度（0x06 或 0x0A）。</para></param>
    /// <param name="readBufferSize">The size of the read buffer. <para>读取缓冲区大小。</para></param>
    /// <param name="requiredMinimum">The minimum number of data bytes required. <para>所需的最小数据字节数。</para></param>
    /// <returns>The data bytes read from the device. <para>从设备读取的数据字节。</para></returns>
    private byte[] ReadDataCommand(RkdlCommand opcode, uint transferLength, byte cbwcbLength, int readBufferSize, int requiredMinimum)
    {
        ThrowIfDisposed();
        var cbw = new RkdlCbw(opcode, RkdlFrame.DirectionIn, transferLength, cbwcbLength);
        WriteCbw(cbw);

        var buffer = new byte[readBufferSize];
        var n = 0;
        while (n < readBufferSize)
        {
            var requested = readBufferSize - n;
            var chunk = Transport.Read(buffer, n, requested, _timeoutMs);
            if (chunk <= 0)
            {
                break;
            }

            n += chunk;
            if (chunk < requested)
            {
                break;
            }
        }

        if (n < requiredMinimum)
        {
            throw new RkdlException(RkdlError.ReadFailed, $"Short data read: {n} < {requiredMinimum}.");
        }

        var csw = ReadCsw(cbw.Tag);
        if (!csw.IsSuccess)
        {
            throw new RkdlException(RkdlError.ReadFailed, $"Command 0x{(byte)opcode:X2} failed, CSW status = {csw.Status}.");
        }

        if (n < readBufferSize)
        {
            Array.Resize(ref buffer, n);
        }

        return buffer;
    }

    /// <summary>
    /// Writes a CBW to the bulk OUT endpoint.
    /// <para>将 CBW 写入 bulk OUT 端点。</para>
    /// </summary>
    /// <param name="cbw">The CBW to write. <para>要写入的 CBW。</para></param>
    private void WriteCbw(RkdlCbw cbw)
    {
        var bytes = cbw.ToBytes();
        var written = Transport.Write(bytes, 0, bytes.Length, _timeoutMs);
        if (written != bytes.Length)
        {
            throw new RkdlException(RkdlError.TransferFailed, $"CBW short write: {written}/{bytes.Length}.");
        }
    }

    /// <summary>
    /// Reads a CSW from the bulk IN endpoint and validates signature + tag (UFI_CHECK_SIGN).
    /// When <paramref name="recovery"/> is set, a mismatching CSW is drained up to 16 KB with
    /// up to three attempts (see rkdeveloptool RKU_ClearBuffer).
    /// <para>从 bulk IN 端点读取 CSW 并校验签名与标签（UFI_CHECK_SIGN）。
    /// 当 <paramref name="recovery"/> 开启时，不匹配的 CSW 最多清空 16 KB、重试 3 次
    /// （见 rkdeveloptool RKU_ClearBuffer）。</para>
    /// </summary>
    /// <param name="tag">The CBW tag the CSW must echo. <para>CSW 必须回显的 CBW 标签。</para></param>
    /// <param name="recovery">Whether to drain and retry on a mismatch. <para>不匹配时是否清空并重试。</para></param>
    /// <returns>The validated CSW. <para>校验通过的 CSW。</para></returns>
    private RkdlCsw ReadCsw(uint tag, bool recovery = false)
    {
        var buffer = new byte[RkdlFrame.CswLength];
        var tryCount = recovery ? 3 : 1;
        var totalRead = 0;
        while (tryCount > 0 && totalRead < 16 * 1024)
        {
            var n = Transport.Read(buffer, 0, buffer.Length, _timeoutMs);
            totalRead += n;
            if (n == buffer.Length && RkdlCsw.TryParse(buffer, tag, out var csw))
            {
                return csw!;
            }

            tryCount--;
        }
        throw new RkdlException(RkdlError.TransferFailed, "CSW validation failed (signature or tag mismatch).");
    }

}