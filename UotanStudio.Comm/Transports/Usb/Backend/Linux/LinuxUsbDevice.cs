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
    /// Maximum duration of one poll slice, bounding how long an infinite-timeout wait blocks
    /// without a chance to observe cancellation or a finite deadline.
    /// <para>单个 poll 切片的最大时长，限制无限超时等待在不观察取消或有限期限的情况下阻塞多久。</para>
    /// </summary>
    private const int UrbPollSliceMs = 30000;

    private readonly LinuxUsbFd _fd = new();

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
    /// Issues USBDEVFS_RESET and, only when the ioctl succeeds, drops the recorded interface claims: the kernel revokes every claim held by this descriptor across a reset. A failed reset leaves both the handle and the claim records untouched.
    /// <para>发起 USBDEVFS_RESET，且仅在该 ioctl 成功时清除已记录的接口申请——内核会在复位时撤销本描述符持有的全部申请。复位失败时句柄与申请记录均保持不变。</para>
    /// </summary>
    public override void Reset()
    {
        if (_fd.IsInvalid)
        {
            return;
        }

        if (ioctl(Fd, USBDEVFS_RESET, IntPtr.Zero) != 0)
        {
            var err = Marshal.GetLastWin32Error();
            UsbTrace.Log($"LinuxUsbDevice: USBDEVFS_RESET failed on '{DevicePath}' (errno {err}); claimed interfaces are retained.");
            return;
        }

        _claimedInterfaces.Clear();
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
        var transferred = SubmitUrbAsync(endpointAddress, buffer, offset, length, effectiveTimeoutMs, CancellationToken.None, USBDEVFS_URB_TYPE_INTERRUPT).GetAwaiter().GetResult();
        return transferred == 0
            ? new UsbReadResult(0, isTimeout: true, isShortPacket: false)
            : new UsbReadResult((int)transferred, isTimeout: false, isShortPacket: transferred < length);
    }

    public override long WriteInterrupt(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs)
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
        var transferred = SubmitUrbAsync(endpointAddress, data, offset, length, effectiveTimeoutMs, CancellationToken.None, USBDEVFS_URB_TYPE_INTERRUPT).GetAwaiter().GetResult();
        return transferred;
    }

    protected override async Task<UsbChunkResult> ReadChunkAsync(IntPtr buffer, int length, int timeoutMs, CancellationToken cancellationToken)
    {
        if (_fd.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var transferred = await SubmitUrbAsync(Ep_in, buffer, length, effectiveTimeoutMs, cancellationToken, USBDEVFS_URB_TYPE_BULK).ConfigureAwait(false);
        return transferred > 0
            ? UsbChunkResult.Success((int)transferred)
            : UsbChunkResult.Timeout(ETIMEDOUT);
    }

    protected override async Task<UsbChunkResult> WriteChunkAsync(IntPtr buffer, int length, int timeoutMs, CancellationToken cancellationToken)
    {
        if (_fd.IsInvalid)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var transferred = await SubmitUrbAsync(Ep_out, buffer, length, effectiveTimeoutMs, cancellationToken, USBDEVFS_URB_TYPE_BULK).ConfigureAwait(false);
        return transferred > 0
            ? UsbChunkResult.Success((int)transferred)
            : UsbChunkResult.Timeout(ETIMEDOUT);
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
        var transferred = await SubmitUrbAsync(endpointAddress, ptr, length, effectiveTimeoutMs, cancellationToken, USBDEVFS_URB_TYPE_INTERRUPT, handle).ConfigureAwait(false);
        return transferred == 0
            ? new UsbReadResult(0, isTimeout: true, isShortPacket: false)
            : new UsbReadResult((int)transferred, isTimeout: false, isShortPacket: transferred < length);
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
        var transferred = await SubmitUrbAsync(endpointAddress, ptr, length, effectiveTimeoutMs, cancellationToken, USBDEVFS_URB_TYPE_INTERRUPT, handle).ConfigureAwait(false);
        return transferred;
    }

    private async Task<uint> SubmitUrbAsync(byte endpoint, byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken, byte urbType)
    {
        var bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        var ptr = new IntPtr(bufferHandle.AddrOfPinnedObject().ToInt64() + offset);
        return await SubmitUrbAsync(endpoint, ptr, length, timeoutMs, cancellationToken, urbType, bufferHandle).ConfigureAwait(false);
    }

    /// <summary>
    /// Submits a URB against an already-pinned buffer pointer, waits in bounded poll slices and reaps its completion.
    /// <para>针对已固定的缓冲区指针提交 URB，以有界的 poll 切片等待并回收其完成结果。</para>
    /// Used by the byte[] entry points and the chunk-level async overrides; the byte[] and
    /// interrupt entry points hand over the pinned-buffer handle they own, while the chunk-level
    /// overrides keep base-class ownership of their buffer. The pinned URB structure and any
    /// handed-over buffer handle are freed only once the kernel has provably released the URB
    /// (confirmed reap or device disappearance); any other exit deliberately leaks them, because
    /// freeing memory the kernel may still write to corrupts the heap while one leaked pinned
    /// block per stuck discard is bounded and safe.
    /// <para>供 byte[] 入口与分块级异步覆盖共用；byte[] 与中断入口移交其拥有的固定缓冲区句柄，
    /// 分块级覆盖的缓冲区仍归基类所有。只有在内核可证明已释放 URB（确认 reap 或设备消失）后才
    /// 释放固定的 URB 结构与移交的缓冲区句柄；其余退出路径有意泄漏它们——释放内核仍可能写入的
    /// 内存会破坏堆，而每个卡住的 discard 只泄漏一个固定块，是有界且安全的。</para>
    /// </summary>
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
    /// <returns>The number of bytes transferred, or 0 on timeout or a non-fatal completion status.
    /// <para>已传输字节数；超时或非致命完成状态返回 0。</para></returns>
    /// <exception cref="UsbDeviceDisconnectedException">The device disconnected or the kernel reported a disconnection-class error.
    /// <para>设备断开，或内核报告断连类错误。</para></exception>
    /// <exception cref="IOException">The submit, poll, reap or discard step failed; on an unconfirmed discard the pinned handles are leaked.
    /// <para>提交、poll、reap 或 discard 步骤失败；discard 未获确认时固定句柄被泄漏。</para></exception>
    private async Task<uint> SubmitUrbAsync(byte endpoint, IntPtr buffer, int length, int timeoutMs, CancellationToken cancellationToken, byte urbType, GCHandle? pinnedBuffer = null)
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
        var kernelReferencesUrb = true;
        try
        {
            var submitCode = (IntPtr.Size == 8) ? USBDEVFS_SUBMITURB_X86_64 : USBDEVFS_SUBMITURB_X86;
            if (ioctl(Fd, submitCode, urbHandle.AddrOfPinnedObject()) < 0)
            {
                kernelReferencesUrb = false;
                throw new IOException($"USB async submit failed: {Marshal.GetLastWin32Error()}");
            }

            var urbPointer = urbHandle.AddrOfPinnedObject();
            var infiniteWait = timeoutMs == UsbTransferPolicies.InfiniteTimeoutMs;
            var waitDeadlineTicks = infiniteWait ? long.MaxValue : Environment.TickCount64 + timeoutMs;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remainingMs = waitDeadlineTicks - Environment.TickCount64;
                if (remainingMs <= 0)
                {
                    DiscardPendingUrb(urbHandle);
                    kernelReferencesUrb = false;
                    return 0;
                }

                var sliceMs = (int)Math.Min(UrbPollSliceMs, remainingMs);
                var revents = (short)0;
                var pollErrno = 0;
                var pollResult = await Task.Run(() =>
                {
                    var pfd = new PollFd { Fd = Fd, Events = POLLIN | POLLOUT };
                    var result = poll(ref pfd, 1, sliceMs);
                    revents = pfd.Revents;
                    pollErrno = result < 0 ? Marshal.GetLastWin32Error() : 0;
                    return result;
                }, cancellationToken).ConfigureAwait(false);

                if (pollResult > 0)
                {
                    if ((revents & (POLLERR | POLLHUP)) != 0)
                    {
                        DiscardPendingUrb(urbHandle);
                        kernelReferencesUrb = false;
                        throw new UsbDeviceDisconnectedException(
                            $"USB async poll reported POLLERR/POLLHUP (revents 0x{revents:X}); the device is gone or unusable.", ENODEV);
                    }

                    break;
                }

                if (pollResult == 0)
                {
                    continue;
                }

                if (pollErrno == EINTR)
                {
                    continue;
                }

                DiscardPendingUrb(urbHandle);
                kernelReferencesUrb = false;
                throw new IOException($"USB async poll failed: {pollErrno}");
            }

            var reapCode = (IntPtr.Size == 8) ? USBDEVFS_REAPURBNDELAY_X86_64 : USBDEVFS_REAPURBNDELAY_X86;
            var reaped = IntPtr.Zero;
            if (ioctl(Fd, reapCode, ref reaped) == 0)
            {
                if (reaped != urbPointer)
                {
                    UsbTrace.Log($"LinuxUsbDevice: reaped foreign URB 0x{reaped.ToInt64():X} while waiting for 0x{urbPointer.ToInt64():X}; draining our URB and reporting a timeout.");
                    DiscardPendingUrb(urbHandle);
                    kernelReferencesUrb = false;
                    return 0;
                }

                kernelReferencesUrb = false;
                var completed = Marshal.PtrToStructure<Usbdevfs_urb>(reaped);
                if (completed.Status != 0)
                {
                    var err = -completed.Status;
                    if (err == EPIPE)
                    {
                        ClearHalt(endpoint);
                        return 0;
                    }

                    return err is ENODEV or ESHUTDOWN or EPROTO
                        ? throw new UsbDeviceDisconnectedException($"USB async transfer failed: device disconnected (error: {err}).", err)
                        : (uint)0;
                }

                return (uint)Math.Max(0, completed.Actual_length);
            }

            var reapErrno = Marshal.GetLastWin32Error();
            if (reapErrno is ENODEV or ESHUTDOWN)
            {
                kernelReferencesUrb = false;
                throw new UsbDeviceDisconnectedException($"USB async reap failed: device disconnected (error: {reapErrno}).", reapErrno);
            }

            DiscardPendingUrb(urbHandle);
            kernelReferencesUrb = false;
            throw reapErrno == EPROTO
                ? new UsbDeviceDisconnectedException($"USB async reap failed: device disconnected (error: {reapErrno}).", reapErrno)
                : new IOException($"USB async reap failed: {reapErrno}");
        }
        catch (OperationCanceledException)
        {
            DiscardPendingUrb(urbHandle);
            kernelReferencesUrb = false;
            throw;
        }
        finally
        {
            if (!kernelReferencesUrb)
            {
                urbHandle.Free();
                pinnedBuffer?.Free();
            }
        }
    }

    /// <summary>
    /// Discards a pending (not yet completed) URB and blocks until the kernel provably stops referencing it.
    /// <para>丢弃一个尚未完成的 URB，并阻塞到内核可证明不再引用它为止。</para>
    /// DISCARDURB is an asynchronous unlink: until REAPURBNDELAY hands the URB back, or the device
    /// disappears and the kernel abandons it, the kernel still owns the pinned URB structure and
    /// the user buffer it points at. Freeing that memory earlier would let the kernel write into
    /// released managed memory and corrupt the heap, while leaking one pinned block per stuck
    /// discard is bounded and safe; therefore the reap is confirmed in a retry loop, and when the
    /// budget is exhausted (or an unexpected errno arrives) the pinned handles are intentionally
    /// left allocated and a disconnection-class exception is raised.
    /// <para>DISCARDURB 是异步 unlink：在 REAPURBNDELAY 取回 URB（或设备消失、内核放弃该 URB）之前，
    /// 内核仍持有固定的 URB 结构及其指向的用户缓冲区。提前释放这些内存会让内核写入已释放的托管内存、
    /// 破坏堆；而每个卡住的 discard 只泄漏一个固定块，是有界且安全的。因此通过重试循环确认 reap，
    /// 预算耗尽（或出现意外 errno）时有意保留固定句柄并抛出断连类异常。</para>
    /// </summary>
    /// <param name="urbHandle">The pinned handle of the submitted URB structure.
    /// <para>已提交的 URB 结构的固定句柄。</para></param>
    /// <exception cref="UsbDeviceDisconnectedException">The device disappeared during the discard.
    /// <para>discard 过程中设备消失。</para></exception>
    /// <exception cref="IOException">The kernel did not release the URB before the retry budget was exhausted; the pinned handle is deliberately leaked.
    /// <para>重试预算耗尽时内核仍未释放该 URB；固定句柄被有意泄漏。</para></exception>
    private void DiscardPendingUrb(GCHandle urbHandle)
    {
        var discardCode = (IntPtr.Size == 8) ? USBDEVFS_DISCARDURB_X86_64 : USBDEVFS_DISCARDURB_X86;
        var reapCode = (IntPtr.Size == 8) ? USBDEVFS_REAPURBNDELAY_X86_64 : USBDEVFS_REAPURBNDELAY_X86;
        _ = ioctl(Fd, discardCode, urbHandle.AddrOfPinnedObject());

        var urbPointer = urbHandle.AddrOfPinnedObject();
        var deadlineTicks = Environment.TickCount64 + UrbDiscardConfirmTimeoutMs;
        while (true)
        {
            var drained = IntPtr.Zero;
            var reapResult = ioctl(Fd, reapCode, ref drained);
            var reapErrno = reapResult == 0 ? 0 : Marshal.GetLastWin32Error();
            if (reapResult == 0 && drained == urbPointer)
            {
                return;
            }

            if (reapErrno is ENODEV or ESHUTDOWN)
            {
                return;
            }

            var stalled = reapResult == 0
                || reapErrno == EAGAIN;
            if (!stalled || Environment.TickCount64 >= deadlineTicks)
            {
                throw reapErrno is ENODEV or ESHUTDOWN or EPROTO
                    ? new UsbDeviceDisconnectedException($"USB async discard did not complete: device disconnected (errno {reapErrno}).", reapErrno)
                    : new IOException($"USB async discard did not complete: errno {reapErrno}");
            }

            Thread.Sleep(UrbDiscardConfirmIntervalMs);
        }
    }
}