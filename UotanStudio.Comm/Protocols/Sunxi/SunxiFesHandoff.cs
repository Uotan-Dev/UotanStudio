using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Sunxi;

/// <summary>
/// Header of one <c>eGON.BT0</c> boot image, which is the payload FEL uploads to switch a cold device
/// into the FES service path. <para>一个 <c>eGON.BT0</c> 引导镜像的头部，即 FEL 为把冷设备切到 FES 服务
/// 路径而上传的负载。</para>
/// </summary>
/// <param name="ReturnAddress">DRAM parameter area the loader reports back into.
/// <para>加载器回写结果的 DRAM 参数区地址。</para></param>
/// <param name="RunAddress">Address the image is written to and executed from.
/// <para>镜像写入并被执行的地址。</para></param>
/// <param name="Length">Image length declared by the linker. <para>链接器声明的镜像长度。</para></param>
/// <param name="Platform">Platform string the header carries. <para>头部携带的平台字符串。</para></param>
public readonly record struct SunxiEgonBootImage(uint ReturnAddress, uint RunAddress, uint Length, string Platform);

/// <summary>
/// Header of one <c>u-boot</c> payload, which is the image that brings up the FES service and makes the
/// device re-enumerate. <para>一个 <c>u-boot</c> 负载的头部，即启动 FES 服务并使设备重新枚举的镜像。</para>
/// </summary>
/// <param name="RunAddress">Address the image is written to and executed from.
/// <para>镜像写入并被执行的地址。</para></param>
/// <param name="Length">Image length declared by the header. <para>头部声明的镜像长度。</para></param>
/// <param name="Platform">Platform string the header carries. <para>头部携带的平台字符串。</para></param>
public readonly record struct SunxiUbootImage(uint RunAddress, uint Length, string Platform);

/// <summary>
/// DRAM state the first-stage loader leaves in the parameter area.
/// <para>一级加载器留在参数区中的 DRAM 状态。</para>
/// </summary>
/// <param name="InitialisationFailed">Whether the loader flagged a failed DRAM initialisation.
/// <para>加载器是否标记 DRAM 初始化失败。</para></param>
/// <param name="Updated">Whether the loader rewrote the DRAM parameters.
/// <para>加载器是否重写了 DRAM 参数。</para></param>
public readonly record struct SunxiDramStatus(bool InitialisationFailed, bool Updated);

/// <summary>
/// Images the host uploads over FEL before executing the service payload: the U-Boot image and the
/// optional device tree, board configuration and compiled system configuration the service reads from
/// fixed memory slots after the reserved U-Boot area.
/// <para>主机在执行服务负载前经 FEL 上传的镜像：U-Boot 本体，以及服务从保留 U-Boot 区之后的固定内存槽位
/// 读取的可选设备树、板级配置与编译后系统配置。</para>
/// </summary>
/// <param name="Uboot">U-Boot image, including its <c>u-boot</c> header; required.
/// <para>U-Boot 镜像，含 <c>u-boot</c> 头部；必需。</para></param>
/// <param name="DeviceTree">Device tree blob placed after the U-Boot slot, or empty when the image embeds one.
/// <para>放在 U-Boot 槽位之后的设备树；镜像自带时为空。</para></param>
/// <param name="SysConfig">Compiled <c>sys_config.bin</c> placed after the device-tree slot, or empty.
/// <para>放在设备树槽位之后的编译后 <c>sys_config.bin</c>；可为空。</para></param>
/// <param name="BoardConfig">Board configuration binary placed after the system-configuration slot, or empty.
/// <para>放在系统配置槽位之后的板级配置二进制；可为空。</para></param>
public readonly record struct SunxiServiceImages(ReadOnlyMemory<byte> Uboot,
    ReadOnlyMemory<byte> DeviceTree = default, ReadOnlyMemory<byte> SysConfig = default,
    ReadOnlyMemory<byte> BoardConfig = default);

/// <summary>
/// Reads the boot image headers that drive the FEL-to-FES transition.
/// <para>读取驱动 FEL 到 FES 切换的引导镜像头部。</para>
/// </summary>
public static class SunxiBootImageHeader
{
    /// <summary>Magic of the first-stage loader that initialises DRAM. <para>初始化 DRAM 的一级加载器魔数。</para></summary>
    public static ReadOnlySpan<byte> EgonMagic => "eGON.BT0"u8;

    /// <summary>Magic of the payload that starts the FES service. <para>启动 FES 服务的负载魔数。</para></summary>
    public static ReadOnlySpan<byte> UbootMagic => "u-boot\0\0"u8;

    /// <summary>Shortest header the parser accepts. <para>解析器接受的最短头部。</para></summary>
    public const int HeaderSize = 48;

    /// <summary>
    /// Reads an <c>eGON.BT0</c> header.
    /// <para>读取 <c>eGON.BT0</c> 头部。</para>
    /// </summary>
    /// <param name="image">Image bytes, at least <see cref="HeaderSize"/> long.
    /// <para>镜像字节，至少 <see cref="HeaderSize"/> 个。</para></param>
    /// <returns>The parsed header. <para>解析出的头部。</para></returns>
    /// <exception cref="InvalidDataException">The image is too short or does not carry the magic.
    /// <para>镜像过短或未携带魔数。</para></exception>
    public static SunxiEgonBootImage ReadEgonBoot(ReadOnlyMemory<byte> image) => ReadEgonBoot(image.Span);

    /// <summary>
    /// Reads an <c>eGON.BT0</c> header from a span.
    /// <para>从跨度读取 <c>eGON.BT0</c> 头部。</para>
    /// </summary>
    /// <param name="image">Image bytes, at least <see cref="HeaderSize"/> long.
    /// <para>镜像字节，至少 <see cref="HeaderSize"/> 个。</para></param>
    /// <returns>The parsed header. <para>解析出的头部。</para></returns>
    /// <exception cref="InvalidDataException">The image is too short or does not carry the magic.
    /// <para>镜像过短或未携带魔数。</para></exception>
    public static SunxiEgonBootImage ReadEgonBoot(ReadOnlySpan<byte> image)
    {
        RequireMagic(image, EgonMagic, "eGON.BT0");
        return new SunxiEgonBootImage(
            BinaryPrimitives.ReadUInt32LittleEndian(image[28..]),
            BinaryPrimitives.ReadUInt32LittleEndian(image[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(image[16..]),
            ReadText(image[40..48]));
    }

    /// <summary>
    /// Reads a <c>u-boot</c> header.
    /// <para>读取 <c>u-boot</c> 头部。</para>
    /// </summary>
    /// <param name="image">Image bytes, at least <see cref="HeaderSize"/> long.
    /// <para>镜像字节，至少 <see cref="HeaderSize"/> 个。</para></param>
    /// <returns>The parsed header. <para>解析出的头部。</para></returns>
    /// <exception cref="InvalidDataException">The image is too short or does not carry the magic.
    /// <para>镜像过短或未携带魔数。</para></exception>
    public static SunxiUbootImage ReadUboot(ReadOnlyMemory<byte> image) => ReadUboot(image.Span);

    /// <summary>
    /// Reads a <c>u-boot</c> header from a span.
    /// <para>从跨度读取 <c>u-boot</c> 头部。</para>
    /// </summary>
    /// <param name="image">Image bytes, at least <see cref="HeaderSize"/> long.
    /// <para>镜像字节，至少 <see cref="HeaderSize"/> 个。</para></param>
    /// <returns>The parsed header. <para>解析出的头部。</para></returns>
    /// <exception cref="InvalidDataException">The image is too short or does not carry the magic.
    /// <para>镜像过短或未携带魔数。</para></exception>
    public static SunxiUbootImage ReadUboot(ReadOnlySpan<byte> image)
    {
        RequireMagic(image, UbootMagic, "u-boot");
        return new SunxiUbootImage(
            BinaryPrimitives.ReadUInt32LittleEndian(image[44..]),
            BinaryPrimitives.ReadUInt32LittleEndian(image[20..]),
            ReadText(image[36..44]));
    }

    private static void RequireMagic(ReadOnlySpan<byte> image, ReadOnlySpan<byte> magic, string name)
    {
        if (image.Length < HeaderSize)
        {
            throw new InvalidDataException($"A {name} header is at least {HeaderSize} bytes.");
        }

        if (!image[4..12].SequenceEqual(magic))
        {
            throw new InvalidDataException($"The image does not start with the {name} magic.");
        }
    }

    private static string ReadText(ReadOnlySpan<byte> field)
    {
        var terminator = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(terminator < 0 ? field : field[..terminator]);
    }
}

/// <summary>
/// Drives the two FEL uploads that move a cold Allwinner device from the USB boot ROM into the FES service:
/// the first-stage loader that initialises DRAM and returns to FEL, then the payload that starts the service
/// and makes the device re-enumerate. No FES command works
/// until the second upload completed and the host reconnected to the re-enumerated device.
/// <para>驱动把冷启动的全志设备从 USB boot ROM 带进 FES 服务的两次 FEL 上传：先上传初始化 DRAM 并返回 FEL 的
/// 一级加载器，再上传启动服务并使设备重新枚举的负载。第二次上传完成且主机
/// 重连到重新枚举的设备之前，没有任何 FES 命令可用。</para>
/// </summary>
public sealed class SunxiFesHandoff
{
    /// <summary>Byte length of the DRAM parameter area the loader reads and writes: two flag words followed by
    /// 32 parameter words. <para>加载器读写的 DRAM 参数区字节长度：两个标志字加 32 个参数字。</para></summary>
    public const int DramParameterLength = (2 + 32) * sizeof(uint);

    /// <summary>Offset of <c>uboot_data.work_mode</c> inside a <c>u-boot</c> image; the service only starts when the
    /// host sets it to <see cref="UsbProductWorkMode"/> before the upload.
    /// <para><c>u-boot</c> 镜像中 <c>uboot_data.work_mode</c> 的偏移；主机必须在之上传将其置为
    /// <see cref="UsbProductWorkMode"/>，服务才会启动。</para></summary>
    public const int WorkModeOffset = 48 + 176;

    /// <summary>Work mode that keeps U-Boot in the USB product path, which is what the FES service needs.
    /// <para>让 U-Boot 停留在 USB 产品路径的工作模式，即 FES 服务所需的值。</para></summary>
    public const int UsbProductWorkMode = 0x10;

    /// <summary>Memory reserved for the U-Boot image; the component slots start after it.
    /// <para>为 U-Boot 镜像保留的内存；组件槽位从其后开始。</para></summary>
    public const int UbootSlotLength = 2 * 1024 * 1024;

    /// <summary>Largest device tree the service slot accepts. <para>服务槽位接受的设备树上限。</para></summary>
    public const int DeviceTreeSlotLength = 1024 * 1024;

    /// <summary>Largest compiled system configuration the service slot accepts.
    /// <para>服务槽位接受的编译后系统配置上限。</para></summary>
    public const int SysConfigSlotLength = 512 * 1024;

    /// <summary>Largest board configuration the service slot accepts.
    /// <para>服务槽位接受的板级配置上限。</para></summary>
    public const int BoardConfigSlotLength = 512 * 1024;

    /// <summary>Offset of the initialization-failure flag inside the parameter area.
    /// <para>参数区中初始化失败标志的偏移。</para></summary>
    private const int DramInitFlagOffset = 0;

    /// <summary>Offset of the rewrite flag inside the parameter area.
    /// <para>参数区中重写标志的偏移。</para></summary>
    private const int DramUpdateFlagOffset = 4;

    private readonly Func<CancellationToken, Task<SunxiFesSession>> _reopenService;
    private readonly TimeSpan _dramPollInterval;
    private readonly TimeSpan _dramPollTimeout;
    private readonly TimeSpan _serviceRetryDelay;

    /// <summary>
    /// Creates a handoff that polls DRAM every second for one minute after the first-stage loader and waits
    /// three seconds between service reconnect attempts.
    /// <para>创建一次切换流程：一级加载器之后每秒轮询 DRAM 持续一分钟，服务重连尝试之间等待三秒。</para>
    /// </summary>
    /// <param name="reopenService">Releases the stale FEL transport, waits for the re-enumerated device and
    /// returns a session that already completed the FES handshake.
    /// <para>释放过期的 FEL 传输，等待重新枚举的设备，并返回已完成 FES 握手的会话。</para></param>
    public SunxiFesHandoff(Func<CancellationToken, Task<SunxiFesSession>> reopenService)
        : this(reopenService, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(3))
    {
    }

    /// <summary>
    /// Creates a handoff with explicit DRAM polling and reconnect delays.
    /// <para>以显式 DRAM 轮询与重连等待创建切换流程。</para>
    /// </summary>
    /// <param name="reopenService">Releases the stale FEL transport, waits for the re-enumerated device and
    /// returns a session that already completed the FES handshake.
    /// <para>释放过期的 FEL 传输，等待重新枚举的设备，并返回已完成 FES 握手的会话。</para></param>
    /// <param name="dramPollInterval">Wait between DRAM state reads. <para>两次 DRAM 状态读取之间的等待。</para></param>
    /// <param name="dramPollTimeout">Total time the DRAM state may stay pending.
    /// <para>DRAM 状态允许保持未完成的总时长。</para></param>
    /// <param name="serviceRetryDelay">Wait between service reconnect attempts.
    /// <para>服务重连尝试之间的等待。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="reopenService"/> is <see langword="null"/>.
    /// <para><paramref name="reopenService"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">A delay or the timeout is negative.
    /// <para>某个等待时间或超时为负。</para></exception>
    public SunxiFesHandoff(Func<CancellationToken, Task<SunxiFesSession>> reopenService, TimeSpan dramPollInterval,
        TimeSpan dramPollTimeout, TimeSpan serviceRetryDelay)
    {
        ArgumentNullException.ThrowIfNull(reopenService);
        ArgumentOutOfRangeException.ThrowIfLessThan(dramPollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(dramPollTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(serviceRetryDelay, TimeSpan.Zero);
        _reopenService = reopenService;
        _dramPollInterval = dramPollInterval;
        _dramPollTimeout = dramPollTimeout;
        _serviceRetryDelay = serviceRetryDelay;
    }

    /// <summary>
    /// Uploads and executes the first-stage loader, then polls the parameter area until the loader reports a
    /// completed DRAM initialisation. The area is cleared before the upload because a stale flag from an earlier
    /// run would otherwise be read back as this run's result.
    /// <para>上传并执行一级加载器，然后轮询参数区直到加载器报告 DRAM 初始化完成。上传前先清空参数区，否则上一次
    /// 运行遗留的标志会被当成本次结果读回。</para>
    /// </summary>
    /// <param name="session">Open FEL session. <para>已打开的 FEL 会话。</para></param>
    /// <param name="fes1Image">First-stage loader image, including its <c>eGON.BT0</c> header.
    /// <para>一级加载器镜像，含其 <c>eGON.BT0</c> 头部。</para></param>
    /// <param name="cancellationToken">Cancels the upload, the polling and the delays.
    /// <para>取消上传、轮询与等待。</para></param>
    /// <returns>The DRAM state the loader reported. <para>加载器上报的 DRAM 状态。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="fes1Image"/> is empty.
    /// <para><paramref name="fes1Image"/> 为空。</para></exception>
    /// <exception cref="InvalidDataException">The image is not an <c>eGON.BT0</c> image.
    /// <para>镜像不是 <c>eGON.BT0</c> 镜像。</para></exception>
    /// <exception cref="SunxiEfexException">The device rejected a stage, or the loader reported a failed DRAM
    /// initialisation. <para>设备拒绝了某个阶段，或加载器报告 DRAM 初始化失败。</para></exception>
    /// <exception cref="TimeoutException">The loader never reported a completed initialisation.
    /// <para>加载器始终没有报告初始化完成。</para></exception>
    public async Task<SunxiDramStatus> InitialiseDramAsync(SunxiFelSession session, ReadOnlyMemory<byte> fes1Image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (fes1Image.IsEmpty)
        {
            throw new ArgumentException("The first-stage loader image is empty.", nameof(fes1Image));
        }

        var header = SunxiBootImageHeader.ReadEgonBoot(fes1Image);
        await session.WriteAsync(header.ReturnAddress, new byte[DramParameterLength], cancellationToken)
            .ConfigureAwait(false);
        await session.WriteAsync(header.RunAddress, fes1Image, cancellationToken).ConfigureAwait(false);
        await session.ExecuteAsync(header.RunAddress, cancellationToken).ConfigureAwait(false);

        var attempts = _dramPollInterval <= TimeSpan.Zero || _dramPollTimeout <= TimeSpan.Zero
            ? 1
            : (int)Math.Max(1, Math.Ceiling(_dramPollTimeout.TotalMilliseconds / _dramPollInterval.TotalMilliseconds));
        Exception? lastReadFailure = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(_dramPollInterval, cancellationToken).ConfigureAwait(false);

            var parameters = new byte[DramParameterLength];
            try
            {
                await session.ReadAsync(header.ReturnAddress, parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or EndOfStreamException
                or TimeoutException or SunxiEfexException)
            {
                lastReadFailure = exception;
                continue;
            }

            var initFlag = BinaryPrimitives.ReadUInt32LittleEndian(parameters.AsSpan(DramInitFlagOffset));
            if (initFlag == 0)
            {
                continue;
            }

            if (initFlag == 1)
            {
                throw new SunxiEfexException("The first-stage loader reported a failed DRAM initialisation.");
            }

            var updated = BinaryPrimitives.ReadUInt32LittleEndian(parameters.AsSpan(DramUpdateFlagOffset)) != 0;
            return new SunxiDramStatus(InitialisationFailed: false, Updated: updated);
        }

        throw new TimeoutException(
            $"The first-stage loader did not report a completed DRAM initialisation within "
            + $"{_dramPollTimeout.TotalMilliseconds:0} ms.", lastReadFailure);
    }

    /// <summary>
    /// Uploads the device tree, system configuration and board configuration into the slots that follow the
    /// reserved U-Boot area, then uploads and executes the service payload and waits for the re-enumerated
    /// device to answer the FES handshake.
    /// <para>把设备树、系统配置与板级配置上传到保留 U-Boot 区之后的槽位，然后上传并执行服务负载，等待重新枚举的
    /// 设备应答 FES 握手。</para>
    /// </summary>
    /// <param name="session">Open FEL session. <para>已打开的 FEL 会话。</para></param>
    /// <param name="images">U-Boot image and the optional service components.
    /// <para>U-Boot 镜像与可选的服务组件。</para></param>
    /// <param name="attempts">Reconnect attempts before the handoff fails; each attempt waits
    /// <c>serviceRetryDelay</c> first. <para>切换失败前的重连尝试次数；每次尝试先等待
    /// <c>serviceRetryDelay</c>。</para></param>
    /// <param name="cancellationToken">Cancels the upload, the waits and the reconnect.
    /// <para>取消上传、等待与重连。</para></param>
    /// <returns>The FES session the reconnect produced. <para>重连产生的 FES 会话。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">The U-Boot image is empty or <paramref name="attempts"/> is not
    /// positive. <para>U-Boot 镜像为空，或 <paramref name="attempts"/> 不是正数。</para></exception>
    /// <exception cref="InvalidDataException">The U-Boot image is not a <c>u-boot</c> image, is too short to carry
    /// the work-mode field, or a component exceeds its memory slot.
    /// <para>U-Boot 镜像不是 <c>u-boot</c> 镜像、短于工作模式字段，或某个组件超出其内存槽位。</para></exception>
    /// <exception cref="TimeoutException">No attempt produced a device answering the FES handshake.
    /// <para>没有任何一次尝试得到应答 FES 握手的设备。</para></exception>
    public async Task<SunxiFesSession> StartServiceAsync(SunxiFelSession session, SunxiServiceImages images,
        int attempts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (images.Uboot.IsEmpty)
        {
            throw new ArgumentException("The service payload is empty.", nameof(images));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempts);
        var header = SunxiBootImageHeader.ReadUboot(images.Uboot);
        if (images.Uboot.Length < WorkModeOffset + sizeof(uint))
        {
            throw new InvalidDataException(
                $"A u-boot image carries the work mode at offset {WorkModeOffset} and must be at least "
                + $"{WorkModeOffset + sizeof(uint)} bytes.");
        }

        var componentBase = checked(header.RunAddress + (uint)UbootSlotLength);
        var systemConfigBase = checked(componentBase + (uint)DeviceTreeSlotLength);
        var boardConfigBase = checked(systemConfigBase + (uint)SysConfigSlotLength);
        await WriteComponentAsync(session, componentBase, images.DeviceTree, DeviceTreeSlotLength, "device tree",
            cancellationToken).ConfigureAwait(false);
        await WriteComponentAsync(session, systemConfigBase, images.SysConfig, SysConfigSlotLength,
            "system configuration", cancellationToken).ConfigureAwait(false);
        await WriteComponentAsync(session, boardConfigBase, images.BoardConfig, BoardConfigSlotLength,
            "board configuration", cancellationToken).ConfigureAwait(false);

        var image = images.Uboot.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(WorkModeOffset), UsbProductWorkMode);
        await session.WriteAsync(header.RunAddress, image, cancellationToken).ConfigureAwait(false);
        await ExecuteTolerantlyAsync(session, header.RunAddress, cancellationToken).ConfigureAwait(false);

        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(_serviceRetryDelay, cancellationToken).ConfigureAwait(false);
            try
            {
                return await _reopenService(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsReconnectFailure(exception))
            {
                lastFailure = exception;
            }
        }

        throw new TimeoutException(
            $"The device did not answer the FES handshake after {attempts} reconnect attempts.", lastFailure);
    }

    private static async Task WriteComponentAsync(SunxiFelSession session, uint address, ReadOnlyMemory<byte> data,
        int slotLength, string name, CancellationToken cancellationToken)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if (data.Length > slotLength)
        {
            throw new InvalidDataException($"The {name} is {data.Length} bytes, above its {slotLength}-byte slot.");
        }

        await session.WriteAsync(address, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes the service payload and tolerates a failed status read, because the device may leave FEL before
    /// the trailing status arrives. The reconnect that follows is what proves whether the payload ran.
    /// <para>执行服务负载并容忍状态读取失败，因为设备可能在尾部状态到达前就离开 FEL。随后的重连才是判断负载
    /// 是否运行成功的依据。</para>
    /// </summary>
    private static async Task ExecuteTolerantlyAsync(SunxiFelSession session, uint address,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.ExecuteAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or TimeoutException
            or SunxiEfexException)
        {
        }
    }

    private static bool IsReconnectFailure(Exception exception) =>
        exception is IOException or TimeoutException or EndOfStreamException or SunxiEfexException
            or InvalidOperationException or NotSupportedException;
}