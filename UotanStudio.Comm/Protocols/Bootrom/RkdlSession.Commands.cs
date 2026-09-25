using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Bootrom;

public sealed partial class RkdlSession
{
    /// <summary>
    /// Reads chip info from the device via a READ_CHIP_INFO CBW/CSW transaction (16 bytes).
    /// The first four bytes of the response are an ASCII chip tag (e.g. "320A"); it is
    /// exposed both raw and decoded (see upstream CRKDevice::CheckChip).
    /// <para>通过 READ_CHIP_INFO CBW/CSW 事务从设备读取芯片信息（16 字节）。响应的前 4 字节是
    /// ASCII 芯片标签（如 "320A"）；同时以原始值与解码字符串暴露（见上游 CRKDevice::CheckChip）。</para>
    /// </summary>
    internal RkChipInfo ReadChipInfo()
    {
        var data = ReadDataCommand(RkdlCommand.ReadChipInfo, 16, RkdlFrame.CbwcbLengthShort, 16, 16);
        return new RkChipInfo
        {
            ChipId = BinaryPrimitives.ReadUInt32LittleEndian(data),
            Tag = System.Text.Encoding.ASCII.GetString(data, 0, 4),
            // Upstream RKU_ReadChipInfo only forwards the 16 bytes and CRKDevice decodes the
            // chip tag; the revision interpretation at offset 4 has no upstream definition.
            Revision = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4))
        };
    }

    /// <summary>
    /// Reads flash info from the device via a READ_FLASH_INFO CBW/CSW transaction.
    /// The device responds with a packed <c>STRUCT_FLASHINFO_CMD</c> (up to 512 bytes);
    /// the leading DWORD is the total sector count.
    /// <para>通过 READ_FLASH_INFO CBW/CSW 事务从设备读取闪存信息。设备以紧凑的
    /// <c>STRUCT_FLASHINFO_CMD</c>（最多 512 字节）响应；首 DWORD 为总扇区数。</para>
    /// </summary>
    internal RkFlashInfo ReadFlashInfo()
    {
        var data = ReadDataCommand(RkdlCommand.ReadFlashInfo, 11, RkdlFrame.CbwcbLengthShort, 512, 11);
        var sectorCount = BinaryPrimitives.ReadUInt32LittleEndian(data);
        return new RkFlashInfo
        {
            Size = sectorCount * (long)RkdlConstants.SectorSize,
            SectorSize = RkdlConstants.SectorSize
        };
    }

    /// <summary>
    /// Reads the flash ID via a READ_FLASH_ID CBW/CSW transaction (5 bytes).
    /// <para>通过 READ_FLASH_ID CBW/CSW 事务读取闪存 ID（5 字节）。</para>
    /// </summary>
    public byte[] ReadFlashId()
        => ReadDataCommand(RkdlCommand.ReadFlashId, 5, RkdlFrame.CbwcbLengthShort, 5, 5);

    /// <summary>
    /// Reads the device capability bits via a READ_CAPABILITY CBW/CSW transaction (8 bytes,
    /// matching rkdeveloptool RKU_ReadCapability with dwCBWTransferLength = 8).
    /// <para>通过 READ_CAPABILITY CBW/CSW 事务读取设备能力位（8 字节，对应 rkdeveloptool
    /// RKU_ReadCapability 的 dwCBWTransferLength = 8）。</para>
    /// </summary>
    public byte[] ReadCapability()
        => ReadDataCommand(RkdlCommand.ReadCapability, 8, RkdlFrame.CbwcbLengthShort, 8, 8);

    /// <summary>
    /// Resets the device via a DEVICE_RESET (0xFF) CBW/CSW transaction.
    /// <para>通过 DEVICE_RESET（0xFF）CBW/CSW 事务复位设备。</para>
    /// </summary>
    /// <param name="subCode">A <see cref="RkdlResetSubCode"/> value (default none). <para><see cref="RkdlResetSubCode"/> 子码（默认无）。</para></param>
    /// <returns>0 when the reset command completed. <para>复位命令完成时返回 0。</para></returns>
    public int ResetDevice(byte subCode = RkdlResetSubCode.None)
    {
        ThrowIfDisposed();
        var cbw = new RkdlCbw(RkdlCommand.DeviceReset, RkdlFrame.DirectionOut, 0,
            RkdlFrame.CbwcbLengthShort, subCode: subCode);
        WriteCbw(cbw);
        var csw = ReadCsw(cbw.Tag, recovery: true);
        return !csw.IsSuccess ? throw new RkdlException(RkdlError.DeviceReset, $"DEVICE_RESET failed, CSW status = {csw.Status}.") : 0;
    }

    /// <summary>
    /// Reboots the device back into mask ROM mode (DEVICE_RESET with the
    /// <see cref="RkdlResetSubCode.ResetMaskRom"/> sub code).
    /// <para>将设备重新启动回 Mask ROM 模式（DEVICE_RESET，子码
    /// <see cref="RkdlResetSubCode.ResetMaskRom"/>）。</para>
    /// </summary>
    public void RebootMaskRom()
        => ResetDevice(RkdlResetSubCode.ResetMaskRom);

    /// <summary>
    /// Powers the device off (DEVICE_RESET with the <see cref="RkdlResetSubCode.PowerOff"/>
    /// sub code, matching rkdeveloptool PowerOffDevice).
    /// <para>关闭设备电源（DEVICE_RESET，子码 <see cref="RkdlResetSubCode.PowerOff"/>，
    /// 对应 rkdeveloptool 的 PowerOffDevice）。</para>
    /// </summary>
    public void Shutdown()
        => ResetDevice(RkdlResetSubCode.PowerOff);

    /// <summary>
    /// Erases flash blocks via ERASE_NORMAL (or ERASE_FORCE) CBW/CSW transactions, chunked at
    /// <see cref="RkdlConstants.MaxEraseBlocksPerCommand"/> blocks per command
    /// (rkdeveloptool CRKComm::EraseBlocks / MAX_ERASE_BLOCKS).
    /// <para>通过 ERASE_NORMAL（或 ERASE_FORCE）CBW/CSW 事务擦除 flash 块，每条命令最多
    /// <see cref="RkdlConstants.MaxEraseBlocksPerCommand"/> 个块（rkdeveloptool 的
    /// CRKComm::EraseBlocks / MAX_ERASE_BLOCKS）。</para>
    /// </summary>
    /// <param name="blockPos">The first block to erase. <para>起始块号。</para></param>
    /// <param name="blockCount">The number of blocks to erase. <para>要擦除的块数。</para></param>
    /// <param name="force"><c>true</c> to use ERASE_FORCE (ignores bad-block markers). <para>是否使用 ERASE_FORCE（忽略坏块标记）。</para></param>
    public void EraseBlocks(uint blockPos, uint blockCount, bool force = false)
    {
        ThrowIfDisposed();
        var opcode = force ? (RkdlCommand)0x0B : RkdlCommand.EraseNormal;
        while (blockCount > 0)
        {
            var chunk = (ushort)Math.Min(blockCount, RkdlConstants.MaxEraseBlocksPerCommand);
            var cbw = new RkdlCbw(opcode, RkdlFrame.DirectionOut, 0,
                RkdlFrame.CbwcbLengthLong, address: blockPos, sectorLength: chunk);
            WriteCbw(cbw);
            var csw = ReadCsw(cbw.Tag);
            if (!csw.IsSuccess)
            {
                throw new RkdlException(RkdlError.EraseFailed, $"Erase failed at block {blockPos}, CSW status = {csw.Status}.");
            }

            blockPos += chunk;
            blockCount -= chunk;
        }
    }

    /// <summary>
    /// Erases the entire flash via an ERASE_SYSTEMDISK (0x16) CBW/CSW transaction.
    /// <para>通过 ERASE_SYSTEMDISK（0x16）CBW/CSW 事务擦除整个 flash。</para>
    /// </summary>
    public int EraseFlash()
    {
        ThrowIfDisposed();
        var cbw = new RkdlCbw(RkdlCommand.EraseSystemDisk, RkdlFrame.DirectionOut, 0,
            RkdlFrame.CbwcbLengthShort);
        WriteCbw(cbw);
        var csw = ReadCsw(cbw.Tag);
        return !csw.IsSuccess ? throw new RkdlException(RkdlError.EraseFailed, $"ERASE_SYSTEMDISK failed, CSW status = {csw.Status}.") : 0;
    }

    /// <summary>
    /// Erases <paramref name="sectorCount"/> 512-byte sectors starting at the given LBA via an
    /// ERASE_LBA (0x25) CBW/CSW transaction.
    /// <para>通过 ERASE_LBA（0x25）CBW/CSW 事务，从指定 LBA 开始擦除
    /// <paramref name="sectorCount"/> 个 512 字节扇区。</para>
    /// </summary>
    /// <param name="address">The start LBA. <para>起始 LBA。</para></param>
    /// <param name="sectorCount">The number of sectors to erase. <para>要擦除的扇区数。</para></param>
    public void EraseLBA(long address, int sectorCount)
    {
        ThrowIfDisposed();
        if (address is < 0 or > uint.MaxValue)
        {
            throw new RkdlException(RkdlError.InvalidParameter, $"LBA address out of range: {address}.");
        }

        if (sectorCount is < 0 or > ushort.MaxValue)
        {
            throw new RkdlException(RkdlError.InvalidParameter, $"Sector count out of range: {sectorCount}.");
        }

        var cbw = new RkdlCbw(RkdlCommand.EraseLba, RkdlFrame.DirectionOut, 0,
            RkdlFrame.CbwcbLengthLong, address: (uint)address, sectorLength: (ushort)sectorCount);
        WriteCbw(cbw);
        var csw = ReadCsw(cbw.Tag);
        if (!csw.IsSuccess)
        {
            throw new RkdlException(RkdlError.EraseFailed, $"ERASE_LBA failed at LBA {address}, CSW status = {csw.Status}.");
        }
    }

    /// <summary>
    /// Tests whether the device is ready via a TEST_UNIT_READY (0x00) CBW/CSW transaction,
    /// decoding the loader-boot progress from the CSW data residue (total in the low 16 bits,
    /// current in the high 16 bits, both big-endian — see rkdeveloptool RKU_TestDeviceReady).
    /// <para>通过 TEST_UNIT_READY（0x00）CBW/CSW 事务检测设备是否就绪，并从 CSW 数据余量解码
    /// loader 启动进度（总进度在低 16 位、当前进度在高 16 位，均按大端序——见 rkdeveloptool
    /// 的 RKU_TestDeviceReady）。</para>
    /// </summary>
    /// <param name="progressCurrent">Receives the current progress value. <para>接收当前进度值。</para></param>
    /// <param name="progressTotal">Receives the total progress value. <para>接收总进度值。</para></param>
    /// <returns><c>true</c> when the device is ready. <para>设备就绪时返回 <c>true</c>。</para></returns>
    public bool TestDeviceReady(out ushort progressCurrent, out ushort progressTotal)
    {
        ThrowIfDisposed();
        var cbw = new RkdlCbw(RkdlCommand.TestUnitReady, RkdlFrame.DirectionIn, 0,
            RkdlFrame.CbwcbLengthShort);
        WriteCbw(cbw);
        var csw = ReadCsw(cbw.Tag, recovery: true);
        // Residue halves are big-endian on the wire while the CSW field itself is read little-endian.
        var residue = csw.DataResidue;
        progressTotal = BinaryPrimitives.ReverseEndianness((ushort)(residue & 0xFFFF));
        progressCurrent = BinaryPrimitives.ReverseEndianness((ushort)(residue >> 16));
        return csw.IsSuccess;
    }

    /// <summary>
    /// Tests whether the device is ready via a TEST_UNIT_READY (0x00) CBW/CSW transaction.
    /// <para>通过 TEST_UNIT_READY（0x00）CBW/CSW 事务检测设备是否就绪。</para>
    /// </summary>
    /// <returns><c>true</c> when the device is ready. <para>设备就绪时返回 <c>true</c>。</para></returns>
    public bool TestDeviceReady() => TestDeviceReady(out _, out _);

    /// <summary>
    /// Polls TEST_UNIT_READY until the loader reports ready or the timeout elapses, reporting
    /// boot progress (current/total) along the way (rkdeveloptool CRKComm::TestDeviceReady loop).
    /// <para>轮询 TEST_UNIT_READY 直到 loader 报告就绪或超时，期间上报启动进度
    /// （当前/总进度）（rkdeveloptool 的 CRKComm::TestDeviceReady 循环）。</para>
    /// </summary>
    /// <param name="timeoutMs">Total wait budget in milliseconds. <para>总等待预算（毫秒）。</para></param>
    /// <param name="progress">Optional progress receiver. <para>可选进度接收器。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns><c>true</c> when the device became ready. <para>设备就绪返回 <c>true</c>。</para></returns>
    public async Task<bool> WaitForLoaderReadyAsync(int timeoutMs = 30000, IProgress<(ushort Current, ushort Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ushort current = 0, total = 0;
            var ready = await Task.Run(() => TestDeviceReady(out current, out total), cancellationToken).ConfigureAwait(false);
            if (ready)
            {
                return true;
            }

            if (total > 0)
            {
                progress?.Report((current, total));
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// Tests whether the device is ready via a TEST_UNIT_READY (0x00) CBW/CSW transaction,
    /// asynchronously.
    /// <para>异步地通过 TEST_UNIT_READY（0x00）CBW/CSW 事务检测设备是否就绪。</para>
    /// </summary>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns><c>true</c> when the device is ready. <para>设备就绪时返回 <c>true</c>。</para></returns>
    public async Task<bool> TestDeviceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var cbw = new RkdlCbw(RkdlCommand.TestUnitReady, RkdlFrame.DirectionIn, 0,
            RkdlFrame.CbwcbLengthShort);
        var cbwBytes = cbw.ToBytes();
        var written = await Transport.WriteAsync(cbwBytes, 0, cbwBytes.Length, _timeoutMs, cancellationToken).ConfigureAwait(false);
        if (written != cbwBytes.Length)
        {
            return false;
        }

        var cswBuffer = new byte[RkdlFrame.CswLength];
        var read = await Transport.ReadAsync(cswBuffer, 0, cswBuffer.Length, _timeoutMs, cancellationToken).ConfigureAwait(false);
        return read == cswBuffer.Length && RkdlCsw.TryParse(cswBuffer, cbw.Tag, out var csw) && csw!.IsSuccess;
    }

}