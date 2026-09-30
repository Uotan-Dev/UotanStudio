using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Bootrom;

public sealed partial class RkdlSession
{
    private RkFlashInfo? _flashInfo;

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
            Revision = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4))
        };
    }

    /// <summary>
    /// Reads flash info from the device via a READ_FLASH_INFO CBW/CSW transaction.
    /// The device responds with a packed <c>STRUCT_FLASHINFO_CMD</c> (up to 512 bytes);
    /// the leading DWORD is the total sector count.
    /// <para>通过 READ_FLASH_INFO CBW/CSW 事务从设备读取闪存信息。设备以紧凑的
    /// <c>STRUCT_FLASHINFO_CMD</c>（最多 512 字节）响应；首 DWORD 为总扇区数。</para>
    /// Only that leading DWORD is decoded. The offsets of the remaining fields of the upstream
    /// structure (media block size, page size, ...) cannot be verified against a local copy of
    /// <c>RKComm.h</c>, so the whole response is preserved verbatim in
    /// <see cref="RkFlashInfo.RawResponse"/> rather than being decoded from guessed offsets.
    /// <see cref="RkFlashInfo.SectorSize"/> is therefore the protocol LBA granularity
    /// <see cref="RkdlConstants.SectorSize"/>, never a device-reported value.
    /// <para>只解码首个 DWORD。上游结构其余字段（介质块大小、页大小等）的偏移无法用本地
    /// <c>RKComm.h</c> 副本核对，因此整个响应原样保留在 <see cref="RkFlashInfo.RawResponse"/> 中，
    /// 而不按猜测的偏移解码。所以 <see cref="RkFlashInfo.SectorSize"/> 是协议 LBA 粒度
    /// <see cref="RkdlConstants.SectorSize"/>，绝不是设备报告值。</para>
    /// </summary>
    /// <returns>The decoded sector count together with the verbatim response bytes.
    /// <para>解码出的扇区数与逐字节的原始响应。</para></returns>
    /// <exception cref="RkdlException">The data phase was shorter than the 11 bytes requested, or
    /// the CSW reported failure.
    /// <para>数据阶段短于请求的 11 字节，或 CSW 报告失败。</para></exception>
    internal RkFlashInfo ReadFlashInfo()
    {
        var data = ReadDataCommand(RkdlCommand.ReadFlashInfo, 11, RkdlFrame.CbwcbLengthShort, 512, 11);
        return new RkFlashInfo
        {
            SectorCount = BinaryPrimitives.ReadUInt32LittleEndian(data),
            RawResponse = data
        };
    }

    /// <summary>
    /// Returns the cached device flash geometry, querying it with a single READ_FLASH_INFO the first
    /// time it is needed so that a multi-chunk transfer or a GPT write does not re-read it per
    /// operation.
    /// <para>返回缓存的设备闪存几何；首次需要时用一条 READ_FLASH_INFO 查询，因此多分块传输或一次 GPT
    /// 写入不会每个操作都重读几何。</para>
    /// The cache is dropped by <see cref="InvalidateFlashGeometry"/>, which <see cref="ResetDevice"/>
    /// calls after the device acknowledged a reset, because a reset makes the device re-enumerate and
    /// the geometry it reports afterwards is a fresh observation.
    /// <para>缓存由 <see cref="InvalidateFlashGeometry"/> 丢弃；<see cref="ResetDevice"/> 会在设备确认
    /// 复位后调用它，因为复位会让设备重新枚举，之后报告的几何是一次全新观测。</para>
    /// </summary>
    /// <returns>The geometry the device reported. <para>设备报告的几何。</para></returns>
    /// <exception cref="RkdlException">The READ_FLASH_INFO transaction failed; the original error code
    /// is preserved and the message states that the geometry is unavailable, so callers fail closed
    /// instead of addressing flash against an unknown capacity.
    /// <para>READ_FLASH_INFO 事务失败；保留原始错误码，并在消息中说明几何不可用，使调用方拒绝操作而不是
    /// 在容量未知的情况下寻址闪存。</para></exception>
    internal RkFlashInfo RequireFlashGeometry()
    {
        if (_flashInfo is { } cached)
        {
            return cached;
        }

        RkFlashInfo geometry;
        try
        {
            geometry = ReadFlashInfo();
        }
        catch (RkdlException error)
        {
            throw new RkdlException(error.ErrorCode,
                $"The device flash geometry is unavailable because READ_FLASH_INFO failed, so addressing flash is refused: {error.Message}",
                error);
        }

        _flashInfo = geometry;
        return geometry;
    }

    /// <summary>
    /// Drops the cached flash geometry so the next addressing operation queries the device again.
    /// <para>丢弃缓存的闪存几何，使下一次寻址操作重新向设备查询。</para>
    /// Callers that make the device re-enumerate or switch its storage medium
    /// (<c>CHANGE_STORAGE</c>), and any code that adopts a reconnected transport into an existing
    /// session, must call this before addressing flash again.
    /// <para>会让设备重新枚举或切换存储介质（<c>CHANGE_STORAGE</c>）的调用方，以及把重连后的传输接管进
    /// 既有会话的任何代码，都必须在再次寻址闪存前调用本方法。</para>
    /// </summary>
    internal void InvalidateFlashGeometry() => _flashInfo = null;

    /// <summary>
    /// Rejects an addressing range that the device geometry cannot hold, so a partition offset or a
    /// parameter-table value that exceeds the media is refused before any command reaches the device.
    /// <para>拒绝设备几何无法容纳的寻址范围，使超出介质的分区偏移或 parameter 表取值在任何命令到达设备
    /// 之前就被拒绝。</para>
    /// Both arguments are expressed in the 32-bit <c>dwAddress</c> wire units the rockusb CBWCB
    /// carries, which <see cref="EraseLBA"/> documents as 512-byte sectors and which
    /// <see cref="EraseBlocks"/> fills from its block arguments verbatim. This repository carries no
    /// verified erase-block-to-sector ratio, so no unit conversion is applied here; see the
    /// unverified-boundary note on <see cref="EraseBlocks"/>.
    /// <para>两个参数都以 rockusb CBWCB 承载的 32 位 <c>dwAddress</c> 线上单位表示；
    /// <see cref="EraseLBA"/> 把它们记录为 512 字节扇区，<see cref="EraseBlocks"/> 则把自己的块参数
    /// 原样填入。本仓库没有可核对的擦除块与扇区比例，因此此处不做单位换算；见
    /// <see cref="EraseBlocks"/> 上的未验证边界说明。</para>
    /// </summary>
    /// <param name="address">The first addressed unit, as a signed value so a negative argument is
    /// rejected rather than cast. <para>首个被寻址的单位，用有符号值以便负参数被拒绝而非被转型。</para></param>
    /// <param name="count">The number of addressed units. <para>被寻址的单位数量。</para></param>
    /// <exception cref="RkdlException">An argument is negative or cannot be expressed as an unsigned
    /// 32-bit address, the device-reported geometry is unusable, or
    /// <paramref name="address"/> + <paramref name="count"/> exceeds the device sector count.
    /// <para>参数为负或无法表示为无符号 32 位地址、设备报告的几何不可用，或
    /// <paramref name="address"/> + <paramref name="count"/> 超过设备扇区数。</para></exception>
    private void ValidateLbaRange(long address, long count)
    {
        if (address < 0 || count < 0)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"Negative addressing range: address = {address}, count = {count}.");
        }

        if (address > uint.MaxValue || count > uint.MaxValue)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"Addressing range [{address}, +{count}) cannot be expressed as an unsigned 32-bit device address.");
        }

        var deviceSectors = RequireFlashGeometry().SectorCount;
        if (deviceSectors is <= 0 or > uint.MaxValue)
        {
            throw new RkdlException(RkdlError.FlashInfoFailed,
                $"READ_FLASH_INFO reported an unusable sector count ({deviceSectors}), so the device geometry is unavailable and addressing flash is refused.");
        }

        if (checked(address + count) > deviceSectors)
        {
            throw new RkdlException(RkdlError.InvalidParameter,
                $"Addressing range [{address}, {checked(address + count)}) exceeds the device geometry of {deviceSectors} sectors.");
        }
    }

    /// <summary>
    /// Reads the flash ID via a READ_FLASH_ID CBW/CSW transaction (5 bytes).
    /// <para>通过 READ_FLASH_ID CBW/CSW 事务读取闪存 ID（5 字节）。</para>
    /// </summary>
    /// <returns>The raw data-in bytes returned by the device (at least 5).
    /// <para>设备返回的原始数据输入字节（至少 5 个）。</para></returns>
    public byte[] ReadFlashId()
        => ReadDataCommand(RkdlCommand.ReadFlashId, 5, RkdlFrame.CbwcbLengthShort, 5, 5);

    /// <summary>
    /// Reads the device capability bits via a READ_CAPABILITY CBW/CSW transaction (8 bytes,
    /// matching rkdeveloptool RKU_ReadCapability with dwCBWTransferLength = 8).
    /// <para>通过 READ_CAPABILITY CBW/CSW 事务读取设备能力位（8 字节，对应 rkdeveloptool
    /// RKU_ReadCapability 的 dwCBWTransferLength = 8）。</para>
    /// The result is undecoded. The meaning of each capability bit is defined by the capability
    /// structure in upstream <c>RKComm.h</c>/<c>RKBoot.h</c>, which this repository does not carry a
    /// verifiable copy of, so no typed decoder is offered and callers must not interpret individual
    /// bits from the prose above alone. <see cref="ReadChipInfo"/> does return a typed result only
    /// because the layout of its 16-byte response is already exercised by an existing code path.
    /// <para>返回结果未解码。每个能力位的含义由上游 <c>RKComm.h</c>/<c>RKBoot.h</c> 中的 capability
    /// 结构定义，本仓库没有可核对的副本，因此不提供类型化解码器，调用方也不得仅凭上面的散文描述去解释
    /// 单个位。<see cref="ReadChipInfo"/> 之所以返回类型化结果，是因为其 16 字节响应的布局已被现有代码
    /// 路径实际使用。</para>
    /// </summary>
    /// <returns>The raw capability bytes returned by the device (at least 8).
    /// <para>设备返回的原始能力位字节（至少 8 个）。</para></returns>
    /// <exception cref="RkdlException">The data phase was shorter than the 8 bytes requested, or the
    /// CSW reported failure. <para>数据阶段短于请求的 8 字节，或 CSW 报告失败。</para></exception>
    public byte[] ReadCapability()
        => ReadDataCommand(RkdlCommand.ReadCapability, 8, RkdlFrame.CbwcbLengthShort, 8, 8);

    /// <summary>
    /// Resets the device via a DEVICE_RESET (0xFF) CBW/CSW transaction. The cached flash geometry is
    /// dropped once the command was answered, because a reset makes the device re-enumerate and the
    /// geometry reported afterwards is a fresh observation.
    /// <para>通过 DEVICE_RESET（0xFF）CBW/CSW 事务复位设备。命令得到应答后即丢弃缓存的闪存几何，
    /// 因为复位会让设备重新枚举，之后报告的几何是一次全新观测。</para>
    /// </summary>
    /// <param name="subCode">A <see cref="RkdlResetSubCode"/> value (default none). <para><see cref="RkdlResetSubCode"/> 子码（默认无）。</para></param>
    /// <returns>0 when the reset command completed. <para>复位命令完成时返回 0。</para></returns>
    /// <exception cref="RkdlException">The session is disposed, the CSW could not be validated, or
    /// the CSW reported failure. <para>会话已释放、CSW 无法校验，或 CSW 报告失败。</para></exception>
    public int ResetDevice(byte subCode = RkdlResetSubCode.None)
    {
        ThrowIfDisposed();
        var cbw = new RkdlCbw(RkdlCommand.DeviceReset, RkdlFrame.DirectionOut, 0,
            RkdlFrame.CbwcbLengthShort, subCode: subCode);
        WriteCbw(cbw);
        var csw = ReadCsw(cbw.Tag, recovery: true);
        InvalidateFlashGeometry();
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
    /// The range is validated against the device geometry before the first command is written, and
    /// the running position is accumulated in 64 bits with a checked add, so a range that would pass
    /// the 32-bit wire address cannot wrap back to the start of the media and erase it again.
    /// <para>在写出第一条命令前，范围会对照设备几何校验；运行位置以 64 位累加并做 checked 加法，因此一个
    /// 能通过 32 位线上地址的范围不可能回绕到介质起点再擦一遍。</para>
    /// Unverified boundary: this repository carries no verified erase-block-to-sector ratio, so the
    /// block arguments are passed to <c>cbwcb.dwAddress</c> verbatim exactly as before and are
    /// validated in those same wire units. Whether a given device counts ERASE_NORMAL addresses in
    /// erase blocks rather than 512-byte sectors can only be settled on real hardware; a device that
    /// counts in erase blocks therefore accepts a narrower range here than it physically has.
    /// <para>未验证边界：本仓库没有可核对的擦除块与扇区比例，因此块参数仍像以前一样原样填入
    /// <c>cbwcb.dwAddress</c>，并按同样的线上单位校验。某台设备究竟按擦除块还是按 512 字节扇区计
    /// ERASE_NORMAL 地址，只能在真机上确认；按擦除块计数的设备在此会接受比其物理容量更窄的范围。</para>
    /// </summary>
    /// <param name="blockPos">The first block to erase. <para>起始块号。</para></param>
    /// <param name="blockCount">The number of blocks to erase. <para>要擦除的块数。</para></param>
    /// <param name="force"><c>true</c> to use ERASE_FORCE (ignores bad-block markers). <para>是否使用 ERASE_FORCE（忽略坏块标记）。</para></param>
    /// <exception cref="RkdlException">The session is disposed, the range exceeds the device geometry
    /// or cannot be read because READ_FLASH_INFO failed, or a CSW reported failure.
    /// <para>会话已释放、范围超出设备几何或因 READ_FLASH_INFO 失败而无法读取几何，或 CSW 报告失败。</para></exception>
    public void EraseBlocks(uint blockPos, uint blockCount, bool force = false)
    {
        ThrowIfDisposed();
        ValidateLbaRange(blockPos, blockCount);
        var opcode = force ? (RkdlCommand)0x0B : RkdlCommand.EraseNormal;
        long position = blockPos;
        long remaining = blockCount;
        while (remaining > 0)
        {
            var chunk = (ushort)Math.Min(remaining, RkdlConstants.MaxEraseBlocksPerCommand);
            var cbw = new RkdlCbw(opcode, RkdlFrame.DirectionOut, 0,
                RkdlFrame.CbwcbLengthLong, address: (uint)position, sectorLength: chunk);
            WriteCbw(cbw);
            var csw = ReadCsw(cbw.Tag);
            if (!csw.IsSuccess)
            {
                throw new RkdlException(RkdlError.EraseFailed, $"Erase failed at block {position}, CSW status = {csw.Status}.");
            }

            position = checked(position + chunk);
            remaining -= chunk;
        }
    }

    /// <summary>
    /// Erases the entire flash via an ERASE_SYSTEMDISK (0x16) CBW/CSW transaction.
    /// <para>通过 ERASE_SYSTEMDISK（0x16）CBW/CSW 事务擦除整个 flash。</para>
    /// </summary>
    /// <returns>0 when the device acknowledged the erase with a successful CSW.
    /// <para>设备以成功的 CSW 确认擦除时返回 0。</para></returns>
    /// <exception cref="RkdlException">The session is disposed, or the CSW reported failure.
    /// <para>会话已释放，或 CSW 报告失败。</para></exception>
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
    /// <exception cref="RkdlException">The address or sector count is outside the unsigned wire
    /// range, the erased sector span leaves the addressed range beyond the device geometry, the
    /// device geometry is unusable because READ_FLASH_INFO reported an invalid sector count
    /// (<see cref="RkdlError.FlashInfoFailed"/>), or a CSW reported failure.
    /// <para>地址或扇区数超出无符号线上范围、被擦除扇区跨度使所寻址范围超出设备几何、因
    /// READ_FLASH_INFO 报告无效扇区数导致设备几何不可用（<see cref="RkdlError.FlashInfoFailed"/>），
    /// 或 CSW 报告失败。</para></exception>
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

        ValidateLbaRange(address, sectorCount);

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