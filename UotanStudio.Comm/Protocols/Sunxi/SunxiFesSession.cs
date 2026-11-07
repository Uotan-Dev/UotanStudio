using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Sunxi;

/// <summary>
/// FES session over the U-Boot service: storage queries, chunked downloads and uploads with a data tag,
/// the CRC verification commands the flashing flow relies on, and the erase and tool-mode commands that
/// bracket a run. Every transfer is split into 64 KiB frames whose address advances in bytes for data
/// tags and in 512-byte sectors for the raw and flash tags.
/// <para>U-Boot 服务之上的 FES 会话：存储查询、带数据标签的分块下载与上传、烧录流程依赖的 CRC 校验命令，
/// 以及界定一次运行的擦除与工具模式命令。每次传输拆成 64 KiB 帧，数据标签按字节推进地址，裸写与闪存标签
/// 按 512 字节扇区推进地址。</para>
/// </summary>
public sealed class SunxiFesSession : SunxiEfexSession
{
    /// <summary>Longest a single erase acknowledgement may take before the run is failed, in milliseconds.
    /// <para>单次擦除确认在运行失败前允许的最长等待，单位为毫秒。</para></summary>
    public const int EraseTimeoutMilliseconds = 300_000;

    /// <summary>Length of the erase request payload. <para>擦除请求负载的长度。</para></summary>
    private const int EraseRequestSize = 16;

    /// <summary>Creates a FES session over one bulk transport. <para>在一条批量传输之上创建 FES 会话。</para></summary>
    /// <param name="transport">Bulk transport the session runs on. <para>会话所使用的批量传输。</para></param>
    internal SunxiFesSession(ISunxiEfexTransport transport)
        : base(transport)
    {
    }

    /// <summary>
    /// Performs the identity handshake and rejects a device that is not running the FES service.
    /// <para>执行身份握手，并拒绝未运行 FES 服务的设备。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the handshake. <para>取消本次握手。</para></param>
    /// <returns>The reported identity. <para>上报的身份信息。</para></returns>
    /// <exception cref="SunxiEfexException">The device is not in FES mode, or answered with a non-zero status.
    /// <para>设备不在 FES 模式，或以非零状态应答。</para></exception>
    public async Task<SunxiDeviceIdentity> OpenAsync(CancellationToken cancellationToken = default)
    {
        var identity = await VerifyDeviceAsync(cancellationToken).ConfigureAwait(false);
        if (identity.Mode != SunxiDeviceMode.Fes)
        {
            throw new SunxiEfexException(
                $"The device reports mode {identity.Mode} but the FES session requires FES mode.");
        }

        return identity;
    }

    /// <summary>
    /// Queries the storage type the service exposes.
    /// <para>查询服务所暴露的存储类型。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the query. <para>取消本次查询。</para></param>
    /// <returns>The storage type word. <para>存储类型字。</para></returns>
    public Task<uint> QueryStorageAsync(CancellationToken cancellationToken = default) =>
        ReceiveValueAsync(SunxiEfexCommand.QueryStorage, cancellationToken);

    /// <summary>
    /// Queries the secure-boot and boot-media mode word.
    /// <para>查询安全启动与启动介质模式字。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the query. <para>取消本次查询。</para></param>
    /// <returns>The mode word. <para>模式字。</para></returns>
    public Task<uint> QuerySecureAsync(CancellationToken cancellationToken = default) =>
        ReceiveValueAsync(SunxiEfexCommand.QuerySecure, cancellationToken);

    /// <summary>
    /// Probes the flash capacity in 512-byte sectors.
    /// <para>探测闪存容量（512 字节扇区数）。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the probe. <para>取消本次探测。</para></param>
    /// <returns>The capacity in sectors. <para>扇区容量。</para></returns>
    public Task<uint> ProbeFlashSizeAsync(CancellationToken cancellationToken = default) =>
        ReceiveValueAsync(SunxiEfexCommand.FlashSizeProbe, cancellationToken);

    /// <summary>
    /// Queries the bitmask of the storage types the service exposes: bit0 raw NAND, bit1 eMMC, bit2 SPI NOR,
    /// bit3 UFS. The service answers with a single byte.
    /// <para>查询服务所暴露存储类型的位图：bit0 raw NAND、bit1 eMMC、bit2 SPI NOR、bit3 UFS。服务以单字节应答。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the query. <para>取消本次查询。</para></param>
    /// <returns>The storage bitmask. <para>存储类型位图。</para></returns>
    public async Task<uint> QueryStorageListAsync(CancellationToken cancellationToken = default)
    {
        var mask = new byte[1];
        await TransferAsync(SunxiEfexCommand.QueryStorageList, ReadOnlyMemory<byte>.Empty, null, mask,
            cancellationToken).ConfigureAwait(false);
        return mask[0];
    }

    /// <summary>
    /// Switches the flash controller on or off around a sequence of transfers.
    /// <para>在一组传输前后打开或关闭闪存控制器。</para>
    /// </summary>
    /// <remarks>
    /// The wire format's 36-byte structure is dropped to an all-zero buffer by the 12-byte inline window, so the
    /// storage type word is sent instead, which the service accepts.
    /// <para>线格式的 36 字节结构会被 12 字节内联窗口丢弃成全零缓冲，因此改为发送存储类型字，服务端接受该形式。</para>
    /// </remarks>
    /// <param name="storageType">Storage type reported by <see cref="QueryStorageAsync"/>.
    /// <para><see cref="QueryStorageAsync"/> 上报的存储类型。</para></param>
    /// <param name="on">Whether to switch the controller on. <para>是否打开控制器。</para></param>
    /// <param name="cancellationToken">Cancels the command. <para>取消本次命令。</para></param>
    /// <returns>A task that completes when the device switched the controller.
    /// <para>设备切换控制器后完成的任务。</para></returns>
    public Task SetFlashAsync(uint storageType, bool on, CancellationToken cancellationToken = default)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, storageType);
        return TransferAsync(on ? SunxiEfexCommand.FlashSetOn : SunxiEfexCommand.FlashSetOff, payload, null, null,
            cancellationToken);
    }

    /// <summary>
    /// Switches the active storage the following transfers target, which is how a device that reports several
    /// storage types selects the one a run operates on.
    /// <para>切换后续传输所针对的活动存储；上报多种存储的设备借此选定一次运行所用的介质。</para>
    /// </summary>
    /// <param name="flashType">Storage type to switch to, as reported by <see cref="QueryStorageAsync"/> or
    /// <see cref="QueryStorageListAsync"/>. <para>要切换到的存储类型；取
    /// <see cref="QueryStorageAsync"/> 或 <see cref="QueryStorageListAsync"/> 上报的值。</para></param>
    /// <param name="cancellationToken">Cancels the command. <para>取消本次命令。</para></param>
    /// <returns>A task that completes when the device switched the storage.
    /// <para>设备切换存储后完成的任务。</para></returns>
    /// <remarks>
    /// Like <see cref="SetFlashAsync"/>, the type word rides in the inline window; a larger wire structure is
    /// dropped by that window.
    /// <para>与 <see cref="SetFlashAsync"/> 相同，类型字放在内联窗口内；更大的线格式结构会被该窗口丢弃。</para>
    /// </remarks>
    public Task FlashSwitchAsync(uint flashType, CancellationToken cancellationToken = default)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, flashType);
        return TransferAsync(SunxiEfexCommand.FlashSwitch, payload, null, null, cancellationToken);
    }

    /// <summary>
    /// Sends a payload under a data tag, splitting it into 64 KiB transfers and marking the last chunk.
    /// <para>按数据标签发送负载，拆分为 64 KiB 传输并标记最后一块。</para>
    /// </summary>
    /// <param name="address">First address in the unit the tag implies. <para>标签所隐含单位下的首个地址。</para></param>
    /// <param name="data">Bytes to send. <para>待发送的字节。</para></param>
    /// <param name="tag">Data tag selecting the destination. <para>选择目标的数据标签。</para></param>
    /// <param name="cancellationToken">Cancels the download. <para>取消本次下载。</para></param>
    /// <param name="timeoutMilliseconds">Acknowledgement timeout per chunk, or <see langword="null"/> for the
    /// transport default. <para>每个分块的确认超时；<see langword="null"/> 表示使用传输默认值。</para></param>
    /// <returns>A task that completes when every chunk is acknowledged. <para>所有分块都被确认后完成的任务。</para></returns>
    /// <exception cref="ArgumentException">The payload is empty. <para>负载为空。</para></exception>
    /// <exception cref="OverflowException">The address range passes the 32-bit address space.
    /// <para>地址范围越过 32 位地址空间。</para></exception>
    public async Task DownloadAsync(uint address, ReadOnlyMemory<byte> data, SunxiFesDataTag tag,
        CancellationToken cancellationToken = default, int? timeoutMilliseconds = null)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("A FES download needs at least one byte.", nameof(data));
        }

        var byteAddressed = SunxiEfexFrames.IsByteAddressed(tag);
        var offset = 0;
        var current = address;
        while (offset < data.Length)
        {
            var length = Math.Min(SunxiEfexConstants.MaximumTransfer, data.Length - offset);
            var last = offset + length == data.Length;
            var flags = (uint)tag | (last ? SunxiEfexConstants.TransferFinishTag : 0u);
            var payload = SunxiEfexFrames.BuildFesTransferPayload(current, (uint)length, flags);
            await TransferAsync(SunxiEfexCommand.FesDown, payload, data.Slice(offset, length), null,
                cancellationToken, timeoutMilliseconds).ConfigureAwait(false);
            current = checked(current + (byteAddressed ? (uint)length : (uint)(length / SunxiEfexConstants.SectorSize)));
            offset += length;
        }
    }

    /// <summary>
    /// Reads a payload back from storage under a data tag, splitting it into 64 KiB transfers and marking the
    /// last chunk. The service sends exactly the requested bytes of each frame.
    /// <para>按数据标签从存储读回负载，拆分为 64 KiB 传输并标记最后一块。服务对每一帧恰好送出所请求的字节数。</para>
    /// </summary>
    /// <param name="address">First address in the unit the tag implies. <para>标签所隐含单位下的首个地址。</para></param>
    /// <param name="destination">Buffer that the read fills. <para>由本次读取填满的缓冲区。</para></param>
    /// <param name="tag">Data tag selecting the source. <para>选择来源的数据标签。</para></param>
    /// <param name="cancellationToken">Cancels the upload. <para>取消本次上传。</para></param>
    /// <param name="timeoutMilliseconds">Read timeout per chunk, or <see langword="null"/> for the transport
    /// default. <para>每个分块的读取超时；<see langword="null"/> 表示使用传输默认值。</para></param>
    /// <returns>A task that completes when every chunk arrived. <para>所有分块都到达后完成的任务。</para></returns>
    /// <exception cref="ArgumentException">The destination is empty. <para>目标缓冲区为空。</para></exception>
    /// <exception cref="OverflowException">The address range passes the 32-bit address space.
    /// <para>地址范围越过 32 位地址空间。</para></exception>
    public async Task UploadAsync(uint address, Memory<byte> destination, SunxiFesDataTag tag,
        CancellationToken cancellationToken = default, int? timeoutMilliseconds = null)
    {
        if (destination.IsEmpty)
        {
            throw new ArgumentException("A FES upload needs at least one byte.", nameof(destination));
        }

        await UploadCoreAsync(SunxiEfexCommand.FesUp, address, destination, tag, alwaysByteAddressed: false,
            cancellationToken, timeoutMilliseconds).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads raw NAND directly into host bytes, which is how the boot partitions and their sizes are read back.
    /// Unlike the tagged transfers this path always advances its address in bytes, whatever the tag implies.
    /// <para>把 raw NAND 直读到主机字节，boot 分区与其大小都经此读回。与带标签传输不同，无论标签隐含什么单位，
    /// 该路径的地址始终按字节推进。</para>
    /// </summary>
    /// <param name="address">First byte address on the raw NAND. <para>raw NAND 上的首个字节地址。</para></param>
    /// <param name="destination">Buffer that the read fills. <para>由本次读取填满的缓冲区。</para></param>
    /// <param name="tag">Data tag selecting the source, usually <see cref="SunxiFesDataTag.Raw"/>, a boot
    /// partition tag or a size query tag. <para>选择来源的数据标签；通常为 <see cref="SunxiFesDataTag.Raw"/>、
    /// boot 分区标签或大小查询标签。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <param name="timeoutMilliseconds">Read timeout per chunk, or <see langword="null"/> for the transport
    /// default. <para>每个分块的读取超时；<see langword="null"/> 表示使用传输默认值。</para></param>
    /// <returns>A task that completes when every chunk arrived. <para>所有分块都到达后完成的任务。</para></returns>
    /// <exception cref="ArgumentException">The destination is empty. <para>目标缓冲区为空。</para></exception>
    /// <exception cref="OverflowException">The address range passes the 32-bit address space.
    /// <para>地址范围越过 32 位地址空间。</para></exception>
    public Task UploadNandAsync(uint address, Memory<byte> destination, SunxiFesDataTag tag,
        CancellationToken cancellationToken = default, int? timeoutMilliseconds = null) =>
        UploadCoreAsync(SunxiEfexCommand.FesNand, address, RequireUploadDestination(destination), tag,
            alwaysByteAddressed: true, cancellationToken, timeoutMilliseconds);

    /// <summary>
    /// Reads SPI NAND directly into host bytes; the address always advances in bytes.
    /// <para>把 SPI NAND 直读到主机字节；地址始终按字节推进。</para>
    /// </summary>
    /// <param name="address">First byte address on the SPI NAND. <para>SPI NAND 上的首个字节地址。</para></param>
    /// <param name="destination">Buffer that the read fills. <para>由本次读取填满的缓冲区。</para></param>
    /// <param name="tag">Data tag selecting the source, usually <see cref="SunxiFesDataTag.Raw"/>, a boot
    /// partition tag or a size query tag. <para>选择来源的数据标签；通常为 <see cref="SunxiFesDataTag.Raw"/>、
    /// boot 分区标签或大小查询标签。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <param name="timeoutMilliseconds">Read timeout per chunk, or <see langword="null"/> for the transport
    /// default. <para>每个分块的读取超时；<see langword="null"/> 表示使用传输默认值。</para></param>
    /// <returns>A task that completes when every chunk arrived. <para>所有分块都到达后完成的任务。</para></returns>
    /// <exception cref="ArgumentException">The destination is empty. <para>目标缓冲区为空。</para></exception>
    /// <exception cref="OverflowException">The address range passes the 32-bit address space.
    /// <para>地址范围越过 32 位地址空间。</para></exception>
    public Task UploadSpinandAsync(uint address, Memory<byte> destination, SunxiFesDataTag tag,
        CancellationToken cancellationToken = default, int? timeoutMilliseconds = null) =>
        UploadCoreAsync(SunxiEfexCommand.FesSpinand, address, RequireUploadDestination(destination), tag,
            alwaysByteAddressed: true, cancellationToken, timeoutMilliseconds);

    /// <summary>
    /// Reads SPI NOR directly into host bytes; the address always advances in bytes.
    /// <para>把 SPI NOR 直读到主机字节；地址始终按字节推进。</para>
    /// </summary>
    /// <param name="address">First byte address on the SPI NOR. <para>SPI NOR 上的首个字节地址。</para></param>
    /// <param name="destination">Buffer that the read fills. <para>由本次读取填满的缓冲区。</para></param>
    /// <param name="tag">Data tag selecting the source, usually <see cref="SunxiFesDataTag.Raw"/>, a boot
    /// partition tag or a size query tag. <para>选择来源的数据标签；通常为 <see cref="SunxiFesDataTag.Raw"/>、
    /// boot 分区标签或大小查询标签。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <param name="timeoutMilliseconds">Read timeout per chunk, or <see langword="null"/> for the transport
    /// default. <para>每个分块的读取超时；<see langword="null"/> 表示使用传输默认值。</para></param>
    /// <returns>A task that completes when every chunk arrived. <para>所有分块都到达后完成的任务。</para></returns>
    /// <exception cref="ArgumentException">The destination is empty. <para>目标缓冲区为空。</para></exception>
    /// <exception cref="OverflowException">The address range passes the 32-bit address space.
    /// <para>地址范围越过 32 位地址空间。</para></exception>
    public Task UploadNorAsync(uint address, Memory<byte> destination, SunxiFesDataTag tag,
        CancellationToken cancellationToken = default, int? timeoutMilliseconds = null) =>
        UploadCoreAsync(SunxiEfexCommand.FesNor, address, RequireUploadDestination(destination), tag,
            alwaysByteAddressed: true, cancellationToken, timeoutMilliseconds);

    private static Memory<byte> RequireUploadDestination(Memory<byte> destination)
    {
        if (destination.IsEmpty)
        {
            throw new ArgumentException("A FES upload needs at least one byte.", nameof(destination));
        }

        return destination;
    }

    private async Task UploadCoreAsync(SunxiEfexCommand command, uint address, Memory<byte> destination,
        SunxiFesDataTag tag, bool alwaysByteAddressed, CancellationToken cancellationToken,
        int? timeoutMilliseconds)
    {
        var byteAddressed = alwaysByteAddressed || SunxiEfexFrames.IsByteAddressed(tag);
        var offset = 0;
        var current = address;
        while (offset < destination.Length)
        {
            var length = Math.Min(SunxiEfexConstants.MaximumTransfer, destination.Length - offset);
            var last = offset + length == destination.Length;
            var flags = (uint)tag | (last ? SunxiEfexConstants.TransferFinishTag : 0u);
            var payload = SunxiEfexFrames.BuildFesTransferPayload(current, (uint)length, flags);
            await TransferAsync(command, payload, null, destination.Slice(offset, length),
                cancellationToken, timeoutMilliseconds).ConfigureAwait(false);
            current = checked(current + (byteAddressed ? (uint)length : (uint)(length / SunxiEfexConstants.SectorSize)));
            offset += length;
        }
    }

    /// <summary>
    /// Erases storage through the dedicated erase tag; the flag selects a full erase, a selected-partition
    /// erase or no erase at all.
    /// <para>通过专用擦除标签擦除存储；标志选择整片擦除、所选分区擦除或不擦除。</para>
    /// </summary>
    /// <param name="flag">Erase scope the service must apply. <para>服务必须应用的擦除范围。</para></param>
    /// <param name="cancellationToken">Cancels the erase. <para>取消本次擦除。</para></param>
    /// <param name="timeoutMilliseconds">Acknowledgement timeout, or <see langword="null"/> for
    /// <see cref="EraseTimeoutMilliseconds"/>. <para>确认超时；<see langword="null"/> 表示使用
    /// <see cref="EraseTimeoutMilliseconds"/>。</para></param>
    /// <returns>A task that completes when the service acknowledged the erase.
    /// <para>服务确认擦除后完成的任务。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="flag"/> is not a defined erase flag.
    /// <para><paramref name="flag"/> 不是已定义的擦除标志。</para></exception>
    /// <exception cref="SunxiEfexException">The device answered with a non-zero status.
    /// <para>设备以非零状态应答。</para></exception>
    public Task EraseAsync(SunxiEraseFlag flag, CancellationToken cancellationToken = default,
        int? timeoutMilliseconds = null)
    {
        if (!Enum.IsDefined(flag))
        {
            throw new ArgumentOutOfRangeException(nameof(flag), flag, "Unknown erase flag.");
        }

        var request = new byte[EraseRequestSize];
        BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)flag);
        return DownloadAsync(0, request, SunxiFesDataTag.Erase, cancellationToken,
            timeoutMilliseconds ?? EraseTimeoutMilliseconds);
    }

    /// <summary>
    /// Compares a storage range against a CRC in the service.
    /// <para>在服务内把存储区间与 CRC 比较。</para>
    /// </summary>
    /// <param name="address">Storage address to verify. <para>待校验的存储地址。</para></param>
    /// <param name="size">Byte count to verify. <para>待校验的字节数。</para></param>
    /// <param name="cancellationToken">Cancels the verification. <para>取消本次校验。</para></param>
    /// <returns>The flag and CRC words reported by the device. <para>设备上报的标志与 CRC 字。</para></returns>
    public Task<SunxiFesVerifyResult> VerifyValueAsync(uint address, ulong size,
        CancellationToken cancellationToken = default) =>
        VerifyAsync(SunxiEfexCommand.VerifyValue, SunxiEfexFrames.BuildFesVerifyValuePayload(address, size),
            cancellationToken);

    /// <summary>
    /// Queries the status of a previous tagged transfer.
    /// <para>查询前一次带标签传输的状态。</para>
    /// </summary>
    /// <param name="tag">Tag stored in the transfer. <para>传输中记录的状态标签。</para></param>
    /// <param name="cancellationToken">Cancels the query. <para>取消本次查询。</para></param>
    /// <returns>The flag and CRC words reported by the device. <para>设备上报的标志与 CRC 字。</para></returns>
    public Task<SunxiFesVerifyResult> VerifyStatusAsync(uint tag, CancellationToken cancellationToken = default) =>
        VerifyAsync(SunxiEfexCommand.VerifyStatus, SunxiEfexFrames.BuildFesVerifyStatusPayload(tag),
            cancellationToken);

    /// <summary>
    /// Verifies the U-Boot block a previous tagged transfer wrote, using the same reply shape as the status
    /// verification.
    /// <para>校验前一次带标签传输写入的 U-Boot 块，应答形状与状态校验相同。</para>
    /// </summary>
    /// <param name="tag">Tag stored in the transfer. <para>传输中记录的状态标签。</para></param>
    /// <param name="cancellationToken">Cancels the verification. <para>取消本次校验。</para></param>
    /// <returns>The flag and CRC words reported by the device. <para>设备上报的标志与 CRC 字。</para></returns>
    public Task<SunxiFesVerifyResult> VerifyUbootBlkAsync(uint tag, CancellationToken cancellationToken = default) =>
        VerifyAsync(SunxiEfexCommand.VerifyUbootBlk, SunxiEfexFrames.BuildFesVerifyStatusPayload(tag),
            cancellationToken);

    /// <summary>
    /// Reports the tool mode and the mode the firmware should switch to afterwards.
    /// <para>上报工具模式以及固件随后应切换到的模式。</para>
    /// </summary>
    /// <param name="mode">Mode the tool runs in. <para>工具运行的模式。</para></param>
    /// <param name="nextMode">Mode requested after the last transfer. <para>最后一次传输后请求的模式。</para></param>
    /// <param name="cancellationToken">Cancels the command. <para>取消本次命令。</para></param>
    /// <returns>A task that completes when the device acknowledged the modes.
    /// <para>设备确认模式后完成的任务。</para></returns>
    public Task SetToolModeAsync(SunxiToolMode mode, SunxiToolMode nextMode,
        CancellationToken cancellationToken = default)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)mode);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)nextMode);
        return TransferAsync(SunxiEfexCommand.ToolMode, payload, null, null, cancellationToken);
    }

    private async Task<uint> ReceiveValueAsync(SunxiEfexCommand command, CancellationToken cancellationToken)
    {
        var value = new byte[4];
        await TransferAsync(command, ReadOnlyMemory<byte>.Empty, null, value, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    /// <summary>
    /// Sends one tagged download frame carrying caller-supplied flags, which is how the flasher streams a
    /// package entry segment by segment while keeping the addressing and last-chunk marking under its control.
    /// <para>发送一帧携带调用方所给标志的下载帧；烧录器借此逐段流出包内条目，同时把寻址与末块标记掌握在自己手里。</para>
    /// </summary>
    /// <param name="address">Address in the unit the tag implies. <para>标签所隐含单位下的地址。</para></param>
    /// <param name="length">Chunk length in bytes. <para>分块字节长度。</para></param>
    /// <param name="flags">Data tag combined with transfer flags. <para>与传输标志组合的数据标签。</para></param>
    /// <param name="data">Chunk bytes. <para>分块字节。</para></param>
    /// <param name="cancellationToken">Cancels the transfer. <para>取消本次传输。</para></param>
    /// <param name="timeoutMilliseconds">Acknowledgement timeout, or <see langword="null"/> for the transport
    /// default. <para>确认超时；<see langword="null"/> 表示使用传输默认值。</para></param>
    internal Task DownloadChunkAsync(uint address, uint length, uint flags, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken, int? timeoutMilliseconds = null) =>
        TransferAsync(SunxiEfexCommand.FesDown, SunxiEfexFrames.BuildFesTransferPayload(address, length, flags),
            data, null, cancellationToken, timeoutMilliseconds);

    private async Task<SunxiFesVerifyResult> VerifyAsync(SunxiEfexCommand command, byte[] payload,
        CancellationToken cancellationToken)
    {
        var result = new byte[12];
        await TransferAsync(command, payload, null, result, cancellationToken).ConfigureAwait(false);
        return SunxiEfexFrames.ReadVerifyResult(result);
    }

    private async Task TransferAsync(SunxiEfexCommand command, ReadOnlyMemory<byte> payload,
        ReadOnlyMemory<byte>? send, Memory<byte>? receive, CancellationToken cancellationToken,
        int? timeoutMilliseconds = null)
    {
        var header = new byte[SunxiEfexConstants.FesTransferSize];
        SunxiEfexFrames.WriteFesTransfer(header, command, payload.Span);
        await Transport.WriteAsync(header, cancellationToken, timeoutMilliseconds).ConfigureAwait(false);
        if (send is { } outgoing)
        {
            await Transport.WriteAsync(outgoing, cancellationToken, timeoutMilliseconds).ConfigureAwait(false);
        }

        if (receive is { } incoming)
        {
            await ReadExactAsync(incoming, "FES payload", cancellationToken, timeoutMilliseconds)
                .ConfigureAwait(false);
        }

        await ReadResponseAsync(cancellationToken, timeoutMilliseconds).ConfigureAwait(false);
    }
}