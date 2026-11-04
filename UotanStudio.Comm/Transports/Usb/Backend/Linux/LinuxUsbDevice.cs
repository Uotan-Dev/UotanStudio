using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using static UotanStudio.Comm.Transports.Usb.Backend.Linux.LinuxUsbAPI;

namespace UotanStudio.Comm.Transports.Usb.Backend.Linux;

internal class LinuxUsbDevice : UsbDevice
{
    private const int PlatformDefaultTimeoutMs = UsbTransferPolicies.DefaultTimeoutMs;

    /// <summary>
    /// Sleep interval between REAPURBNDELAY attempts while confirming a discarded URB.
    /// <para>确认被丢弃 URB 期间两次 REAPURBNDELAY 尝试之间的休眠间隔。</para>
    /// </summary>
    private const int UrbDiscardConfirmIntervalMs = 1;

    /// <summary>
    /// Total budget for confirming a discarded URB; past it the pinned handles are intentionally
    /// leaked instead of being freed while the kernel may still reference them.
    /// <para>确认被丢弃 URB 的总时间预算；超过后有意泄漏固定句柄，而不是在内核仍可能引用时释放。</para>
    /// </summary>
    private const int UrbDiscardConfirmTimeoutMs = 5000;

    /// <summary>
    /// Maximum duration of one poll slice, bounding how long a wait can block before it observes its own
    /// completion (which another thread may have reaped and routed), a finite deadline, or cancellation.
    /// <para>单个 poll 切片的最大时长，限制一次等待在观察自身完成（可能已被另一线程回收并路由）、有限期限或
    /// 取消之前可以阻塞多久。</para>
    /// </summary>
    private const int UrbPollSliceMs = 100;

    private readonly LinuxUsbFd _fd = new();

    /// <summary>
    /// Kernel completion of one URB: the status the kernel wrote back (0 = success, otherwise -errno) and the
    /// number of bytes it transferred.
    /// <para>单个 URB 的内核完成结果：内核写回的状态（0 表示成功，否则为 -errno）与其传输的字节数。</para>
    /// </summary>
    private readonly record struct UrbOutcome(int Status, int ActualLength);

    /// <summary>
    /// Result of one bounded usbfs poll slice: the poll return value, the revents the kernel reported and the
    /// errno captured when the poll failed.
    /// <para>单个有界 usbfs poll 切片的结果：poll 返回值、内核报告的 revents，以及 poll 失败时捕获的 errno。</para>
    /// </summary>
    private readonly record struct PollOutcome(int Result, short Revents, int Errno);

    /// <summary>
    /// Serializes the shared completion table. usbfs hands out whatever URB has completed on the file
    /// descriptor, so a reader and a writer can each reap the other's URB; every completed URB is routed to
    /// the transfer that submitted it instead of belonging to whichever thread reaped it.
    /// <para>串行化共享的完成表。usbfs 会把该文件描述符上任意已完成的 URB 交出，因此读线程与写线程可能各自
    /// 回收对方的 URB；每个完成的 URB 都按其提交者路由，而不是归回收它的线程所有。</para>
    /// </summary>
    private readonly Lock _urbSync = new();

    private readonly Dictionary<IntPtr, UrbOutcome> _urbCompletions = [];

    private readonly Dictionary<IntPtr, TaskCompletionSource> _urbWaiters = [];

    /// <summary>
    /// Cached delegate for one full-slice URB poll, created once per device so the slice wait does not
    /// allocate a fresh closure for every poll slice.
    /// <para>单个完整 poll 切片使用的缓存委托；每个设备只创建一次，使逐片等待不必为每个切片分配新闭包。</para>
    /// </summary>
    private readonly Func<PollOutcome> _pollUrbFullSlice;

    /// <summary>
    /// Initializes the cached full-slice poll delegate.
    /// <para>初始化缓存的完整切片 poll 委托。</para>
    /// </summary>
    public LinuxUsbDevice()
    {
        _pollUrbFullSlice = PollUrbFullSlice;
    }

    private int Fd => (int)_fd.DangerousGetHandle();

    public byte Ep_in { get; set; }
    public byte Ep_out { get; set; }
    public int InterfaceId { get; set; }
    public byte ISerialNumber { get; set; }

    /// <summary>
    /// Additional interfaces to claim in addition to <see cref="InterfaceId"/> (e.g. the
    /// CDC-ACM control interface next to the data interface). Each is claimed with the same
    /// detach-and-retry fallback as the primary interface.
    /// <para>除 <see cref="InterfaceId"/> 外还需要声明的附加接口（例如数据接口旁边的
    /// CDC-ACM 控制接口）。每个接口都使用与主接口相同的分离并重试回退。</para>
    /// </summary>
    public IReadOnlyList<byte> ClaimedInterfaceIds { get; set; } = [];

    private readonly List<byte> _claimedInterfaces = [];

    public override byte EndpointIn => Ep_in;
    public override byte EndpointOut => Ep_out;

    public override int DefaultTimeoutMs => PlatformDefaultTimeoutMs;

    /// <summary>
    /// Opens the usbfs node, anchors the physical port identity and claims the requested interfaces. On a failed claim, every interface already claimed by this call is released before the descriptor is closed, and the errno is captured immediately so it cannot be overwritten.
    /// <para>打开 usbfs 节点、锚定物理端口身份并申请所需接口。申请失败时，先释放本次已申请的全部接口再关闭描述符，并立即捕获 errno 以防被覆盖。</para>
    /// </summary>
    /// <returns><c>0</c> on success; otherwise the failed claim's errno or <c>-1</c>.
    /// <para>成功返回 <c>0</c>；否则返回失败声明的 errno 或 <c>-1</c>。</para></returns>
    public override int CreateHandle()
    {
        var fd = open(DevicePath, O_RDWR | O_CLOEXEC);
        if (fd < 0)
        {
            var err = Marshal.GetLastWin32Error();
            if (err == EACCES)
            {
                LinuxUsbFinder.ReportPermissionDenied(DevicePath);
            }
            _fd.SetFd(fd);
            return -1;
        }
        _fd.SetFd(fd);

        var physicalPortPath = LinuxUsbPortIdentity.Observe(fd, DevicePath);
        if (!LinuxUsbPortIdentity.Matches(PhysicalPortPath, physicalPortPath))
        {
            _fd.Dispose();
            return -1;
        }
        PhysicalPortPath = physicalPortPath;

        var interfacesToClaim = new List<byte>(ClaimedInterfaceIds);
        if (InterfaceId is >= 0 and <= byte.MaxValue && !interfacesToClaim.Contains((byte)InterfaceId))
        {
            interfacesToClaim.Insert(0, (byte)InterfaceId);
        }

        foreach (var ifcByte in interfacesToClaim)
        {
            if (!ClaimInterface(ifcByte))
            {
                var err = Marshal.GetLastWin32Error();
                ReleaseClaimedInterfaces();
                _fd.Dispose();
                return err;
            }
        }

        GetSerialNumber();
        return 0;
    }

    /// <summary>
    /// Claims a single interface with a detach-and-retry fallback for udev-rebound drivers.
    /// <para>使用分离并重试回退声明单个接口，以应对 udev 重新绑定驱动。</para>
    /// </summary>
    /// <param name="interfaceId">The interface number to claim. <para>要声明的接口编号。</para></param>
    /// <returns><c>true</c> when claimed; otherwise <c>false</c>. <para>声明成功返回 <c>true</c>；否则返回 <c>false</c>。</para></returns>
    private bool ClaimInterface(byte interfaceId)
    {
        int ifc = interfaceId;
        var n = ioctl(Fd, USBDEVFS_CLAIMINTERFACE, ref ifc);
        if (n != 0)
        {
            ioctl(Fd, USBDEVFS_DISCONNECT, ref ifc);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                n = ioctl(Fd, USBDEVFS_CLAIMINTERFACE, ref ifc);
                if (n == 0)
                {
                    break;
                }

                Thread.Sleep(50);
            }
        }

        if (n != 0)
        {
            var err = Marshal.GetLastWin32Error();
            if (err == EBUSY)
            {
                LinuxUsbFinder.ReportBusy(DevicePath);
            }
            return false;
        }

        _claimedInterfaces.Add(interfaceId);
        return true;
    }

    /// <summary>
    /// Issues USBDEVFS_RESET and then re-claims every interface this descriptor had claimed. usbfs may drop
    /// the claims across a reset; re-claiming is idempotent for claims the kernel kept and restores the ones
    /// it dropped, so the same session remains usable for the caller's retry after a device-level reset.
    /// <para>发起 USBDEVFS_RESET，然后重新申请本描述符曾申请的全部接口。usbfs 可能在复位时丢弃申请；重新申请
    /// 对内核保留的申请是幂等的，对被丢弃的则恢复它们，使同一会话在设备级复位后仍可供调用方重试。</para>
    /// </summary>
    /// <exception cref="IOException">An interface could not be re-claimed after a successful reset; the session
    /// is no longer usable.
    /// <para>复位成功后某个接口无法重新申请；会话已不可用。</para></exception>
    public override void Reset()
    {
        if (_fd.IsInvalid)
        {
            return;
        }

        if (ioctl(Fd, USBDEVFS_RESET, IntPtr.Zero) != 0)
        {
            var err = Marshal.GetLastWin32Error();
            UsbTrace.Log($"LinuxUsbDevice: USBDEVFS_RESET failed on '{DevicePath}' (errno {err}); the session is unchanged.");
            return;
        }

        foreach (var interfaceId in _claimedInterfaces.ToArray())
        {
            int ifc = interfaceId;
            if (ioctl(Fd, USBDEVFS_CLAIMINTERFACE, ref ifc) != 0)
            {
                var err = Marshal.GetLastWin32Error();
                UsbTrace.LogError($"LinuxUsbDevice: re-claim of interface {interfaceId} after USBDEVFS_RESET failed (errno {err}); the session is no longer usable.");
                throw new IOException($"USB interface {interfaceId} could not be re-claimed after reset (errno {err}).");
            }
        }
    }

    /// <summary>
    /// Selects an interface alternate setting through USBDEVFS_SETINTERFACE so the kernel's interface state
    /// matches what the device was told.
    /// <para>经 USBDEVFS_SETINTERFACE 选择接口备用设置，使内核的接口状态与设备收到的一致。</para>
    /// </summary>
    /// <param name="interfaceNumber">The interface number to reconfigure. <para>要重配置的接口号。</para></param>
    /// <param name="altSetting">The alternate setting to activate. <para>要激活的备用设置。</para></param>
    /// <exception cref="UsbDeviceHandleClosedException">The handle has been closed. <para>句柄已关闭。</para></exception>
    /// <exception cref="UsbDeviceDisconnectedException">The device disappeared. <para>设备已消失。</para></exception>
    /// <exception cref="IOException">The kernel rejected the request. <para>内核拒绝了该请求。</para></exception>
    public override void SetInterfaceAltSetting(byte interfaceNumber, byte altSetting)
    {
        if (_fd.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        var request = new Usbdevfs_setinterface { Interface = interfaceNumber, Altsetting = altSetting };
        if (ioctl(Fd, USBDEVFS_SETINTERFACE, ref request) != 0)
        {
            var err = Marshal.GetLastWin32Error();
            throw err is ENODEV or ESHUTDOWN or EPROTO
                ? new UsbDeviceDisconnectedException($"USB SET_INTERFACE failed: device disconnected (error: {err}).", err)
                : new IOException($"USB SET_INTERFACE failed with errno {err}.");
        }
    }

    /// <summary>
    /// Activates a device configuration through USBDEVFS_SETCONFIGURATION. The kernel refuses the request
    /// while interfaces of this descriptor are still claimed, so release them first.
    /// <para>经 USBDEVFS_SETCONFIGURATION 激活设备配置。本描述符仍申请着接口时内核会拒绝，必须先释放。</para>
    /// </summary>
    /// <param name="configuration">The configuration value to activate. <para>要激活的配置值。</para></param>
    /// <exception cref="UsbDeviceHandleClosedException">The handle has been closed. <para>句柄已关闭。</para></exception>
    /// <exception cref="UsbDeviceDisconnectedException">The device disappeared. <para>设备已消失。</para></exception>
    /// <exception cref="IOException">The kernel rejected the request. <para>内核拒绝了该请求。</para></exception>
    public override void SetConfiguration(byte configuration)
    {
        if (_fd.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        int value = configuration;
        if (ioctl(Fd, USBDEVFS_SETCONFIGURATION, ref value) != 0)
        {
            var err = Marshal.GetLastWin32Error();
            throw err is ENODEV or ESHUTDOWN or EPROTO
                ? new UsbDeviceDisconnectedException($"USB SET_CONFIGURATION failed: device disconnected (error: {err}).", err)
                : new IOException($"USB SET_CONFIGURATION failed with errno {err}.");
        }
    }

    /// <summary>Executes a control transfer through the usbfs control ioctl, validating the length before any handle inspection because <c>WLength</c> is a 16-bit field that would otherwise truncate silently. The managed infinite-timeout sentinel is mapped to the kernel's zero "wait forever" encoding.
    /// <para>通过 usbfs 控制 ioctl 执行控制传输；因 <c>WLength</c> 为 16 位字段、否则会静默截断，长度在任何句柄检查之前校验。托管的无限超时哨兵值映射为内核的 0（"永久等待"）编码。</para></summary>
    /// <param name="setupPacket">The setup packet for the control request.
    /// <para>控制请求的 Setup 包。</para></param>
    /// <param name="buffer">The data buffer, or <c>null</c> for zero-length transfers.
    /// <para>数据缓冲区；零长度传输时为 <c>null</c>。</para></param>
    /// <param name="offset">The byte offset within the buffer.<para>缓冲区内的字节偏移量。</para></param>
    /// <param name="length">The number of bytes to transfer, at most 65535.
    /// <para>要传输的字节数，最大 65535。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds.<para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes transferred.<para>已传输的字节数。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="length"/> exceeds the 16-bit <c>WLength</c> range.
    /// <para>当 <paramref name="length"/> 超过 16 位 <c>WLength</c> 范围时抛出。</para></exception>
    /// <exception cref="UsbDeviceHandleClosedException">Thrown when the device handle is closed.
    /// <para>当设备句柄已关闭时抛出。</para></exception>
    /// <exception cref="IOException">Thrown when the control ioctl fails.
    /// <para>当控制 ioctl 失败时抛出。</para></exception>
    public override int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
    {
        if (length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "USB control transfers are limited to 65535 bytes (16-bit WLength).");
        }

        if (_fd.IsInvalid)
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

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var ctrl = new Usbdevfs_ctrltransfer
        {
            BRequestType = setupPacket.RequestType,
            BRequest = setupPacket.Request,
            WValue = setupPacket.Value,
            WIndex = setupPacket.Index,
            WLength = (ushort)length,
            Timeout = ConvertTimeoutMs(effectiveTimeoutMs)
        };

        var isInDirection = (setupPacket.RequestType & 0x80) != 0;

        if (length > 0)
        {
            if (buffer != null && offset == 0 && length == buffer.Length)
            {
                var pinnedHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    ctrl.Data = pinnedHandle.AddrOfPinnedObject();
                    var result = ioctl(Fd, IntPtr.Size == 8 ? USBDEVFS_CONTROL_X86_64 : USBDEVFS_CONTROL_X86, ref ctrl);
                    return result < 0 ? throw new IOException($"USB control transfer failed with error: {Marshal.GetLastWin32Error()}") : result;
                }
                finally
                {
                    pinnedHandle.Free();
                }
            }

            var transferBuffer = new byte[length];
            if (!isInDirection && buffer != null)
            {
                Buffer.BlockCopy(buffer, offset, transferBuffer, 0, length);
            }

            var transferHandle = GCHandle.Alloc(transferBuffer, GCHandleType.Pinned);
            try
            {
                ctrl.Data = transferHandle.AddrOfPinnedObject();
                var result = ioctl(Fd, IntPtr.Size == 8 ? USBDEVFS_CONTROL_X86_64 : USBDEVFS_CONTROL_X86, ref ctrl);
                if (result < 0)
                {
                    throw new IOException($"USB control transfer failed with error: {Marshal.GetLastWin32Error()}");
                }

                if (isInDirection && buffer != null)
                {
                    Buffer.BlockCopy(transferBuffer, 0, buffer, offset, Math.Min(result, length));
                }

                return result;
            }
            finally
            {
                transferHandle.Free();
            }
        }

        ctrl.Data = IntPtr.Zero;
        var zeroResult = ioctl(Fd, IntPtr.Size == 8 ? USBDEVFS_CONTROL_X86_64 : USBDEVFS_CONTROL_X86, ref ctrl);
        return zeroResult < 0 ? throw new IOException($"USB control transfer failed with error: {Marshal.GetLastWin32Error()}") : zeroResult;
    }

    /// <summary>
    /// Releases every interface this descriptor still records as claimed and clears the record.
    /// Shared by <see cref="Dispose"/> and the <see cref="CreateHandle"/> rollback path so a
    /// partially claimed device releases its claims explicitly instead of relying on the implicit
    /// teardown performed by close().
    /// <para>释放本描述符仍记录为已声明的全部接口并清空记录。<see cref="Dispose"/> 与
    /// <see cref="CreateHandle"/> 的回滚路径共用，使部分声明失败的设备显式释放声明，而不是仅依赖
    /// close() 的隐式拆卸。</para>
    /// </summary>
    private void ReleaseClaimedInterfaces()
    {
        foreach (var interfaceNumber in _claimedInterfaces)
        {
            int interfaceId = interfaceNumber;
            ioctl(Fd, USBDEVFS_RELEASEINTERFACE, ref interfaceId);
        }

        _claimedInterfaces.Clear();
    }

    /// <summary>
    /// Maps a managed timeout to the kernel usbfs timeout encoding used by the control and
    /// synchronous bulk ioctl structures, where <c>0</c> means "wait forever". The managed
    /// infinite sentinel must not be cast directly: <c>(uint)(-1)</c> would become roughly 49.7
    /// days instead of an unbounded wait.
    /// <para>将托管超时映射为控制与同步 bulk ioctl 结构使用的内核 usbfs 超时编码，其中 <c>0</c>
    /// 表示“永久等待”。托管的无限超时哨兵值不得直接强转：<c>(uint)(-1)</c> 会变成约 49.7 天而不是
    /// 无限等待。</para>
    /// </summary>
    /// <param name="timeoutMs">The timeout in milliseconds, or <see cref="UsbTransferPolicies.InfiniteTimeoutMs"/>. <para>毫秒超时，或 <see cref="UsbTransferPolicies.InfiniteTimeoutMs"/>。</para></param>
    /// <returns><c>0</c> for the infinite sentinel; otherwise the value widened to unsigned. <para>无限哨兵返回 <c>0</c>；其余值拓宽为无符号数。</para></returns>
    internal static uint ConvertTimeoutMs(int timeoutMs)
        => timeoutMs == UsbTransferPolicies.InfiniteTimeoutMs ? 0u : (uint)timeoutMs;

    public override void Dispose()
    {
        if (!_fd.IsInvalid)
        {
            ReleaseClaimedInterfaces();
            _fd.Dispose();
        }
    }

    public override int GetSerialNumber()
    {
        if (ISerialNumber == 0)
        {
            return -1;
        }

        var ctrl = new Usbdevfs_ctrltransfer();
        var descriptor = new byte[256];
        var handle = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var ctrlCode = (IntPtr.Size == 8) ? USBDEVFS_CONTROL_X86_64 : USBDEVFS_CONTROL_X86;
            var ctrlCodePtr = (UIntPtr)ctrlCode;

            ctrl.BRequestType = 0x80;
            ctrl.BRequest = 0x06;
            ctrl.WValue = 0x03 << 8;
            ctrl.WIndex = 0;
            ctrl.WLength = (ushort)descriptor.Length;
            ctrl.Data = handle.AddrOfPinnedObject();
            ctrl.Timeout = 1000;

            var n = ioctl(Fd, ctrlCodePtr, ref ctrl);
            var languageCount = 0;
            var languages = new ushort[128];
            if (n > 2)
            {
                languageCount = (n - 2) / 2;
                for (var i = 0; i < languageCount; i++)
                {
                    languages[i] = (ushort)(descriptor[2 + (i * 2)] | (descriptor[3 + (i * 2)] << 8));
                }
            }
            else
            {
                languages[0] = 0x0409;
                languageCount = 1;
            }

            for (var i = 0; i < languageCount; i++)
            {
                ctrl.BRequestType = 0x80;
                ctrl.BRequest = 0x06;
                ctrl.WValue = (ushort)((0x03 << 8) | ISerialNumber);
                ctrl.WIndex = languages[i];
                ctrl.WLength = (ushort)descriptor.Length;
                ctrl.Data = handle.AddrOfPinnedObject();
                ctrl.Timeout = 1000;

                n = ioctl(Fd, ctrlCodePtr, ref ctrl);
                if (n > 2)
                {
                    SerialNumber = UsbStringDescriptor.Decode(descriptor, n);
                    return 0;
                }
            }
            return -1;
        }
        finally
        {
            handle.Free();
        }
    }

    public override byte[] Read(int length) => Read(length, PlatformDefaultTimeoutMs);

    public override byte[] Read(int length, int timeoutMs)
    {
        if (length <= 0)
        {
            return [];
        }

        var buffer = new byte[length];
        var count = ReadInto(buffer, 0, length, timeoutMs);
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

    public override int ReadInto(byte[] buffer, int offset, int length) => ReadInto(buffer, offset, length, PlatformDefaultTimeoutMs);

    protected override string BackendName => "linux-usbfs";

    protected override bool IsOpen => !_fd.IsInvalid;

    /// <inheritdoc/>
    internal override bool IsHandleOpen => !_fd.IsInvalid;

    protected override bool IsDisconnectionError(int nativeError)
        => nativeError is ENODEV or ESHUTDOWN or EPROTO;

    protected override int MaxChunkSize => UsbTransferPolicies.LinuxUsbFsMaxBulkSize;

    /// <summary>
    /// Clears a halted endpoint pipe with USBDEVFS_CLEAR_HALT so a single STALL does not wedge the pipe permanently.
    /// <para>通过 USBDEVFS_CLEAR_HALT 清除停滞的端点管道，避免一次 STALL 永久楔死管道。</para>
    /// This is a best-effort recovery: failures are logged and swallowed, and the caller keeps its
    /// original status handling.
    /// <para>这是尽力而为的恢复手段：失败仅记录日志并被吞掉，调用方保持原有的状态处理。</para>
    /// </summary>
    /// <param name="endpoint">The endpoint address whose halt condition is cleared, direction included (e.g. <see cref="Ep_in"/> or <see cref="Ep_out"/>).
    /// <para>要清除停滞状态的端点地址（含方向，如 <see cref="Ep_in"/> 或 <see cref="Ep_out"/>）。</para></param>
    internal void ClearHalt(byte endpoint)
    {
        var endpointValue = (int)endpoint;
        if (ioctl(Fd, USBDEVFS_CLEAR_HALT, ref endpointValue) != 0)
        {
            UsbTrace.Log($"LinuxUsbDevice: USBDEVFS_CLEAR_HALT failed on endpoint 0x{endpoint:X2} (errno {Marshal.GetLastWin32Error()}).");
        }
    }

    protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
    {
        var bulk = new Usbdevfs_bulktransfer
        {
            Ep = Ep_in,
            Len = (uint)length,
            Timeout = ConvertTimeoutMs(timeoutMs),
            Data = buffer
        };

        var bulkCode = (IntPtr.Size == 8) ? USBDEVFS_BULK_X86_64 : USBDEVFS_BULK_X86;
        var bulkCodePtr = (UIntPtr)bulkCode;
        var retry = 0;
        var retryCount = 0;
        var retryPolicy = UsbTransferPolicies.DefaultRetryPolicy;
        int n;
        do
        {
            n = ioctl(Fd, bulkCodePtr, ref bulk);
            if (n < 0)
            {
                var err = Marshal.GetLastWin32Error();
                if (err is EINTR or EAGAIN)
                {
                    continue;
                }

                if (err == EPIPE)
                {
                    ClearHalt(Ep_in);
                }

                if (err == ETIMEDOUT)
                {
                    return UsbChunkResult.Timeout(err);
                }

                if (err is ENODEV or ESHUTDOWN or EPROTO)
                {
                    return UsbChunkResult.Fatal(err);
                }

                if (++retry > retryPolicy.MaxRetries)
                {
                    return UsbChunkResult.Error(err);
                }

                retryCount++;
                Thread.Sleep(retryPolicy.RetryDelayMs);
            }
        } while (n < 0);
        return new UsbChunkResult(UsbChunkStatus.Success, n, 0, retryCount);
    }

    protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
    {
        var bulk = new Usbdevfs_bulktransfer
        {
            Ep = Ep_out,
            Len = (uint)length,
            Timeout = ConvertTimeoutMs(timeoutMs),
            Data = buffer
        };

        var bulkCode = (IntPtr.Size == 8) ? USBDEVFS_BULK_X86_64 : USBDEVFS_BULK_X86;
        var bulkCodePtr = (UIntPtr)bulkCode;
        var retry = 0;
        var retryCount = 0;
        var retryPolicy = UsbTransferPolicies.DefaultRetryPolicy;
        int n;
        do
        {
            n = ioctl(Fd, bulkCodePtr, ref bulk);
            if (n < 0)
            {
                var err = Marshal.GetLastWin32Error();
                if (err is EINTR or EAGAIN)
                {
                    continue;
                }

                if (err == EPIPE)
                {
                    ClearHalt(Ep_out);
                }

                if (err == ETIMEDOUT)
                {
                    return UsbChunkResult.Timeout(err);
                }

                if (err is ENODEV or ESHUTDOWN or EPROTO)
                {
                    return UsbChunkResult.Fatal(err);
                }

                if (++retry > retryPolicy.MaxRetries)
                {
                    return UsbChunkResult.Error(err);
                }

                retryCount++;
                Thread.Sleep(retryPolicy.RetryDelayMs);
            }
        } while (n < 0);
        return new UsbChunkResult(UsbChunkStatus.Success, n, 0, retryCount);
    }

    public override long Write(byte[] data, int length) => Write(data, length, PlatformDefaultTimeoutMs);

    public override void WriteZlp(int timeoutMs)
    {
        if (_fd.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        var bulk = new Usbdevfs_bulktransfer
        {
            Ep = Ep_out,
            Len = 0,
            Timeout = ConvertTimeoutMs(timeoutMs),
            Data = IntPtr.Zero
        };

        var bulkCode = (IntPtr.Size == 8) ? USBDEVFS_BULK_X86_64 : USBDEVFS_BULK_X86;
        var n = ioctl(Fd, bulkCode, ref bulk);
        if (n < 0)
        {
            var err = Marshal.GetLastWin32Error();
            if (err is ENODEV or ESHUTDOWN or EPROTO)
            {
                throw new UsbDeviceDisconnectedException("USB device disconnected during zero-length write.", err);
            }
            throw new IOException($"USB zero-length write failed with error: {err}");
        }
    }

    public override UsbReadResult ReadInterrupt(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs)
        => ReadInterruptAsync(endpointAddress, buffer, offset, length, timeoutMs).GetAwaiter().GetResult();

    public override long WriteInterrupt(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs)
        => WriteInterruptAsync(endpointAddress, data, offset, length, timeoutMs).GetAwaiter().GetResult();

    protected override async Task<UsbChunkResult> ReadChunkAsync(IntPtr buffer, int length, int timeoutMs, CancellationToken cancellationToken)
    {
        if (_fd.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var outcome = await SubmitUrbAsync(Ep_in, buffer, length, effectiveTimeoutMs, cancellationToken, USBDEVFS_URB_TYPE_BULK).ConfigureAwait(false);
        return MapOutcome(outcome);
    }

    protected override async Task<UsbChunkResult> WriteChunkAsync(IntPtr buffer, int length, int timeoutMs, CancellationToken cancellationToken)
    {
        if (_fd.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var outcome = await SubmitUrbAsync(Ep_out, buffer, length, effectiveTimeoutMs, cancellationToken, USBDEVFS_URB_TYPE_BULK).ConfigureAwait(false);
        return MapOutcome(outcome);
    }

    /// <summary>
    /// Maps one URB completion to the chunk protocol. A success with no bytes means the wait ended without a
    /// payload (deadline or an idle device); a non-zero kernel status other than a disconnection becomes
    /// <c>Error</c> so a STALL or protocol failure is not reported as an idle read.
    /// <para>把一次 URB 完成映射为分块协议结果。成功但零字节表示等待结束而没有负载（到期或设备空闲）；除断连
    /// 以外的非零内核状态映射为 <c>Error</c>，使 STALL 或协议错误不会被当作空闲读取上报。</para>
    /// </summary>
    /// <param name="outcome">The routed URB outcome. <para>已路由的 URB 结果。</para></param>
    /// <returns>The chunk result. <para>分块结果。</para></returns>
    /// <exception cref="UsbDeviceDisconnectedException">The kernel reported a disconnection-class status.
    /// <para>内核报告了断连类状态。</para></exception>
    private static UsbChunkResult MapOutcome(UrbOutcome outcome)
    {
        if (outcome.Status == 0)
        {
            return outcome.ActualLength > 0
                ? UsbChunkResult.Success(outcome.ActualLength)
                : UsbChunkResult.Timeout(ETIMEDOUT);
        }

        var err = -outcome.Status;
        return err is ENODEV or ESHUTDOWN or EPROTO
            ? throw new UsbDeviceDisconnectedException($"USB async transfer failed: device disconnected (error: {err}).", err)
            : UsbChunkResult.Error(err);
    }

    public override async Task<UsbReadResult> ReadInterruptAsync(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        if (_fd.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }
        if (length <= 0)
        {
            return new UsbReadResult(0, false, false);
        }

        ValidateBufferRange(buffer, offset, length);

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset);
        var outcome = await SubmitUrbAsync(endpointAddress, ptr, length, effectiveTimeoutMs, cancellationToken, USBDEVFS_URB_TYPE_INTERRUPT, handle).ConfigureAwait(false);
        if (outcome.Status != 0)
        {
            var err = -outcome.Status;
            if (err is ENODEV or ESHUTDOWN or EPROTO)
            {
                throw new UsbDeviceDisconnectedException($"USB async interrupt read failed: device disconnected (error: {err}).", err);
            }
        }

        return outcome.ActualLength == 0
            ? new UsbReadResult(0, isTimeout: true, isShortPacket: false)
            : new UsbReadResult(outcome.ActualLength, isTimeout: false, isShortPacket: outcome.ActualLength < length);
    }

    public override async Task<long> WriteInterruptAsync(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        if (_fd.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }
        ValidateWriteData(data, offset, length);
        if (length == 0)
        {
            return 0;
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset);
        var outcome = await SubmitUrbAsync(endpointAddress, ptr, length, effectiveTimeoutMs, cancellationToken, USBDEVFS_URB_TYPE_INTERRUPT, handle).ConfigureAwait(false);
        if (outcome.Status != 0)
        {
            var err = -outcome.Status;
            if (err is ENODEV or ESHUTDOWN or EPROTO)
            {
                throw new UsbDeviceDisconnectedException($"USB async interrupt write failed: device disconnected (error: {err}).", err);
            }
        }

        return outcome.ActualLength;
    }

    private async Task<UrbOutcome> SubmitUrbAsync(byte endpoint, byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken, byte urbType)
    {
        var bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        var ptr = new IntPtr(bufferHandle.AddrOfPinnedObject().ToInt64() + offset);
        return await SubmitUrbAsync(endpoint, ptr, length, timeoutMs, cancellationToken, urbType, bufferHandle).ConfigureAwait(false);
    }

    /// <summary>
    /// Submits a URB against an already-pinned buffer pointer, waits in bounded poll slices and routes its
    /// completion back to this call. usbfs hands a completed URB to whichever thread reaps first, so a reader
    /// can reap the writer's completion; every reaped URB is filed by pointer and the owning transfer picks it
    /// up instead of discarding it. The pinned URB structure and any handed-over buffer handle are freed only
    /// once the kernel has provably released the URB (routed completion or device disappearance); any other
    /// exit deliberately leaks them, because freeing memory the kernel may still write to corrupts the heap.
    /// <para>针对已固定的缓冲区指针提交 URB，以有界的 poll 切片等待并把完成结果路由回本次调用。usbfs 会把完成
    /// 的 URB 交给先回收的线程，读线程可能回收写线程的完成；每个被回收的 URB 都按指针归档并由所属传输自行取走，
    /// 而不是被丢弃。只有在内核可证明已释放 URB（完成已路由或设备消失）后才释放固定的 URB 结构与移交的缓冲区
    /// 句柄；其余退出路径有意泄漏它们——释放内核仍可能写入的内存会破坏堆。</para>
    /// </summary>
    /// <remarks>
    /// The wake-up may belong to the other direction on this descriptor; file every completed URB
    /// and keep waiting for our own completion.
    /// <para>唤醒可能属于该描述符上的另一个方向；归档每个已完成的 URB，并继续等待属于本次调用的完成。</para>
    /// </remarks>
    /// <param name="endpoint">The endpoint address for the URB. <para>URB 的端点地址。</para></param>
    /// <param name="buffer">Pointer into pinned memory that must stay valid until the URB is released.
    /// <para>指向固定内存的指针，在 URB 释放前必须保持有效。</para></param>
    /// <param name="length">The number of bytes to transfer. <para>要传输的字节数。</para></param>
    /// <param name="timeoutMs">The wait budget in milliseconds; <see cref="UsbTransferPolicies.InfiniteTimeoutMs"/> waits in bounded poll slices forever.
    /// <para>等待预算（毫秒）；<see cref="UsbTransferPolicies.InfiniteTimeoutMs"/> 表示以有界 poll 切片无限等待。</para></param>
    /// <param name="cancellationToken">A token that aborts the wait and discards the URB.
    /// <para>中止等待并丢弃 URB 的取消令牌。</para></param>
    /// <param name="urbType">The usbfs URB type (bulk or interrupt). <para>usbfs URB 类型（bulk 或 interrupt）。</para></param>
    /// <param name="pinnedBuffer">An optional pinned-buffer handle whose ownership moves to this method.
    /// <para>可选的固定缓冲区句柄，其所有权移交本方法。</para></param>
    /// <returns>The kernel completion; a zero status with zero bytes means the wait ended without a payload.
    /// <para>内核完成结果；状态为零且字节数为零表示等待结束但没有负载。</para></returns>
    /// <exception cref="UsbDeviceDisconnectedException">The device disconnected while the URB was in flight.
    /// <para>URB 在途时设备断开。</para></exception>
    /// <exception cref="IOException">The submit, poll or discard step failed; on an unconfirmed discard the pinned handles are leaked.
    /// <para>提交、poll 或 discard 步骤失败；discard 未获确认时固定句柄被泄漏。</para></exception>
    private async Task<UrbOutcome> SubmitUrbAsync(byte endpoint, IntPtr buffer, int length, int timeoutMs, CancellationToken cancellationToken, byte urbType, GCHandle? pinnedBuffer = null)
    {
        var urb = new Usbdevfs_urb
        {
            Type = urbType,
            Endpoint = endpoint,
            Flags = 0,
            Buffer = buffer,
            Buffer_length = length,
            Usercontext = IntPtr.Zero
        };

        var urbHandle = GCHandle.Alloc(urb, GCHandleType.Pinned);
        var urbPointer = urbHandle.AddrOfPinnedObject();
        var kernelReferencesUrb = true;
        lock (_urbSync)
        {
            _urbWaiters[urbPointer] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            var submitCode = (IntPtr.Size == 8) ? USBDEVFS_SUBMITURB_X86_64 : USBDEVFS_SUBMITURB_X86;
            if (ioctl(Fd, submitCode, urbPointer) < 0)
            {
                kernelReferencesUrb = false;
                throw new IOException($"USB async submit failed: {Marshal.GetLastWin32Error()}");
            }

            var infiniteWait = timeoutMs == UsbTransferPolicies.InfiniteTimeoutMs;
            var waitDeadlineTicks = infiniteWait ? long.MaxValue : Environment.TickCount64 + timeoutMs;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (TryTakeCompletion(urbPointer, out var completed))
                {
                    kernelReferencesUrb = false;
                    return InterpretCompletion(completed, endpoint);
                }

                var remainingMs = waitDeadlineTicks - Environment.TickCount64;
                if (remainingMs <= 0)
                {
                    DiscardPendingUrb(urbPointer);
                    kernelReferencesUrb = false;
                    return default;
                }

                var sliceMs = (int)Math.Min(UrbPollSliceMs, remainingMs);
                PollOutcome pollOutcome;
                if (sliceMs == UrbPollSliceMs)
                {
                    pollOutcome = await Task.Run(_pollUrbFullSlice, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    pollOutcome = await Task.Run(() => PollUrbOnce(sliceMs), cancellationToken).ConfigureAwait(false);
                }

                var pollResult = pollOutcome.Result;
                var revents = pollOutcome.Revents;
                var pollErrno = pollOutcome.Errno;

                if (pollResult < 0)
                {
                    if (pollErrno == EINTR)
                    {
                        continue;
                    }

                    DiscardPendingUrb(urbPointer);
                    kernelReferencesUrb = false;
                    throw new IOException($"USB async poll failed: {pollErrno}");
                }

                if (pollResult > 0 && (revents & (POLLERR | POLLHUP)) != 0)
                {
                    DiscardPendingUrb(urbPointer);
                    kernelReferencesUrb = false;
                    throw new UsbDeviceDisconnectedException(
                        $"USB async poll reported POLLERR/POLLHUP (revents 0x{revents:X}); the device is gone or unusable.", ENODEV);
                }

                DrainCompletedUrbs();
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                DiscardPendingUrb(urbPointer);
                kernelReferencesUrb = false;
            }
            catch (Exception discardError)
            {
                UsbTrace.LogError(
                    $"LinuxUsbDevice: cancelling URB 0x{urbPointer.ToInt64():X} did not complete ({discardError.GetType().Name}: {discardError.Message}); the pinned URB is kept so the kernel never writes freed memory.");
            }

            throw;
        }
        finally
        {
            lock (_urbSync)
            {
                _urbWaiters.Remove(urbPointer);
                _urbCompletions.Remove(urbPointer);
            }

            if (!kernelReferencesUrb)
            {
                urbHandle.Free();
                pinnedBuffer?.Free();
            }
        }
    }

    /// <summary>
    /// Runs one bounded poll for a URB wait slice. The poll descriptor is a per-call stack local because a
    /// reader and a writer can wait concurrently on this device; a shared per-instance descriptor would race
    /// revents between the two directions.
    /// <para>为 URB 等待切片执行一次有界 poll。poll 描述符是每次调用的栈上局部量，因为读与写可能在本设备上
    /// 并发等待；共享的实例级描述符会让两个方向的 revents 相互竞争。</para>
    /// </summary>
    /// <param name="timeoutMs">The poll slice budget in milliseconds. <para>本片 poll 的预算（毫秒）。</para></param>
    /// <returns>The poll return value, the reported revents and the captured errno.
    /// <para>poll 返回值、报告的 revents 与捕获的 errno。</para></returns>
    private PollOutcome PollUrbOnce(int timeoutMs)
    {
        var pfd = new PollFd { Fd = Fd, Events = POLLIN | POLLOUT };
        var result = poll(ref pfd, 1, timeoutMs);
        var errno = result < 0 ? Marshal.GetLastWin32Error() : 0;
        return new PollOutcome(result, pfd.Revents, errno);
    }

    /// <summary>Polls one full <see cref="UrbPollSliceMs"/> slice through the cached delegate target.
    /// <para>经缓存的委托目标 poll 一个完整的 <see cref="UrbPollSliceMs"/> 切片。</para></summary>
    private PollOutcome PollUrbFullSlice() => PollUrbOnce(UrbPollSliceMs);

    /// <summary>
    /// Reaps every URB the kernel has completed on this descriptor and files each completion under its URB
    /// pointer, so a reader that reaps the writer's completion does not lose it.
    /// <para>回收内核在该描述符上已完成的全部 URB，并按 URB 指针归档每个完成结果，使回收了写方向完成结果的
    /// 读线程不会把它丢掉。</para>
    /// </summary>
    private void DrainCompletedUrbs()
    {
        while (true)
        {
            var reaped = IntPtr.Zero;
            if (ioctl(Fd, ReapUrbNoDelayCode, ref reaped) != 0 || reaped == IntPtr.Zero)
            {
                return;
            }

            RouteCompletion(reaped);
        }
    }

    /// <remarks>
    /// Only file the completion while a submitting call still waits for it. A late completion whose
    /// owner already walked away (quarantined handles) is dropped instead of growing the table forever.
    /// <para>仅当提交方调用仍在等待时才归档完成结果。所属调用早已离开的迟到完成（被隔离的句柄）会被
    /// 丢弃，而不是让该表无限增长。</para>
    /// </remarks>
    private void RouteCompletion(IntPtr urbPointer)
    {
        var completed = Marshal.PtrToStructure<Usbdevfs_urb>(urbPointer);
        var outcome = new UrbOutcome(completed.Status, completed.Actual_length);
        TaskCompletionSource? waiter;
        lock (_urbSync)
        {
            if (_urbWaiters.TryGetValue(urbPointer, out waiter))
            {
                _urbCompletions[urbPointer] = outcome;
            }
        }

        waiter?.TrySetResult();
    }

    private bool TryTakeCompletion(IntPtr urbPointer, out UrbOutcome outcome)
    {
        lock (_urbSync)
        {
            return _urbCompletions.Remove(urbPointer, out outcome);
        }
    }

    private static uint ReapUrbNoDelayCode => IntPtr.Size == 8 ? USBDEVFS_REAPURBNDELAY_X86_64 : USBDEVFS_REAPURBNDELAY_X86;

    private static uint DiscardUrbCode => IntPtr.Size == 8 ? USBDEVFS_DISCARDURB_X86_64 : USBDEVFS_DISCARDURB_X86;

    /// <summary>
    /// Clears a halted endpoint after the kernel reported a stall and turns the stall into an empty read,
    /// matching the synchronous bulk path.
    /// <para>在内核报告 STALL 后清除端点停住状态并把该 STALL 转为空读取，与同步批量路径一致。</para>
    /// </summary>
    private UrbOutcome InterpretCompletion(UrbOutcome completed, byte endpoint)
    {
        if (completed.Status == 0)
        {
            return new UrbOutcome(0, Math.Max(0, completed.ActualLength));
        }

        if (-completed.Status == EPIPE)
        {
            ClearHalt(endpoint);
            return default;
        }

        return completed;
    }

    /// <summary>
    /// Discards a pending (not yet completed) URB and blocks until the kernel provably stops referencing it.
    /// <para>丢弃一个尚未完成的 URB，并阻塞到内核可证明不再引用它为止。</para>
    /// DISCARDURB is an asynchronous unlink: until REAPURBNDELAY hands the URB back, or the device disappears
    /// and the kernel abandons it, the kernel still owns the pinned URB structure and the user buffer it points
    /// at. Completions reaped here for other in-flight URBs are routed to their owners instead of being
    /// dropped. When the budget is exhausted (or an unexpected errno arrives) the pinned handles are
    /// intentionally left allocated and an exception is raised.
    /// <para>DISCARDURB 是异步 unlink：在 REAPURBNDELAY 取回 URB（或设备消失、内核放弃该 URB）之前，内核仍
    /// 持有固定的 URB 结构及其指向的用户缓冲区。此处回收到的其它在途 URB 完成会路由给其所属传输而不是被丢弃。
    /// 预算耗尽（或出现意外 errno）时有意保留固定句柄并抛出异常。</para>
    /// </summary>
    /// <param name="urbPointer">The pinned URB structure pointer that was submitted.
    /// <para>已提交的 URB 结构固定指针。</para></param>
    /// <exception cref="UsbDeviceDisconnectedException">The device disappeared during the discard.
    /// <para>discard 过程中设备消失。</para></exception>
    /// <exception cref="IOException">The kernel did not release the URB before the retry budget was exhausted; the pinned handle is deliberately leaked.
    /// <para>重试预算耗尽时内核仍未释放该 URB；固定句柄被有意泄漏。</para></exception>
    private void DiscardPendingUrb(IntPtr urbPointer)
    {
        _ = ioctl(Fd, DiscardUrbCode, urbPointer);

        var deadlineTicks = Environment.TickCount64 + UrbDiscardConfirmTimeoutMs;
        while (true)
        {
            if (TryTakeCompletion(urbPointer, out _))
            {
                return;
            }

            var drained = IntPtr.Zero;
            if (ioctl(Fd, ReapUrbNoDelayCode, ref drained) == 0)
            {
                if (drained != IntPtr.Zero)
                {
                    RouteCompletion(drained);
                }

                continue;
            }

            var reapErrno = Marshal.GetLastWin32Error();
            if (reapErrno is ENODEV or ESHUTDOWN)
            {
                return;
            }

            if (reapErrno != EAGAIN)
            {
                throw reapErrno == EPROTO
                    ? new UsbDeviceDisconnectedException($"USB async discard did not complete: device disconnected (errno {reapErrno}).", reapErrno)
                    : new IOException($"USB async discard did not complete: errno {reapErrno}");
            }

            if (Environment.TickCount64 >= deadlineTicks)
            {
                throw new IOException(
                    "USB async discard did not complete before the confirmation budget expired; the pinned URB is kept so the kernel never writes freed memory.");
            }

            Thread.Sleep(UrbDiscardConfirmIntervalMs);
        }
    }
}