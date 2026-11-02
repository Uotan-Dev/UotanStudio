using UotanStudio.Comm.Diagnostics;

namespace UotanStudio.Comm.Protocols.Bootrom;

public sealed partial class RkdlSession
{
    /// <summary>
    /// Writes raw data into device SDRAM through a WRITE_SDRAM (0x18) CBW/CSW transaction, the
    /// first step of the mask ROM's execute-from-memory path.
    /// <para>通过 WRITE_SDRAM（0x18）CBW/CSW 事务把原始数据写入设备 SDRAM，这是 Mask ROM 内存执行路径的
    /// 第一步。</para>
    /// </summary>
    /// <remarks>
    /// The official tool always sends this command with the vendor's fixed 0xAA sub code and caps the
    /// transfer at one command; callers chunk larger images themselves. Mask ROM answers this command,
    /// so it is the only write path that works before a loader is running.
    /// <para>官方工具发送该命令时始终携带厂商固定的 0xAA 子码，且一条命令一次传输；更大的镜像由调用方自行
    /// 分块。Mask ROM 会应答该命令，因此它是在 loader 运行前唯一可用的写入路径。</para>
    /// </remarks>
    /// <param name="data">The bytes to place in SDRAM; at most one command's worth.
    /// <para>要写入 SDRAM 的字节；不超过一条命令的容量。</para></param>
    /// <exception cref="RkdlException">The payload is empty or longer than 64 KB, or the transaction failed.
    /// <para>负载为空或超过 64 KB，或事务失败。</para></exception>
    public void WriteSdram(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        if (data.Length is <= 0 or > RkdlConstants.TransferChunkSize)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"WRITE_SDRAM payload must be between 1 and {RkdlConstants.TransferChunkSize} bytes, but is {data.Length} bytes.");
        }

        var payload = data.ToArray();
        var cbw = new RkdlCbw(RkdlCommand.WriteSdram, RkdlFrame.DirectionOut, (uint)payload.Length,
            RkdlFrame.CbwcbLengthLong, subCode: RkdlConstants.SdramVendorSubCode, sectorLength: (ushort)payload.Length);
        WriteCbw(cbw);
        var written = Transport.Write(payload, 0, payload.Length, _timeoutMs);
        if (written != payload.Length)
        {
            throw new RkdlException(RkdlError.TransferFailed,
                $"WRITE_SDRAM payload short write: {written}/{payload.Length}.");
        }

        var csw = ReadCsw(cbw.Tag);
        if (!csw.IsSuccess)
        {
            throw new RkdlException(RkdlError.WriteFailed, $"WRITE_SDRAM failed, CSW status = {csw.Status}.");
        }
    }

    /// <summary>
    /// Reads back device SDRAM through a READ_SDRAM (0x17) CBW/CSW transaction.
    /// <para>通过 READ_SDRAM（0x17）CBW/CSW 事务读回设备 SDRAM。</para>
    /// </summary>
    /// <param name="byteCount">The number of bytes to read, at most one command's worth.
    /// <para>要读取的字节数；不超过一条命令的容量。</para></param>
    /// <returns>The bytes read from SDRAM. <para>从 SDRAM 读回的字节。</para></returns>
    /// <exception cref="RkdlException"><paramref name="byteCount"/> is outside 1–64 KB or the transaction failed.
    /// <para><paramref name="byteCount"/> 不在 1–64 KB 之间或事务失败。</para></exception>
    public byte[] ReadSdram(int byteCount)
    {
        ThrowIfDisposed();
        if (byteCount is <= 0 or > RkdlConstants.TransferChunkSize)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"READ_SDRAM transfer must be between 1 and {RkdlConstants.TransferChunkSize} bytes, but is {byteCount} bytes.");
        }

        var cbw = new RkdlCbw(RkdlCommand.ReadSdram, RkdlFrame.DirectionIn, (uint)byteCount,
            RkdlFrame.CbwcbLengthLong, sectorLength: (ushort)byteCount);
        WriteCbw(cbw);
        var buffer = new byte[byteCount];
        var n = 0;
        while (n < byteCount)
        {
            var chunk = Transport.Read(buffer, n, byteCount - n, _timeoutMs);
            if (chunk <= 0)
            {
                break;
            }

            n += chunk;
        }

        var csw = ReadCsw(cbw.Tag);
        if (!csw.IsSuccess)
        {
            throw new RkdlException(RkdlError.ReadFailed, $"READ_SDRAM failed, CSW status = {csw.Status}.");
        }

        if (n < byteCount)
        {
            throw new RkdlException(RkdlError.ReadFailed, $"Short SDRAM read: {n} < {byteCount}.");
        }

        return buffer;
    }

    /// <summary>
    /// Asks the device to jump to and execute the code previously placed in SDRAM through an
    /// EXECUTE_SDRAM (0x19) transaction.
    /// <para>通过 EXECUTE_SDRAM（0x19）事务让设备跳转并执行先前放入 SDRAM 的代码。</para>
    /// </summary>
    /// <remarks>
    /// The official tool sends this command with the vendor's fixed 0xAA sub code. The device normally
    /// re-enumerates as a new USB device instead of answering a CSW, so the transaction failure of the
    /// final CSW read is tolerated the way <c>CRKDevice::ResetDevice</c> tolerates its shutdown resets.
    /// <para>官方工具发送该命令时携带厂商固定的 0xAA 子码。设备通常会作为新的 USB 设备重新枚举而不回 CSW，
    /// 因此最终 CSW 读取的事务失败按 <c>CRKDevice::ResetDevice</c> 容忍其关机复位的方式容忍。</para>
    /// </remarks>
    /// <exception cref="RkdlException">The device rejected the command with a CSW status other than the
    /// accepted re-enumeration failures. <para>设备以非重枚举失败的 CSW 状态拒绝该命令。</para></exception>
    public void ExecuteSdram()
    {
        ThrowIfDisposed();
        var cbw = new RkdlCbw(RkdlCommand.ExecuteSdram, RkdlFrame.DirectionOut, 0,
            RkdlFrame.CbwcbLengthLong, subCode: RkdlConstants.SdramVendorSubCode);
        WriteCbw(cbw);
        try
        {
            var csw = ReadCsw(cbw.Tag);
            if (!csw.IsSuccess)
            {
                throw new RkdlException(RkdlError.TransferFailed,
                    $"EXECUTE_SDRAM failed, CSW status = {csw.Status}.");
            }
        }
        catch (RkdlException ex) when (ex.ErrorCode is RkdlError.TransferFailed or RkdlError.ReadFailed)
        {
            CommTrace.State("rkdl",
                $"EXECUTE_SDRAM CSW read failed ({ex.Message}); the device most likely re-enumerated to run the SDRAM image.");
        }
    }

    /// <summary>
    /// Sets the device's reset flag through a SET_RESET_FLAG (0x1E) transaction, which changes the boot
    /// stage the device re-enters on its next reset.
    /// <para>通过 SET_RESET_FLAG（0x1E）事务设置设备的复位标志，改变设备下次复位后重新进入的启动阶段。</para>
    /// </summary>
    /// <param name="flag">The vendor-defined flag value stored in the command block's reserved byte.
    /// <para>存入命令块保留字节的厂商自定义标志值。</para></param>
    /// <exception cref="RkdlException">The transaction failed. <para>事务失败。</para></exception>
    public void SetResetFlag(byte flag)
    {
        ThrowIfDisposed();
        var cbw = new RkdlCbw(RkdlCommand.SetResetFlag, RkdlFrame.DirectionOut, 0,
            RkdlFrame.CbwcbLengthShort, subCode: flag);
        WriteCbw(cbw);
        var csw = ReadCsw(cbw.Tag);
        if (!csw.IsSuccess)
        {
            throw new RkdlException(RkdlError.WriteFailed, $"SET_RESET_FLAG failed, CSW status = {csw.Status}.");
        }
    }

    /// <summary>
    /// Writes one efuse region through a WRITE_EFUSE (0x1F) CBW/CSW transaction.
    /// <para>通过 WRITE_EFUSE（0x1F）CBW/CSW 事务写入一个 efuse 区域。</para>
    /// </summary>
    /// <remarks>
    /// Efuse writes are irreversible at the hardware level: a burned bit never clears. The caller
    /// supplies the vendor-defined payload layout; the wire transfer is one command's worth of bytes.
    /// <para>efuse 写入在硬件层面不可逆：烧录的位永远不会清除。负载布局由厂商定义、由调用方提供；
    /// 线上传输为一次一条命令容量的字节。</para>
    /// </remarks>
    /// <param name="data">The vendor-defined efuse payload, at most one command's worth.
    /// <para>厂商定义的 efuse 负载；不超过一条命令的容量。</para></param>
    /// <exception cref="RkdlException">The payload is empty or longer than 64 KB, or the transaction failed.
    /// <para>负载为空或超过 64 KB，或事务失败。</para></exception>
    public void WriteEfuse(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        if (data.Length is <= 0 or > RkdlConstants.TransferChunkSize)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"WRITE_EFUSE payload must be between 1 and {RkdlConstants.TransferChunkSize} bytes, but is {data.Length} bytes.");
        }

        var payload = data.ToArray();
        var cbw = new RkdlCbw(RkdlCommand.WriteEfuse, RkdlFrame.DirectionOut, (uint)payload.Length,
            RkdlFrame.CbwcbLengthLong, sectorLength: (ushort)payload.Length);
        WriteCbw(cbw);
        var written = Transport.Write(payload, 0, payload.Length, _timeoutMs);
        if (written != payload.Length)
        {
            throw new RkdlException(RkdlError.TransferFailed,
                $"WRITE_EFUSE payload short write: {written}/{payload.Length}.");
        }

        var csw = ReadCsw(cbw.Tag);
        if (!csw.IsSuccess)
        {
            throw new RkdlException(RkdlError.WriteFailed, $"WRITE_EFUSE failed, CSW status = {csw.Status}.");
        }
    }

    /// <summary>
    /// Reads efuse bytes through a READ_EFUSE (0x20) CBW/CSW transaction.
    /// <para>通过 READ_EFUSE（0x20）CBW/CSW 事务读取 efuse 字节。</para>
    /// </summary>
    /// <param name="byteCount">The number of bytes to read, at most one command's worth.
    /// <para>要读取的字节数；不超过一条命令的容量。</para></param>
    /// <returns>The efuse bytes the device returned. <para>设备返回的 efuse 字节。</para></returns>
    /// <exception cref="RkdlException"><paramref name="byteCount"/> is outside 1–64 KB or the transaction failed.
    /// <para><paramref name="byteCount"/> 不在 1–64 KB 之间或事务失败。</para></exception>
    public byte[] ReadEfuse(int byteCount)
    {
        ThrowIfDisposed();
        if (byteCount is <= 0 or > RkdlConstants.TransferChunkSize)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"READ_EFUSE transfer must be between 1 and {RkdlConstants.TransferChunkSize} bytes, but is {byteCount} bytes.");
        }

        return ReadDataCommand(RkdlCommand.ReadEfuse, (uint)byteCount, RkdlFrame.CbwcbLengthShort, byteCount, byteCount);
    }

    /// <summary>
    /// Reads SPI flash bytes through a READ_SPI_FLASH (0x21) CBW/CSW transaction.
    /// <para>通过 READ_SPI_FLASH（0x21）CBW/CSW 事务读取 SPI Flash 字节。</para>
    /// </summary>
    /// <param name="byteAddress">The byte address inside the SPI flash. <para>SPI Flash 内的字节地址。</para></param>
    /// <param name="byteCount">The number of bytes to read, at most one command's worth.
    /// <para>要读取的字节数；不超过一条命令的容量。</para></param>
    /// <returns>The SPI flash bytes. <para>SPI Flash 字节。</para></returns>
    /// <exception cref="RkdlException"><paramref name="byteCount"/> is outside 1–64 KB or the transaction failed.
    /// <para><paramref name="byteCount"/> 不在 1–64 KB 之间或事务失败。</para></exception>
    public byte[] ReadSpiFlash(uint byteAddress, int byteCount)
    {
        ThrowIfDisposed();
        if (byteCount is <= 0 or > RkdlConstants.TransferChunkSize)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"READ_SPI_FLASH transfer must be between 1 and {RkdlConstants.TransferChunkSize} bytes, but is {byteCount} bytes.");
        }

        var cbw = new RkdlCbw(RkdlCommand.ReadSpiFlash, RkdlFrame.DirectionIn, (uint)byteCount,
            RkdlFrame.CbwcbLengthLong, address: byteAddress, sectorLength: (ushort)byteCount);
        WriteCbw(cbw);
        var buffer = new byte[byteCount];
        var n = 0;
        while (n < byteCount)
        {
            var chunk = Transport.Read(buffer, n, byteCount - n, _timeoutMs);
            if (chunk <= 0)
            {
                break;
            }

            n += chunk;
        }

        var csw = ReadCsw(cbw.Tag);
        if (!csw.IsSuccess)
        {
            throw new RkdlException(RkdlError.ReadFailed,
                $"READ_SPI_FLASH failed at 0x{byteAddress:X}, CSW status = {csw.Status}.");
        }

        if (n < byteCount)
        {
            throw new RkdlException(RkdlError.ReadFailed, $"Short SPI flash read: {n} < {byteCount}.");
        }

        return buffer;
    }

    /// <summary>
    /// Writes SPI flash bytes through a WRITE_SPI_FLASH (0x22) CBW/CSW transaction.
    /// <para>通过 WRITE_SPI_FLASH（0x22）CBW/CSW 事务写入 SPI Flash 字节。</para>
    /// </summary>
    /// <param name="byteAddress">The byte address inside the SPI flash. <para>SPI Flash 内的字节地址。</para></param>
    /// <param name="data">The bytes to write, at most one command's worth.
    /// <para>要写入的字节；不超过一条命令的容量。</para></param>
    /// <exception cref="RkdlException">The payload is empty or longer than 64 KB, or the transaction failed.
    /// <para>负载为空或超过 64 KB，或事务失败。</para></exception>
    public void WriteSpiFlash(uint byteAddress, ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        if (data.Length is <= 0 or > RkdlConstants.TransferChunkSize)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"WRITE_SPI_FLASH payload must be between 1 and {RkdlConstants.TransferChunkSize} bytes, but is {data.Length} bytes.");
        }

        var payload = data.ToArray();
        var cbw = new RkdlCbw(RkdlCommand.WriteSpiFlash, RkdlFrame.DirectionOut, (uint)payload.Length,
            RkdlFrame.CbwcbLengthLong, address: byteAddress, sectorLength: (ushort)payload.Length);
        WriteCbw(cbw);
        var written = Transport.Write(payload, 0, payload.Length, _timeoutMs);
        if (written != payload.Length)
        {
            throw new RkdlException(RkdlError.TransferFailed,
                $"WRITE_SPI_FLASH payload short write: {written}/{payload.Length}.");
        }

        var csw = ReadCsw(cbw.Tag);
        if (!csw.IsSuccess)
        {
            throw new RkdlException(RkdlError.WriteFailed,
                $"WRITE_SPI_FLASH failed at 0x{byteAddress:X}, CSW status = {csw.Status}.");
        }
    }

    /// <summary>
    /// Writes one efuse region through a WRITE_NEW_EFUSE (0x23) CBW/CSW transaction, which addresses
    /// the efuse by index instead of the flat byte layout of <see cref="WriteEfuse"/>.
    /// <para>通过 WRITE_NEW_EFUSE（0x23）CBW/CSW 事务写入一个 efuse 区域；与 <see cref="WriteEfuse"/>
    /// 的平铺字节布局不同，该命令按索引寻址 efuse。</para>
    /// </summary>
    /// <param name="efuseIndex">The vendor-defined efuse index. <para>厂商定义的 efuse 索引。</para></param>
    /// <param name="data">The vendor-defined payload, at most one command's worth.
    /// <para>厂商定义的负载；不超过一条命令的容量。</para></param>
    /// <param name="subCode">The vendor-defined sub code placed in the command block's reserved byte.
    /// <para>放入命令块保留字节的厂商自定义子码。</para></param>
    /// <exception cref="RkdlException">The payload is empty or longer than 64 KB, or the transaction failed.
    /// <para>负载为空或超过 64 KB，或事务失败。</para></exception>
    public void WriteNewEfuse(ushort efuseIndex, ReadOnlySpan<byte> data, byte subCode = 0)
    {
        ThrowIfDisposed();
        if (data.Length is <= 0 or > RkdlConstants.TransferChunkSize)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"WRITE_NEW_EFUSE payload must be between 1 and {RkdlConstants.TransferChunkSize} bytes, but is {data.Length} bytes.");
        }

        var payload = data.ToArray();
        var cbw = new RkdlCbw(RkdlCommand.WriteNewEfuse, RkdlFrame.DirectionOut, (uint)payload.Length,
            RkdlFrame.CbwcbLengthLong, subCode: subCode, address: efuseIndex, sectorLength: (ushort)payload.Length);
        WriteCbw(cbw);
        var written = Transport.Write(payload, 0, payload.Length, _timeoutMs);
        if (written != payload.Length)
        {
            throw new RkdlException(RkdlError.TransferFailed,
                $"WRITE_NEW_EFUSE payload short write: {written}/{payload.Length}.");
        }

        var csw = ReadCsw(cbw.Tag);
        if (!csw.IsSuccess)
        {
            throw new RkdlException(RkdlError.WriteFailed,
                $"WRITE_NEW_EFUSE failed at index {efuseIndex}, CSW status = {csw.Status}.");
        }
    }

    /// <summary>
    /// Reads efuse bytes through a READ_NEW_EFUSE (0x24) CBW/CSW transaction, which addresses the
    /// efuse by index instead of the flat byte layout of <see cref="ReadEfuse"/>.
    /// <para>通过 READ_NEW_EFUSE（0x24）CBW/CSW 事务读取 efuse 字节；与 <see cref="ReadEfuse"/> 的
    /// 平铺字节布局不同，该命令按索引寻址 efuse。</para>
    /// </summary>
    /// <param name="efuseIndex">The vendor-defined efuse index. <para>厂商定义的 efuse 索引。</para></param>
    /// <param name="byteCount">The number of bytes to read, at most one command's worth.
    /// <para>要读取的字节数；不超过一条命令的容量。</para></param>
    /// <param name="subCode">The vendor-defined sub code placed in the command block's reserved byte.
    /// <para>放入命令块保留字节的厂商自定义子码。</para></param>
    /// <returns>The efuse bytes the device returned. <para>设备返回的 efuse 字节。</para></returns>
    /// <exception cref="RkdlException"><paramref name="byteCount"/> is outside 1–64 KB or the transaction failed.
    /// <para><paramref name="byteCount"/> 不在 1–64 KB 之间或事务失败。</para></exception>
    public byte[] ReadNewEfuse(ushort efuseIndex, int byteCount, byte subCode = 0)
    {
        ThrowIfDisposed();
        if (byteCount is <= 0 or > RkdlConstants.TransferChunkSize)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"READ_NEW_EFUSE transfer must be between 1 and {RkdlConstants.TransferChunkSize} bytes, but is {byteCount} bytes.");
        }

        var cbw = new RkdlCbw(RkdlCommand.ReadNewEfuse, RkdlFrame.DirectionIn, (uint)byteCount,
            RkdlFrame.CbwcbLengthLong, subCode: subCode, address: efuseIndex, sectorLength: (ushort)byteCount);
        WriteCbw(cbw);
        var buffer = new byte[byteCount];
        var n = 0;
        while (n < byteCount)
        {
            var chunk = Transport.Read(buffer, n, byteCount - n, _timeoutMs);
            if (chunk <= 0)
            {
                break;
            }

            n += chunk;
        }

        var csw = ReadCsw(cbw.Tag);
        if (!csw.IsSuccess)
        {
            throw new RkdlException(RkdlError.ReadFailed,
                $"READ_NEW_EFUSE failed at index {efuseIndex}, CSW status = {csw.Status}.");
        }

        if (n < byteCount)
        {
            throw new RkdlException(RkdlError.ReadFailed, $"Short efuse read: {n} < {byteCount}.");
        }

        return buffer;
    }

    /// <summary>
    /// Tests a NAND block range for bad blocks through a TEST_BAD_BLOCK (0x03) CBW/CSW transaction,
    /// reading back the vendor-defined 64-byte bad-block bitmap.
    /// <para>通过 TEST_BAD_BLOCK（0x03）CBW/CSW 事务测试 NAND 块范围中的坏块，读回厂商定义的 64 字节坏块位图。</para>
    /// </summary>
    /// <remarks>
    /// The official tool caps the tested range at 512 blocks per command and selects the flash chip
    /// through the CBW LUN byte; the 64-byte payload is the vendor bad-block bitmap and a CSW status of
    /// 1 is a plain failure, not a bad-block indicator. An empty range is rejected here because a
    /// zero-block test cannot report anything.
    /// <para>官方工具把每条命令的测试范围限制在 512 块以内，并通过 CBW LUN 字节选择 flash 芯片；64 字节负载
    /// 是厂商的坏块位图，CSW 状态为 1 是普通失败而不是坏块标记。这里拒绝空范围，因为零块的测试没有可报告的
    /// 内容。</para>
    /// </remarks>
    /// <param name="flashChip">The flash chip number, 0–3. <para>flash 芯片编号，0–3。</para></param>
    /// <param name="blockAddress">The first block to test. <para>要测试的首个块。</para></param>
    /// <param name="blockCount">The number of blocks to test, 1–512. <para>要测试的块数，1–512。</para></param>
    /// <returns>The raw 64-byte result the device returned. <para>设备返回的原始 64 字节结果。</para></returns>
    /// <exception cref="RkdlException">An argument is outside the official bounds or the transaction failed.
    /// <para>参数超出官方边界或事务失败。</para></exception>
    public byte[] TestBadBlock(byte flashChip, uint blockAddress, ushort blockCount)
    {
        ThrowIfDisposed();
        if (flashChip > 3)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"TEST_BAD_BLOCK flash chip {flashChip} exceeds the official 0-3 range.");
        }

        if (blockCount is 0 or > RkdlConstants.MaxTestBlocksPerCommand)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"TEST_BAD_BLOCK must test between 1 and {RkdlConstants.MaxTestBlocksPerCommand} blocks, but got {blockCount}.");
        }

        var cbw = new RkdlCbw(RkdlCommand.TestBadBlock, RkdlFrame.DirectionIn,
            RkdlConstants.TestBadBlockResultLength, RkdlFrame.CbwcbLengthLong,
            address: blockAddress, sectorLength: blockCount, flashCs: flashChip);
        WriteCbw(cbw);
        var buffer = new byte[RkdlConstants.TestBadBlockResultLength];
        var n = 0;
        while (n < buffer.Length)
        {
            var chunk = Transport.Read(buffer, n, buffer.Length - n, _timeoutMs);
            if (chunk <= 0)
            {
                break;
            }

            n += chunk;
        }

        var csw = ReadCsw(cbw.Tag);
        if (!csw.IsSuccess)
        {
            throw new RkdlException(RkdlError.ReadFailed,
                $"TEST_BAD_BLOCK failed at block {blockAddress}, CSW status = {csw.Status}.");
        }

        if (n < buffer.Length)
        {
            throw new RkdlException(RkdlError.ReadFailed, $"Short bad-block result read: {n} < {buffer.Length}.");
        }

        return buffer;
    }

    /// <summary>
    /// Reads raw 528-byte NAND sectors (512 data bytes plus 16 spare bytes each) through a
    /// READ_SECTOR (0x04) CBW/CSW transaction.
    /// <para>通过 READ_SECTOR（0x04）CBW/CSW 事务读取原始 528 字节 NAND 扇区（每个 512 数据字节加 16 备用字节）。</para>
    /// </summary>
    /// <remarks>
    /// READ_SECTOR addresses raw NAND sectors rather than LBAs, so the spare area is part of the
    /// payload. One command carries at most 20 sectors, the official transfer bound; an empty range is
    /// rejected here because a zero-sector read cannot return anything.
    /// <para>READ_SECTOR 寻址的是原始 NAND 扇区而非 LBA，因此备用区属于负载。一条命令最多携带 20 个扇区，
    /// 即官方的传输上限；这里拒绝空范围，因为零扇区的读取没有可返回的内容。</para>
    /// </remarks>
    /// <param name="sectorPosition">The first 528-byte sector to read. <para>要读取的首个 528 字节扇区。</para></param>
    /// <param name="sectorCount">The number of sectors to read, 1–20. <para>要读取的扇区数，1–20。</para></param>
    /// <returns>The sector payload, <paramref name="sectorCount"/> × 528 bytes.
    /// <para>扇区负载，<paramref name="sectorCount"/> × 528 字节。</para></returns>
    /// <exception cref="RkdlException"><paramref name="sectorCount"/> is outside 1–20 or the transaction failed.
    /// <para><paramref name="sectorCount"/> 不在 1–20 之间或事务失败。</para></exception>
    public byte[] ReadSector(uint sectorPosition, int sectorCount)
    {
        ThrowIfDisposed();
        if (sectorCount is <= 0 or > RkdlConstants.MaxReadSectorsPerCommand)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"READ_SECTOR must read between 1 and {RkdlConstants.MaxReadSectorsPerCommand} sectors, but got {sectorCount}.");
        }

        var transferLength = sectorCount * RkdlConstants.SectorWithSpareSize;
        var cbw = new RkdlCbw(RkdlCommand.ReadSector, RkdlFrame.DirectionIn, (uint)transferLength,
            RkdlFrame.CbwcbLengthLong, address: sectorPosition, sectorLength: (ushort)sectorCount);
        WriteCbw(cbw);
        var buffer = new byte[transferLength];
        var n = 0;
        while (n < transferLength)
        {
            var chunk = Transport.Read(buffer, n, transferLength - n, _timeoutMs);
            if (chunk <= 0)
            {
                break;
            }

            n += chunk;
        }

        var csw = ReadCsw(cbw.Tag);
        if (!csw.IsSuccess)
        {
            throw new RkdlException(RkdlError.ReadFailed,
                $"READ_SECTOR failed at sector {sectorPosition}, CSW status = {csw.Status}.");
        }

        if (n < transferLength)
        {
            throw new RkdlException(RkdlError.ReadFailed, $"Short sector read: {n} < {transferLength}.");
        }

        return buffer;
    }
}