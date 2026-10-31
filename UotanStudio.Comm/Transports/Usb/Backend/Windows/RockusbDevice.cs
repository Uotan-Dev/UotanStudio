using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using static UotanStudio.Comm.Transports.Usb.Backend.Windows.Win32API;

namespace UotanStudio.Comm.Transports.Usb.Backend.Windows;

/// <summary>
/// Drives a Rockchip boot ROM port whose interface is claimed by Rockchip's own <c>rockusb.sys</c> driver instead of
/// WinUSB. The driver is a bulk-only pass-through built on the Windows bulkusb pattern: it registers the generic USB
/// device interface, binds exactly one pipe per opened handle, and moves bulk traffic through plain
/// <c>ReadFile</c> and <c>WriteFile</c> on those handles; control traffic and the loader handshake go
/// through three private IOCTLs.
/// <para>驱动接口被瑞芯微自带 <c>rockusb.sys</c> 而非 WinUSB 占用的 boot ROM 端口。该驱动继承了 Windows bulkusb 范式：
/// 注册通用 USB 设备接口、为每个已打开句柄绑定一条管道，并通过这些句柄上的 <c>ReadFile</c> 与
/// <c>WriteFile</c> 完成批量传输；控制流量与 loader 握手则走三个私有 IOCTL。</para>
/// </summary>
/// <remarks>
/// The driver decides which pipe a handle talks to from the trailing digits of the file name the caller opens against
/// the interface symbolic link, in the <c>\PIPEnn</c> form the official <c>RKDevTool</c> uses:
/// <c>\PIPE00</c> selects the interface's first device-to-host (IN) endpoint, <c>\PIPE01</c> its first
/// host-to-device (OUT) endpoint, and <c>\PIPE02</c> through <c>\PIPE05</c> the pipe with that index in configuration
/// descriptor order. A handle opened without such a suffix carries no pipe, so <c>ReadFile</c> and
/// <c>WriteFile</c> on it are rejected; that is why this backend holds one control handle for the IOCTLs plus a
/// separate handle per bulk direction, and why one handle can never serve both directions. Each pipe handle asks only
/// for the access right of its own direction, because the driver's per-direction read and write paths reject a handle
/// that does not carry it.
/// <para>驱动依据调用方针对接口符号链接打开时所用文件名末尾的数字决定句柄对应哪条管道，形式是与官方
/// <c>RKDevTool</c> 一致的 <c>\PIPEnn</c>：<c>\PIPE00</c> 选取接口的第一条设备到主机（IN）端点，
/// <c>\PIPE01</c> 选取其第一条主机到设备（OUT）端点，<c>\PIPE02</c> 至 <c>\PIPE05</c> 则按配置描述符顺序选取该序号的
/// 管道。不带该后缀打开的句柄不携带管道，其上的 <c>ReadFile</c> 与 <c>WriteFile</c> 会被拒绝；因此本后端保留一个用于
/// IOCTL 的控制句柄，再为每个批量方向各持一个句柄，同一个句柄绝不能同时服务两个方向。每条管道句柄只申请自身方向所需的
/// 访问权限，因为驱动按方向划分的读写路径会拒绝不携带该权限的句柄。</para>
/// </remarks>
internal sealed unsafe class RockusbDevice : UsbDevice
{
    private const int IoTimeoutMs = 30000;
    private const int ConfigurationDescriptorBufferBytes = 4096;
    private const int PipeResetBudgetMs = 3000;
    private const int ERROR_INVALID_HANDLE_CODE = 6;
    private const int ERROR_NOT_SUPPORTED_CODE = 50;
    private const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    private const int ERROR_NO_SUCH_DEVICE = 433;
    private const int ERROR_DEVICE_REMOVED = 1617;
    private const uint WAIT_OBJECT_0 = 0;

    /// <summary>bulkusb <c>IOCTL_BULKUSB_GET_CONFIG_DESCRIPTOR</c>: returns the configuration descriptor the driver read
    /// and configured when it started, which is the only way to learn the interface class and the bulk endpoint
    /// addresses while <c>rockusb.sys</c> owns the port.
    /// <para>bulkusb 的 <c>IOCTL_BULKUSB_GET_CONFIG_DESCRIPTOR</c>：返回驱动启动时读取并配置的配置描述符，在
    /// <c>rockusb.sys</c> 占用端口时这是了解接口类与批量端点地址的唯一途径。</para>
    /// </summary>
    private const uint IoGetConfigurationCode = 0x00220000;

    /// <summary>bulkusb <c>IOCTL_BULKUSB_RESET_PIPE</c>: resets the pipe the calling handle is bound to. It requires a
    /// pipe handle and is only the fallback recovery path.
    /// <para>bulkusb 的 <c>IOCTL_BULKUSB_RESET_PIPE</c>：复位调用句柄绑定的那条管道。它要求使用管道句柄，且只作为兜底
    /// 恢复手段。</para>
    /// </summary>
    private const uint IoResetPipeCode = 0x00220008;

    /// <summary>Rockchip's <c>IOCTL_BULKUSB_RESET_ALL_PIPES</c>, the reset the official <c>RKDevTool</c> issues before it
    /// retries a bulk command: the driver aborts and resets every pipe of the interface in one call, so a pipe left halted
    /// by the previous owner of the port cannot poison the next transfer. It takes a one-byte input buffer that the driver
    /// ignores and requires write access.
    /// <para>瑞芯微的 <c>IOCTL_BULKUSB_RESET_ALL_PIPES</c>，即官方 <c>RKDevTool</c> 在重试批量命令之前发出的复位：驱动在
    /// 一次调用中中止并复位该接口的全部管道，因此前一占用者留下的已停住管道不会影响下一次传输。它接受一个驱动并不读取的
    /// 单字节输入缓冲区，并要求写权限。</para>
    /// </summary>
    private const uint IoResetAllPipesCode = 0x8000A008;

    /// <summary>Vendor request entry for the <c>0x0471</c> (first stage) loader download: the driver sends
    /// <c>bRequest 0x0C</c> with <c>wValue 0</c> and <c>wIndex 0x0471</c> as a host-to-device control transfer and takes
    /// the payload from the IOCTL input buffer.
    /// <para><c>0x0471</c>（第一阶段）loader 下载的厂商请求入口：驱动以主机到设备的控制传输发送 <c>bRequest 0x0C</c>、
    /// <c>wValue 0</c> 与 <c>wIndex 0x0471</c>，负载取自 IOCTL 输入缓冲区。</para>
    /// </summary>
    private const uint IoVendorRequest471Code = 0x8000A000;

    /// <summary>Vendor request entry for the <c>0x0472</c> (second stage) loader download, the same request with
    /// <c>wIndex 0x0472</c>.
    /// <para><c>0x0472</c>（第二阶段）loader 下载的厂商请求入口，即 <c>wIndex 0x0472</c> 的同一种请求。</para>
    /// </summary>
    private const uint IoVendorRequest472Code = 0x8000A004;

    private const byte TransferTypeMask = 0x03;
    private const byte BulkTransferType = 0x02;
    private const byte VendorRequestType = 0x40;
    private const byte VendorRequestDownloadBoot = 0x0C;
    private const ushort WIndex471 = 0x0471;
    private const ushort WIndex472 = 0x0472;

    /// <summary>File-name suffix selecting the interface's first IN endpoint, as the driver parses it.
    /// <para>选取接口第一条 IN 端点的文件名后缀，与驱动的解析方式一致。</para>
    /// </summary>
    private const string FirstInPipeName = "PIPE00";

    /// <summary>File-name suffix selecting the interface's first OUT endpoint, as the driver parses it.
    /// <para>选取接口第一条 OUT 端点的文件名后缀，与驱动的解析方式一致。</para>
    /// </summary>
    private const string FirstOutPipeName = "PIPE01";

    /// <summary>Prefix of the suffix that selects the pipe with a literal index, in two digits.
    /// <para>按字面序号选取管道的后缀前缀，序号写成两位数。</para>
    /// </summary>
    private const string LiteralPipeNamePrefix = "PIPE";

    private SafeFileHandle _controlHandle = new(new IntPtr(-1), ownsHandle: true);
    private SafeFileHandle _bulkInHandle = new(new IntPtr(-1), ownsHandle: true);
    private SafeFileHandle _bulkOutHandle = new(new IntPtr(-1), ownsHandle: true);
    private bool _disposed;
    private byte _endpointIn;
    private byte _endpointOut;

    /// <summary>
    /// Gets the control handle the driver IOCTLs run on, or <see cref="INVALID_HANDLE_VALUE"/> when the device is closed.
    /// <para>获取驱动 IOCTL 使用的控制句柄；设备已关闭时为 <see cref="INVALID_HANDLE_VALUE"/>。</para>
    /// </summary>
    public IntPtr Handle => _controlHandle.IsInvalid ? INVALID_HANDLE_VALUE : _controlHandle.DangerousGetHandle();

    /// <inheritdoc/>
    public override byte EndpointIn => _endpointIn;

    /// <inheritdoc/>
    public override byte EndpointOut => _endpointOut;

    /// <summary>
    /// Gets or sets the session options the discovery path requested for this device.
    /// <para>获取或设置发现路径为该设备请求的会话选项。</para>
    /// </summary>
    internal UsbSessionOptions? SessionOptions { get; set; }

    /// <inheritdoc/>
    public override int DefaultTimeoutMs => SessionOptions?.DefaultTimeoutMs ?? IoTimeoutMs;

    /// <summary>
    /// Opens the control handle, reads the configuration descriptor the driver caches, then opens one handle for each
    /// bulk direction because the driver binds a single pipe per handle. Both pipes are reset once afterwards so a
    /// transfer the previous owner of the port left halted cannot fail the first command of this session.
    /// <para>打开控制句柄、读取驱动缓存的配置描述符，然后为每个批量方向各打开一个句柄，因为驱动为每个句柄只绑定一条管道。
    /// 随后复位一次两条管道，使端口上一任占用者留下的已停住传输不会让本次会话的第一条命令失败。</para>
    /// </summary>
    /// <returns><c>0</c> on success, otherwise the <see cref="Marshal.GetLastWin32Error"/> value of the failing step.
    /// <para>成功时为 <c>0</c>，否则为失败步骤的 <see cref="Marshal.GetLastWin32Error"/> 取值。</para></returns>
    public override int CreateHandle()
    {
        _controlHandle = new SafeFileHandle(SimpleCreateHandle(DevicePath), ownsHandle: true);
        if (_controlHandle.IsInvalid)
        {
            return Marshal.GetLastWin32Error();
        }

        var failure = ReadConfiguration();
        if (failure != 0)
        {
            DisposeHandles();
            return failure;
        }

        failure = OpenBulkPipes();
        if (failure != 0)
        {
            DisposeHandles();
            return failure;
        }

        ResetAllPipes();
        return 0;
    }

    /// <summary>
    /// Queries the driver for the device's configuration descriptor and records the interface it exposes.
    /// <para>向驱动查询设备的配置描述符，并记录其暴露的接口。</para>
    /// </summary>
    /// <returns><c>0</c> on success, otherwise the failing step's native error, or <c>-1</c> when the descriptor could not
    /// be parsed into an interface.
    /// <para>成功时为 <c>0</c>，否则为失败步骤的原生错误；描述符无法解析出接口时为 <c>-1</c>。</para>
    /// </returns>
    private int ReadConfiguration()
    {
        var buffer = new byte[ConfigurationDescriptorBufferBytes];
        if (!DeviceIoControl(_controlHandle.DangerousGetHandle(), IoGetConfigurationCode, null, 0, buffer,
                (uint)buffer.Length, out var returned, IntPtr.Zero))
        {
            return Marshal.GetLastWin32Error();
        }

        var length = (int)Math.Min(returned, (uint)buffer.Length);
        var interfaces = UsbConfigurationDescriptorParser.Parse(buffer.AsSpan(0, length));
        ApplyConfiguration(interfaces);
        if (CommTrace.IsEnabled(CommLogLevel.Debug))
        {
            CommTrace.Debug("usb",
                $"opened rockusb {DevicePath}: interface {InterfaceClass:X2}/{InterfaceSubClass:X2}/{InterfaceProtocol:X2}, " +
                $"bulk in 0x{_endpointIn:X2}/out 0x{_endpointOut:X2}; {interfaces.Count} interface(s) reported");
        }

        return interfaces.Count == 0 ? -1 : 0;
    }

    /// <summary>
    /// Opens the two pipe handles the bulk directions need. Each handle names the pipe the driver must bind, so a
    /// direction whose endpoint is not the pipe the driver's name selects is reported instead of silently reading the
    /// wrong pipe.
    /// <para>为两个批量方向打开管道句柄。每个句柄都指明驱动必须绑定的管道，因此当某个方向的端点不是驱动按名字选中的
    /// 管道时会被上报，而不是静默读写错误的管道。</para>
    /// </summary>
    /// <returns><c>0</c> on success, otherwise the failing step's native error.
    /// <para>成功时为 <c>0</c>，否则为失败步骤的原生错误。</para></returns>
    private int OpenBulkPipes()
    {
        var endpoints = Interfaces.Count > 0 ? Interfaces[0].Endpoints : [];
        if (SelectPipes(endpoints) is not { } pipes)
        {
            return ERROR_NOT_SUPPORTED_CODE;
        }

        _bulkInHandle = new SafeFileHandle(
            SimpleCreateHandle(DevicePath + "\\" + pipes.InPipeName, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                overlapped: true),
            ownsHandle: true);
        if (_bulkInHandle.IsInvalid)
        {
            return Marshal.GetLastWin32Error();
        }

        _bulkOutHandle = new SafeFileHandle(
            SimpleCreateHandle(DevicePath + "\\" + pipes.OutPipeName, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                overlapped: true),
            ownsHandle: true);
        if (_bulkOutHandle.IsInvalid)
        {
            return Marshal.GetLastWin32Error();
        }

        _endpointIn = pipes.EndpointIn;
        _endpointOut = pipes.EndpointOut;
        UsbTrace.LogFormatted(
            $"Rockusb: {DevicePath} bulk in 0x{_endpointIn:X2} on pipe '{pipes.InPipeName}', out 0x{_endpointOut:X2} on pipe '{pipes.OutPipeName}'.");
        return 0;
    }

    /// <summary>
    /// Picks the file-name suffix that makes the driver bind the wanted bulk endpoints. The driver reads the decimal
    /// number trailing a name and resolves <c>0</c> to the interface's first IN endpoint, <c>1</c> to its first OUT
    /// endpoint, and <c>2</c> upward to the pipe with that literal index, so a wanted endpoint that is not the first of
    /// its direction is only addressable when its index is <c>2</c> or higher.
    /// <para>选出能让驱动绑定目标批量端点的文件名后缀。驱动读取名字末尾的十进制数，把 <c>0</c> 解析为接口的第一条 IN
    /// 端点、<c>1</c> 解析为其第一条 OUT 端点、<c>2</c> 及以上解析为该序号的字面管道序号，因此目标端点若不是其方向上的
    /// 第一条，只有当它的序号不小于 <c>2</c> 时才可寻址。</para>
    /// </summary>
    /// <returns>The pipe-name pair, or <see langword="null"/> when the interface exposes no addressable bulk pair.
    /// <para>管道名对；接口没有可寻址的批量端点对时为 <see langword="null"/>。</para></returns>
    internal static RockusbPipeSelection? SelectPipes(IReadOnlyList<UsbEndpointInfo> endpoints)
    {
        var firstIn = FirstEndpointIndex(endpoints, isIn: true);
        var firstOut = FirstEndpointIndex(endpoints, isIn: false);
        var bulkIn = FirstBulkEndpointIndex(endpoints, isIn: true);
        var bulkOut = FirstBulkEndpointIndex(endpoints, isIn: false);
        if (bulkIn < 0 || bulkOut < 0)
        {
            return null;
        }

        var inName = PipeName(firstIn, bulkIn, isIn: true);
        var outName = PipeName(firstOut, bulkOut, isIn: false);
        return inName is null || outName is null
            ? null
            : new RockusbPipeSelection(inName, outName, endpoints[bulkIn].EndpointAddress,
                endpoints[bulkOut].EndpointAddress);
    }

    /// <summary>
    /// Maps one wanted endpoint index to the file-name suffix the driver resolves to it. The driver reads the number as a
    /// selector, not as an endpoint index: <c>0</c> and <c>1</c> mean "the first IN endpoint" and "the first OUT endpoint",
    /// and only <c>2</c> upward name a pipe by its index in the configuration, so a wanted endpoint that is the first of
    /// its direction always takes the direction's own selector whatever its index is.
    /// <para>把一个目标端点序号映射为驱动解析得到的文件名后缀。驱动把该数字当作选择器而非端点序号：<c>0</c> 与 <c>1</c>
    /// 分别表示「第一条 IN 端点」与「第一条 OUT 端点」，只有 <c>2</c> 及以上才按配置中的管道序号寻址，因此目标端点只要是
    /// 其方向上的第一条，无论其序号是多少都取该方向自己的选择器。</para>
    /// </summary>
    /// <param name="firstOfDirection">Index of the first endpoint in that direction.
    /// <para>该方向上第一条端点的序号。</para></param>
    /// <param name="wanted">Index of the endpoint to bind. <para>要绑定的端点序号。</para></param>
    /// <param name="isIn">Whether the wanted endpoint is device-to-host. <para>目标端点是否为设备到主机方向。</para></param>
    /// <returns>The suffix, or <see langword="null"/> when the driver cannot be made to bind that endpoint.
    /// <para>后缀；无法让驱动绑定该端点时为 <see langword="null"/>。</para></returns>
    private static string? PipeName(int firstOfDirection, int wanted, bool isIn) => wanted switch
    {
        _ when wanted == firstOfDirection => isIn ? FirstInPipeName : FirstOutPipeName,
        >= 2 and <= 5 => LiteralPipeNamePrefix + wanted.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
        _ => null
    };

    /// <summary>
    /// Returns the index of the first endpoint in one direction, or <c>-1</c> when the interface has none.
    /// <para>返回某方向上第一条端点的序号；接口没有时为 <c>-1</c>。</para>
    /// </summary>
    /// <param name="endpoints">The interface's endpoints in descriptor order.
    /// <para>接口的端点，按描述符顺序排列。</para></param>
    /// <param name="isIn">Whether a device-to-host endpoint is wanted.
    /// <para>是否要设备到主机的端点。</para></param>
    /// <returns>The index, or <c>-1</c>. <para>序号，或 <c>-1</c>。</para></returns>
    private static int FirstEndpointIndex(IReadOnlyList<UsbEndpointInfo> endpoints, bool isIn)
    {
        for (var index = 0; index < endpoints.Count; index++)
        {
            if (endpoints[index].IsIn == isIn)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Returns the index of the first bulk endpoint in one direction, or <c>-1</c> when the interface has none.
    /// <para>返回某方向上第一条批量端点的序号；接口没有时为 <c>-1</c>。</para>
    /// </summary>
    /// <param name="endpoints">The interface's endpoints in descriptor order.
    /// <para>接口的端点，按描述符顺序排列。</para></param>
    /// <param name="isIn">Whether a device-to-host endpoint is wanted.
    /// <para>是否要设备到主机的端点。</para></param>
    /// <returns>The index, or <c>-1</c>. <para>序号，或 <c>-1</c>。</para></returns>
    private static int FirstBulkEndpointIndex(IReadOnlyList<UsbEndpointInfo> endpoints, bool isIn)
    {
        for (var index = 0; index < endpoints.Count; index++)
        {
            if (endpoints[index].IsIn == isIn &&
                (endpoints[index].Attributes & TransferTypeMask) == BulkTransferType)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Selects the interface the driver configured and publishes its endpoints and class. <c>rockusb.sys</c> selects the
    /// configuration but copies only the first interface's pipe table, so only that interface is addressable.
    /// <para>选出驱动已配置的接口并公布其端点与接口类。<c>rockusb.sys</c> 会选择配置，但只复制第一个接口的管道表，
    /// 因此只有该接口可寻址。</para>
    /// </summary>
    /// <param name="interfaces">Interfaces parsed from the configuration descriptor.
    /// <para>从配置描述符解析出的接口。</para></param>
    private void ApplyConfiguration(List<UsbInterfaceInfo> interfaces)
    {
        EnumeratedInterfaces = interfaces;
        if (interfaces.Count == 0)
        {
            return;
        }

        var bound = interfaces[0];
        Interfaces =
        [
            new UsbInterfaceInfo
            {
                InterfaceNumber = bound.InterfaceNumber,
                Class = bound.Class,
                SubClass = bound.SubClass,
                Protocol = bound.Protocol,
                Endpoints = bound.Endpoints
            }
        ];
        InterfaceClass = bound.Class;
        InterfaceSubClass = bound.SubClass;
        InterfaceProtocol = bound.Protocol;
        InterfaceMetadataObserved = true;
    }

    /// <summary>
    /// Reports no serial number: <c>rockusb.sys</c> exposes no serial IOCTL, so the caller must take the serial from
    /// Windows' device instance id instead of guessing one here.
    /// <para>不报告序列号：<c>rockusb.sys</c> 没有序列号 IOCTL，调用方应从 Windows 的设备实例 ID 取值，而不是在此猜测。</para>
    /// </summary>
    /// <returns>Always <c>0</c>. <para>恒为 <c>0</c>。</para></returns>
    public override int GetSerialNumber() => 0;

    /// <summary>
    /// Sends one loader vendor request through the driver's per-index IOCTL. Only the boot download request the RKDL
    /// loader handshake uses is supported: the driver exposes <c>0x0471</c> and <c>0x0472</c> as host-to-device
    /// requests with the payload as the IOCTL input buffer, and offers no vendor IN transfer at all.
    /// <para>通过驱动的按索引 IOCTL 发送一个 loader 厂商请求。仅支持 RKDL loader 握手使用的下载请求：驱动把
    /// <c>0x0471</c> 与 <c>0x0472</c> 暴露为主机到设备的请求，负载作为 IOCTL 输入缓冲区，且完全不提供厂商 IN 传输。</para>
    /// </summary>
    /// <param name="setupPacket">The request to send. <para>要发送的请求。</para></param>
    /// <param name="buffer">Payload bytes, or <see langword="null"/> for a zero-length request.
    /// <para>负载字节；零长请求时为 <see langword="null"/>。</para></param>
    /// <param name="offset">Offset into <paramref name="buffer"/>. <para><paramref name="buffer"/> 内的偏移。</para></param>
    /// <param name="length">Payload byte count. <para>负载字节数。</para></param>
    /// <param name="timeoutMs">Ignored: the driver enforces its own five-second control timeout.
    /// <para>忽略：驱动自行执行五秒的控制传输超时。</para></param>
    /// <returns>The number of payload bytes the driver reports as transferred.
    /// <para>驱动报告的已传输负载字节数。</para></returns>
    /// <exception cref="UsbDeviceHandleClosedException">The device is closed.
    /// <para>设备已关闭。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is non-zero while <paramref name="buffer"/>
    /// is <see langword="null"/>, or the range exceeds <paramref name="buffer"/>.
    /// <para><paramref name="buffer"/> 为 <see langword="null"/> 而 <paramref name="length"/> 非零，或范围超出
    /// <paramref name="buffer"/>。</para></exception>
    /// <exception cref="NotSupportedException">The request is not one the driver exposes.
    /// <para>该请求不在驱动提供的范围内。</para></exception>
    /// <exception cref="Win32Exception">The driver rejected the request.
    /// <para>驱动拒绝了该请求。</para></exception>
    public override int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
    {
        if (_disposed || _controlHandle.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        if (buffer == null)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(length, 0);
        }
        else
        {
            ValidateBufferRange(buffer, offset, length);
        }

        if (buffer == null || setupPacket.RequestType != VendorRequestType ||
            setupPacket.Request != VendorRequestDownloadBoot || setupPacket.Value != 0)
        {
            throw new NotSupportedException(
                $"rockusb.sys exposes only host-to-device boot download requests (0x40/0x{VendorRequestDownloadBoot:X2}, wValue 0); " +
                $"request 0x{setupPacket.RequestType:X2}/0x{setupPacket.Request:X2} with wValue 0x{setupPacket.Value:X4} is unavailable.");
        }

        var ioControlCode = setupPacket.Index switch
        {
            WIndex471 => IoVendorRequest471Code,
            WIndex472 => IoVendorRequest472Code,
            _ => 0u
        };
        if (ioControlCode == 0)
        {
            throw new NotSupportedException(
                $"rockusb.sys forwards vendor requests for wIndex 0x{WIndex471:X4} and 0x{WIndex472:X4} only; " +
                $"wIndex 0x{setupPacket.Index:X4} is unavailable.");
        }

        var payload = length > 0 ? Marshal.AllocHGlobal(length) : IntPtr.Zero;
        try
        {
            if (length > 0)
            {
                Marshal.Copy(buffer, offset, payload, length);
            }

            if (!DeviceIoControl(_controlHandle.DangerousGetHandle(), ioControlCode, payload, (uint)length, null, 0,
                    out var returned, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return (int)Math.Min(returned, (uint)length);
        }
        finally
        {
            if (payload != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(payload);
            }
        }
    }

    /// <inheritdoc/>
    public override byte[] Read(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var buffer = new byte[length];
        var count = ReadInto(buffer, 0, length, IoTimeoutMs);
        if (count == length)
        {
            return buffer;
        }

        if (count == 0)
        {
            return [];
        }

        var result = new byte[count];
        Buffer.BlockCopy(buffer, 0, result, 0, count);
        return result;
    }

    /// <inheritdoc/>
    public override int ReadInto(byte[] buffer, int offset, int length) => ReadInto(buffer, offset, length, IoTimeoutMs);

    /// <summary>
    /// Resets both bulk pipes so a transfer the previous owner of the port left pending cannot poison the next one. The
    /// driver's reset-all IOCTL aborts and resets every pipe of the interface in one call, which is what the official tool
    /// issues before it retries a command; the per-pipe IOCTL is the fallback when the driver rejects that call. Both run
    /// under a budget because the driver's internal cancel wait has no bound, and a reset that does not return leaves the
    /// device reported as unusable instead of freezing the caller.
    /// <para>复位两条批量管道，避免端口上一任占用者留下的悬停传输影响下一次传输。驱动的复位全部管道 IOCTL 会在一次调用中
    /// 中止并复位该接口的所有管道，官方工具即在重试命令之前发出该调用；驱动拒绝该调用时回退到按管道复位的 IOCTL。两者都在
    /// 预算内执行，因为驱动内部的取消等待没有上限，未返回的复位会把设备上报为不可用，而不是让调用方一直挂住。</para>
    /// </summary>
    public override void Reset()
    {
        if (_disposed || _controlHandle.IsInvalid)
        {
            return;
        }

        if (ResetAllPipes())
        {
            return;
        }

        ResetPipe(_bulkInHandle, _endpointIn);
        ResetPipe(_bulkOutHandle, _endpointOut);
    }

    /// <summary>
    /// Aborts and resets every pipe of the interface through the driver's reset-all IOCTL.
    /// <para>通过驱动的复位全部管道 IOCTL 中止并复位该接口的所有管道。</para>
    /// </summary>
    /// <returns><see langword="true"/> when the driver completed the call within the budget.
    /// <para>驱动在预算内完成该调用时为 <see langword="true"/>。</para></returns>
    private bool ResetAllPipes()
    {
        var raw = _controlHandle.DangerousGetHandle();
        var input = new byte[1];
        var reset = Task.Run(() =>
            DeviceIoControl(raw, IoResetAllPipesCode, input, 1, null, 0, out _, IntPtr.Zero));

        if (reset.Wait(PipeResetBudgetMs))
        {
            if (reset.Result)
            {
                return true;
            }

            var error = Marshal.GetLastWin32Error();
            UsbTrace.LogFormatted(
                $"Rockusb: reset-all pipes on {DevicePath} was rejected with error {error}; falling back to per-pipe resets.");
            return false;
        }

        UsbTrace.LogFormatted(
            $"Rockusb: reset-all pipes on {DevicePath} did not return within {PipeResetBudgetMs} ms.");
        return false;
    }

    /// <summary>
    /// Sends one pipe reset on the handle bound to that pipe and reports when the driver does not answer in time.
    /// <para>在绑定该管道的句柄上发送一次管道复位，并在驱动未及时应答时上报。</para>
    /// </summary>
    /// <param name="handle">The pipe handle. <para>管道句柄。</para></param>
    /// <param name="endpoint">The endpoint address, for diagnostics. <para>端点地址，用于诊断。</para></param>
    private void ResetPipe(SafeFileHandle handle, byte endpoint)
    {
        if (handle.IsInvalid)
        {
            return;
        }

        var raw = handle.DangerousGetHandle();
        var completed = Task.Run(() => DeviceIoControl(raw, IoResetPipeCode, null, 0, null, 0, out _, IntPtr.Zero))
            .Wait(PipeResetBudgetMs);
        if (!completed)
        {
            UsbTrace.LogFormatted(
                $"Rockusb: pipe reset on endpoint 0x{endpoint:X2} of {DevicePath} did not return within {PipeResetBudgetMs} ms.");
        }
    }

    /// <inheritdoc/>
    protected override string BackendName => "rockusb";

    /// <inheritdoc/>
    protected override bool IsOpen => !_disposed && !_controlHandle.IsInvalid;

    /// <inheritdoc/>
    internal override bool IsHandleOpen => !_disposed && !_controlHandle.IsInvalid;

    /// <inheritdoc/>
    protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
        => BulkTransfer(_bulkInHandle, buffer, length, isWrite: false, timeoutMs);

    /// <inheritdoc/>
    protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
        => BulkTransfer(_bulkOutHandle, buffer, length, isWrite: true, timeoutMs);

    /// <summary>
    /// Runs one bulk transfer on the handle bound to the direction's pipe, waiting no longer than the caller's timeout
    /// and cancelling the call when that budget expires. The driver itself has no bulk timeout, so this is the only
    /// bound on a device that stops answering.
    /// <para>在绑定该方向管道的句柄上执行一次批量传输，最多等待调用方给定的超时，并在超时后取消该调用。驱动自身没有批量
    /// 超时，因此这是设备停止应答时唯一的时限。</para>
    /// </summary>
    /// <param name="handle">The pipe handle for the direction. <para>该方向的管道句柄。</para></param>
    /// <param name="buffer">The pinned data buffer. <para>已固定的数据缓冲区。</para></param>
    /// <param name="length">The byte count. <para>字节数。</para></param>
    /// <param name="isWrite">Whether this is a host-to-device transfer. <para>是否为主机到设备的传输。</para></param>
    /// <param name="timeoutMs">The budget in milliseconds. <para>毫秒预算。</para></param>
    /// <returns>The chunk outcome. <para>分块结果。</para></returns>
    private UsbChunkResult BulkTransfer(SafeFileHandle handle, IntPtr buffer, int length, bool isWrite, int timeoutMs)
    {
        if (_disposed || handle.IsInvalid)
        {
            return UsbChunkResult.Fatal(ERROR_INVALID_HANDLE_CODE);
        }

        var raw = handle.DangerousGetHandle();
        var ioEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        if (ioEvent == IntPtr.Zero)
        {
            return UsbChunkResult.Fatal(Marshal.GetLastWin32Error());
        }

        var overlapped = new OVERLAPPED { HEvent = ioEvent };
        try
        {
            var started = isWrite
                ? WriteFile(raw, buffer, (uint)length, out var written, ref overlapped)
                : ReadFile(raw, buffer, (uint)length, out written, ref overlapped);
            if (!started)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ERROR_IO_PENDING)
                {
                    return UsbChunkResult.Fatal(error);
                }

                if (WaitForSingleObject(ioEvent, (uint)Math.Max(timeoutMs, 0)) != WAIT_OBJECT_0)
                {
                    var address = (IntPtr)(&overlapped);
                    CancelIoEx(raw, address);
                    GetOverlappedResult(raw, ref overlapped, out _, wait: true);
                    return UsbChunkResult.Timeout(ERROR_IO_PENDING);
                }
            }

            return GetOverlappedResult(raw, ref overlapped, out var transferred, wait: false)
                ? UsbChunkResult.Success((int)transferred)
                : UsbChunkResult.Fatal(Marshal.GetLastWin32Error());
        }
        finally
        {
            CloseHandle(ioEvent);
        }
    }

    /// <inheritdoc/>
    protected override bool IsDisconnectionError(int nativeError)
        => nativeError is ERROR_DEVICE_NOT_CONNECTED or ERROR_NO_SUCH_DEVICE or ERROR_DEVICE_REMOVED;

    /// <inheritdoc/>
    protected override Exception CreateReadFatalException(int nativeError)
        => IsDisconnectionError(nativeError) ? base.CreateReadFatalException(nativeError) : new Win32Exception(nativeError);

    /// <inheritdoc/>
    protected override Exception CreateWriteFatalException(int nativeError)
        => IsDisconnectionError(nativeError) ? base.CreateWriteFatalException(nativeError) : new Win32Exception(nativeError);

    /// <inheritdoc/>
    public override long Write(byte[] data, int length) => Write(data, length, IoTimeoutMs);

    /// <summary>
    /// Closes the control and pipe handles.
    /// <para>关闭控制句柄与管道句柄。</para>
    /// </summary>
    private void DisposeHandles()
    {
        if (!_bulkOutHandle.IsInvalid)
        {
            _bulkOutHandle.Dispose();
        }

        if (!_bulkInHandle.IsInvalid)
        {
            _bulkInHandle.Dispose();
        }

        if (!_controlHandle.IsInvalid)
        {
            _controlHandle.Dispose();
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeHandles();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The pipe handles one Rockchip boot ROM interface needs: the file-name suffixes <c>rockusb.sys</c> resolves to the
/// wanted bulk endpoints, plus the endpoint addresses those pipes carry.
/// <para>一个瑞芯微 boot ROM 接口所需的管道句柄：<c>rockusb.sys</c> 解析到目标批量端点的文件名后缀，以及这些管道对应的
/// 端点地址。</para>
/// </summary>
/// <param name="InPipeName">Suffix selecting the device-to-host pipe, for example <c>PIPE00</c>.
/// <para>选取设备到主机管道的后缀，例如 <c>PIPE00</c>。</para></param>
/// <param name="OutPipeName">Suffix selecting the host-to-device pipe, for example <c>PIPE01</c>.
/// <para>选取主机到设备管道的后缀，例如 <c>PIPE01</c>。</para></param>
/// <param name="EndpointIn">The IN endpoint address the IN pipe carries. <para>IN 管道承载的端点地址。</para></param>
/// <param name="EndpointOut">The OUT endpoint address the OUT pipe carries. <para>OUT 管道承载的端点地址。</para></param>
internal sealed record RockusbPipeSelection(string InPipeName, string OutPipeName, byte EndpointIn, byte EndpointOut);