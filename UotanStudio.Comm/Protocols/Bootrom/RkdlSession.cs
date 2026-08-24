using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Bootrom;

/// <summary>
/// Implements the Rockchip RKDL (Rockchip Download) protocol over USB control and bulk transfers.
/// <para>在 USB 控制和批量传输之上实现 Rockchip RKDL 协议。</para>
/// Protocol flow:
/// 1. Download first-stage loader via vendor request 0x0C / wIndex 0x0471
/// 2. Device re-enumerates, download second-stage loader via vendor request 0x0C / wIndex 0x0472
/// 3. Device enters rockusb mode; use CBW/CSW bulk transfers for flash operations
/// 4. Commands: read/write LBA, write GPT, erase flash, reset
/// <para>协议流程：通过 vendor 请求 0x0C / wIndex 0x0471 下载第一阶段 loader →
/// 设备重新枚举 → 通过 vendor 请求 0x0C / wIndex 0x0472 下载第二阶段 loader →
/// 设备进入 rockusb 模式 → 使用 CBW/CSW 批量传输进行 flash 操作。</para>
/// </summary>
/// <remarks>
/// Creates a new RKDL session over the specified transport. Bulk operations use
/// <paramref name="transport"/>.Read/Write directly; vendor control transfers (loader
/// download) require a <see cref="UsbDeviceTransport"/> and otherwise throw
/// <see cref="NotSupportedException"/>.
/// <para>在指定传输上创建新的 RKDL 会话。批量操作直接使用
/// <paramref name="transport"/>.Read/Write；vendor 控制传输（loader 下载）需要
/// <see cref="UsbDeviceTransport"/>，否则抛出 <see cref="NotSupportedException"/>。</para>
/// </remarks>
/// <param name="transport">The underlying transport. <para>底层传输。</para></param>
/// <param name="timeoutMs">Optional I/O timeout in milliseconds. <para>可选 I/O 超时（毫秒）。</para></param>
public sealed class RkdlSession(ICommTransport transport, int? timeoutMs = null) : IProtocolSession
{
    private readonly int _timeoutMs = timeoutMs ?? RkdlConstants.DefaultTimeoutMs;
    private bool _disposed;

    /// <summary>
    /// Gets the protocol name.
    /// <para>获取协议名称。</para>
    /// </summary>
    public string ProtocolName => "rkdl";

    /// <summary>
    /// Gets the underlying transport.
    /// <para>获取底层传输。</para>
    /// </summary>
    public ICommTransport Transport { get; } = transport ?? throw new ArgumentNullException(nameof(transport));

    /// <summary>
    /// Gets whether the session is connected and ready for commands.
    /// <para>获取会话是否已连接并准备好接收命令。</para>
    /// </summary>
    public bool IsConnected => !_disposed && Transport.IsConnected;

    /// <summary>
    /// Gets the underlying USB device session, when the transport is USB-based
    /// (<c>null</c> otherwise). Needed for vendor control transfers such as the
    /// 0x0471/0x0472 loader download.
    /// <para>当传输基于 USB 时获取底层 USB 设备会话（否则为 <c>null</c>）。
    /// 0x0471/0x0472 loader 下载等 vendor 控制传输需要它。</para>
    /// </summary>
    public IUsbDeviceSession? UsbSession => (Transport as UsbDeviceTransport)?.Session;

    /// <summary>Gets the USB vendor ID. USB 厂商 ID。</summary>
    public ushort VendorId => UsbSession?.DeviceInfo.VendorId ?? 0;

    /// <summary>Gets the USB product ID. USB 产品 ID。</summary>
    public ushort ProductId => UsbSession?.DeviceInfo.ProductId ?? 0;

    /// <summary>Gets whether the device is in rockusb mode (ready for flash operations).
    /// 设备是否处于 rockusb 模式（就绪可刷写）。</summary>
    public bool IsRockusbMode => IsRockchipDevice() && RkdlConstants.KnownLoaderProductIds.Contains(ProductId);

    /// <summary>
    /// Gets whether the device is still in mask ROM (bootrom) mode, i.e. a Rockchip device
    /// whose product ID is not one of the known loader-mode IDs. Only maskrom devices accept
    /// the 0x0471/0x0472 loader-download vendor requests.
    /// <para>获取设备是否仍处于 Mask ROM（bootrom）模式，即产品 ID 不在已知 loader 模式 ID
    /// 之列的 Rockchip 设备。只有 maskrom 设备接受 0x0471/0x0472 loader 下载 vendor 请求。</para>
    /// </summary>
    public bool IsMaskromMode => IsRockchipDevice() && !IsRockusbMode;

    /// <summary>
    /// Checks if this is a Rockchip bootrom device (maskrom or loader mode). Uses the upstream
    /// generic rule (vendor 0x2207 and pid >> 8 != 0) plus the known loader product IDs
    /// (see rkdeveloptool RKScan.cpp IsRockusbDevice).
    /// <para>检查是否为 Rockchip bootrom 设备（maskrom 或 loader 模式）。使用上游通用规则
    /// （厂商 0x2207 且 pid >> 8 != 0）加已知 loader 产品 ID（见 rkdeveloptool RKScan.cpp
    /// 的 IsRockusbDevice）。</para>
    /// </summary>
    public bool IsRockchipDevice()
        => VendorId == RkdlConstants.RockchipVid && ((ProductId >> 8) != 0 || RkdlConstants.KnownLoaderProductIds.Contains(ProductId));

    /// <summary>
    /// Downloads a complete RKBOOT loader image: parses the entry tables, sends every 471
    /// (first-stage) entry followed by every 472 (second-stage) entry through the
    /// 0x0471/0x0472 vendor requests, honouring each entry's delay, and finally waits one
    /// second for the device to re-enumerate in loader mode (rkdeveloptool CRKDevice::DownloadBoot).
    /// <para>下载完整的 RKBOOT loader 镜像：解析条目表，先通过 0x0471 vendor 请求发送全部 471
    /// （第一阶段）条目，再通过 0x0472 请求发送全部 472（第二阶段）条目，遵循每个条目的延时，
    /// 最后等待一秒让设备重新枚举为 loader 模式（rkdeveloptool 的 CRKDevice::DownloadBoot）。</para>
    /// </summary>
    /// <param name="loaderImage">The loader image bytes (RKBOOT .bin). <para>loader 镜像字节（RKBOOT .bin）。</para></param>
    /// <returns>The total number of bytes transferred. <para>实际传输的字节总数。</para></returns>
    public int DownloadBootLoader(byte[] loaderImage)
    {
        ArgumentNullException.ThrowIfNull(loaderImage);
        if (!IsMaskromMode)
        {
            throw new RkdlException(RkdlError.NotSupported, "Loader download requires a maskrom-mode device (0x0471/0x0472 vendor requests).");
        }

        var image = RkBootImage.Parse(loaderImage);
        var total = 0;

        foreach (var entry in image.Entries471)
        {
            total += DeviceRequest(RkdlConstants.RkdlWIndex471, SliceEntry(loaderImage, entry));
            if (entry.DataDelay > 0)
            {
                Thread.Sleep((int)entry.DataDelay);
            }
        }

        foreach (var entry in image.Entries472)
        {
            total += DeviceRequest(RkdlConstants.RkdlWIndex472, SliceEntry(loaderImage, entry));
            if (entry.DataDelay > 0)
            {
                Thread.Sleep((int)entry.DataDelay);
            }
        }

        // The device re-enumerates in loader (rockusb) mode after the second stage.
        Thread.Sleep(1000);
        return total;
    }

    /// <summary>
    /// Asynchronously downloads a complete RKBOOT loader image; see
    /// <see cref="DownloadBootLoader(byte[])"/> for the protocol flow.
    /// <para>异步下载完整的 RKBOOT loader 镜像；协议流程见
    /// <see cref="DownloadBootLoader(byte[])"/>。</para>
    /// </summary>
    /// <param name="loaderImage">The loader image bytes (RKBOOT .bin). <para>loader 镜像字节（RKBOOT .bin）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the total number of bytes transferred. <para>返回实际传输字节总数的任务。</para></returns>
    public Task<int> DownloadBootLoaderAsync(byte[] loaderImage, CancellationToken cancellationToken = default)
        => Task.Run(() => DownloadBootLoader(loaderImage), cancellationToken);

    /// <summary>
    /// Extracts the data slice of a loader entry. <para>提取 loader 条目的数据切片。</para>
    /// </summary>
    private static byte[] SliceEntry(byte[] loaderImage, RkBootEntry entry)
    {
        var end = (long)entry.DataOffset + entry.DataSize;
        if (entry.DataOffset >= loaderImage.Length || end > loaderImage.Length)
        {
            throw new RkdlException(RkdlError.InvalidParameter, $"Loader entry '{entry.Name}' data out of range.");
        }

        var data = new byte[entry.DataSize];
        Array.Copy(loaderImage, entry.DataOffset, data, 0, entry.DataSize);
        return data;
    }

    /// <summary>
    /// Downloads the first-stage loader via the 0x0471 vendor request
    /// (BmRequestType = 0x40, BRequest = 0x0C, wIndex = 0x0471). Data is sent in 4096-byte
    /// chunks with a trailing CRC-16/CCITT, including the 4094/4095 padding rules from
    /// rkdeveloptool RKU_DeviceRequest.
    /// <para>通过 0x0471 vendor 请求下载第一阶段 loader
    /// （BmRequestType = 0x40，BRequest = 0x0C，wIndex = 0x0471）。数据按 4096 字节分块发送，
    /// 尾部追加 CRC-16/CCITT，并遵循 rkdeveloptool RKU_DeviceRequest 的 4094/4095 填充规则。</para>
    /// </summary>
    /// <param name="loaderData">The loader image data. <para>loader 镜像数据。</para></param>
    /// <returns>The total number of bytes transferred. <para>实际传输的字节总数。</para></returns>
    public int DownloadBootLoader471(byte[] loaderData)
        => DeviceRequest(RkdlConstants.RkdlWIndex471, loaderData);

    /// <summary>
    /// Downloads the second-stage loader via the 0x0472 vendor request
    /// (BmRequestType = 0x40, BRequest = 0x0C, wIndex = 0x0472). See
    /// <see cref="DownloadBootLoader471"/> for the framing rules.
    /// <para>通过 0x0472 vendor 请求下载第二阶段 loader
    /// （BmRequestType = 0x40，BRequest = 0x0C，wIndex = 0x0472）。
    /// 帧规则见 <see cref="DownloadBootLoader471"/>。</para>
    /// </summary>
    /// <param name="loaderData">The loader image data. <para>loader 镜像数据。</para></param>
    /// <returns>The total number of bytes transferred. <para>实际传输的字节总数。</para></returns>
    public int DownloadBootLoader472(byte[] loaderData)
        => DeviceRequest(RkdlConstants.RkdlWIndex472, loaderData);

    /// <summary>
    /// Reads chip info from the device via a READ_CHIP_INFO CBW/CSW transaction (16 bytes).
    /// The first four bytes of the response are an ASCII chip tag (e.g. "320A"); it is
    /// exposed both raw and decoded (see upstream CRKDevice::CheckChip).
    /// <para>通过 READ_CHIP_INFO CBW/CSW 事务从设备读取芯片信息（16 字节）。响应的前 4 字节是
    /// ASCII 芯片标签（如 "320A"）；同时以原始值与解码字符串暴露（见上游 CRKDevice::CheckChip）。</para>
    /// </summary>
    public RkChipInfo ReadChipInfo()
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
    /// </summary>
    public RkFlashInfo ReadFlashInfo()
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
    /// Reads the device capability bits via a READ_CAPABILITY CBW/CSW transaction (16 bytes).
    /// <para>通过 READ_CAPABILITY CBW/CSW 事务读取设备能力位（16 字节）。</para>
    /// </summary>
    public byte[] ReadCapability()
        => ReadDataCommand(RkdlCommand.ReadCapability, 16, RkdlFrame.CbwcbLengthShort, 16, 16);

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
    public void WriteGPT(byte[] gptData, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (gptData == null || gptData.Length < RkdlConstants.SectorSize * RkdlGpt.PrimarySectors)
        {
            throw new RkdlException(RkdlError.InvalidParameter, "GPT image must span at least 34 sectors.");
        }

        // 1. Total sector count from flash info (main.cpp write_gpt).
        var totalSectors = (uint)ReadFlashInfo().SectorCount;
        if (totalSectors <= RkdlGpt.BackupSectors)
        {
            throw new RkdlException(RkdlError.FlashInfoFailed, $"Invalid flash sector count: {totalSectors}.");
        }

        // 2. Split master (34 sectors) and backup (33 sectors).
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

        // 3. Update the disk size in both tables (main.cpp update_gpt_disksize).
        RkdlGpt.UpdateDiskSize(master, backup, totalSectors);

        // 4. Write the master GPT at LBA 0 and the backup GPT at the end of the storage.
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
    /// <see cref="RkdlConstants.TransferChunkSize"/>. Partial final sectors are zero padded.
    /// <para>使用 WRITE_LBA（0x15）CBW/CSW 事务，将数据写入指定 LBA 地址（512 字节扇区），
    /// 按 <see cref="RkdlConstants.TransferChunkSize"/> 分块。最后一个不完整扇区补零。</para>
    /// </summary>
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

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(RkdlConstants.TransferChunkSize);
        try
        {
            long totalWritten = 0;
            var currentSector = address;
            while (totalWritten < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(RkdlConstants.TransferChunkSize, length - totalWritten);
                var n = dataStream.Read(buffer, 0, toRead);
                if (n == 0)
                {
                    throw new EndOfStreamException($"Stream ended at {totalWritten}/{length}");
                }

                // Align to full sectors, zero-padding the final partial sector.
                var sectorBytes = (n + RkdlConstants.SectorSize - 1) / RkdlConstants.SectorSize * RkdlConstants.SectorSize;
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
            case RkdlConstants.LoaderChunkSize - 1:   // 4095: pad one zero byte
                dataLength += 1;
                break;
            case RkdlConstants.LoaderChunkSize - 2:   // 4094: send a trailing zero packet
                sendPadPacket = true;
                break;
            default:
                break;
        }

        var buffer = new byte[dataLength + 2];
        Array.Copy(loaderData, buffer, loaderData.Length);
        // (padding + CRC bytes are already zeroed by the fresh buffer)

        var crc = RkdlCrc.Crc16Ccitt(buffer.AsSpan(0, dataLength));
        buffer[dataLength] = (byte)(crc >> 8);       // high byte first
        buffer[dataLength + 1] = (byte)(crc & 0xFF); // low byte second
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
        var n = Transport.Read(buffer, 0, readBufferSize, _timeoutMs);
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

    /// <summary>
    /// Returns the underlying USB device session for vendor control transfers, throwing
    /// <see cref="NotSupportedException"/> when the transport is not USB-based.
    /// <para>返回用于 vendor 控制传输的底层 USB 设备会话；当传输不是基于 USB 时抛出
    /// <see cref="NotSupportedException"/>。</para>
    /// </summary>
    private IUsbDeviceSession GetUsbSession()
    {
        return Transport is UsbDeviceTransport usb
            ? usb.Session
            : throw new NotSupportedException("RKDL loader download and vendor control transfers require a USB transport.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RkdlSession));
        }
    }

    /// <summary>
    /// Disposes the session and the underlying transport.
    /// <para>释放会话和底层传输。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Transport.Dispose();
    }
}