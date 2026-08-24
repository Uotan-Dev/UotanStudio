using System.Runtime.InteropServices;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using static UotanStudio.Comm.Transports.Usb.Backend.Linux.LinuxUsbAPI;

namespace UotanStudio.Comm.Transports.Usb.Backend.Linux;

internal class LinuxUsbDevice : UsbDevice
{
    private const int PlatformDefaultTimeoutMs = UsbTransferPolicies.DefaultTimeoutMs;

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

        // Claim the primary interface plus any additional requested interfaces (e.g.
        // CDC-ACM control + data). A failure to claim any of them fails the open so the
        // session is never half-claimed.
        // <para>声明主接口及所有附加请求接口（例如 CDC-ACM 控制 + 数据）。
        // 任一接口声明失败都会导致打开失败，避免会话处于半声明状态。</para>
        var interfacesToClaim = new List<byte>(ClaimedInterfaceIds);
        if (InterfaceId is >= 0 and <= byte.MaxValue && !interfacesToClaim.Contains((byte)InterfaceId))
        {
            interfacesToClaim.Insert(0, (byte)InterfaceId);
        }

        foreach (var ifcByte in interfacesToClaim)
        {
            if (!ClaimInterface(ifcByte))
            {
                _fd.Dispose();
                return Marshal.GetLastWin32Error();
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
            // udev may rebind a kernel driver right after the disconnect, so retry the claim
            // instead of failing on the first EBUSY. Matches libusb's auto-detach behaviour.
            // <para>udev 可能在分离后立即重新绑定内核驱动，因此重试几次而不是首次 EBUSY 即失败；
            // 与 libusb 的自动分离行为一致。</para>
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

    public override void Reset()
    {
        if (!_fd.IsInvalid)
        {
            ioctl(Fd, USBDEVFS_RESET, IntPtr.Zero);
        }
    }

    public override int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
    {
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
            Timeout = (uint)effectiveTimeoutMs
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

    public override void Dispose()
    {
        if (!_fd.IsInvalid)
        {
            foreach (var ifc in _claimedInterfaces)
            {
                int interfaceId = ifc;
                ioctl(Fd, USBDEVFS_RELEASEINTERFACE, ref interfaceId);
            }

            _claimedInterfaces.Clear();
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

    protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
    {
        var bulk = new Usbdevfs_bulktransfer
        {
            Ep = Ep_in,
            Len = (uint)length,
            Timeout = timeoutMs == UsbTransferPolicies.InfiniteTimeoutMs ? 0 : (uint)timeoutMs,
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
            Timeout = timeoutMs == UsbTransferPolicies.InfiniteTimeoutMs ? 0 : (uint)timeoutMs,
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
            // usbfs treats 0 as "no timeout"; map the -1 sentinel accordingly.
            // <para>usbfs 将 0 视为"无超时"；因此将 -1 哨兵映射为 0。</para>
            Timeout = timeoutMs == UsbTransferPolicies.InfiniteTimeoutMs ? 0 : (uint)timeoutMs,
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

        // Interrupt endpoints cannot use the USBDEVFS_BULK ioctl; they require an URB of
        // type USBDEVFS_URB_TYPE_INTERRUPT, so reuse the async URB machinery synchronously.
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
        // usbfs treats 0 as "no timeout"; map the -1 sentinel accordingly.
        // <para>usbfs 将 0 视为"无超时"；将 -1 哨兵映射为 0。</para>
        if (effectiveTimeoutMs == UsbTransferPolicies.InfiniteTimeoutMs)
        {
            effectiveTimeoutMs = 0;
        }

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
        if (effectiveTimeoutMs == UsbTransferPolicies.InfiniteTimeoutMs)
        {
            effectiveTimeoutMs = 0;
        }

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
        try
        {
            var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset);
            var transferred = await SubmitUrbAsync(endpointAddress, ptr, length, effectiveTimeoutMs, cancellationToken, USBDEVFS_URB_TYPE_INTERRUPT).ConfigureAwait(false);
            return transferred == 0
                ? new UsbReadResult(0, isTimeout: true, isShortPacket: false)
                : new UsbReadResult((int)transferred, isTimeout: false, isShortPacket: transferred < length);
        }
        finally
        {
            handle.Free();
        }
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
        try
        {
            var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset);
            var transferred = await SubmitUrbAsync(endpointAddress, ptr, length, effectiveTimeoutMs, cancellationToken, USBDEVFS_URB_TYPE_INTERRUPT).ConfigureAwait(false);
            return transferred;
        }
        finally
        {
            handle.Free();
        }
    }

    private async Task<uint> SubmitUrbAsync(byte endpoint, byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken, byte urbType)
    {
        var bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var ptr = new IntPtr(bufferHandle.AddrOfPinnedObject().ToInt64() + offset);
            return await SubmitUrbAsync(endpoint, ptr, length, timeoutMs, cancellationToken, urbType).ConfigureAwait(false);
        }
        finally
        {
            bufferHandle.Free();
        }
    }

    /// <summary>
    /// Submits a URB against an already-pinned buffer pointer and reaps its completion.
    /// <para>针对已固定的缓冲区指针提交 URB 并回收其完成结果。</para>
    /// Used by both the byte[] interrupt entry points and the chunk-level async overrides,
    /// where the caller already owns the pinned buffer (the base-class loop pins it).
    /// <para>供 byte[] 中断入口与分块级异步覆盖共用——调用方已持有固定缓冲区
    /// （基类循环负责固定）。</para>
    /// </summary>
    private async Task<uint> SubmitUrbAsync(byte endpoint, IntPtr buffer, int length, int timeoutMs, CancellationToken cancellationToken, byte urbType)
    {
        var urb = new Usbdevfs_urb
        {
            Type = urbType,
            Endpoint = endpoint,
            Flags = 0, // keep short reads completing normally (do not set SHORT_NOT_OK)
            Buffer = buffer,
            Buffer_length = length,
            Usercontext = IntPtr.Zero
        };

        var urbHandle = GCHandle.Alloc(urb, GCHandleType.Pinned);
        try
        {
            var submitCode = (IntPtr.Size == 8) ? USBDEVFS_SUBMITURB_X86_64 : USBDEVFS_SUBMITURB_X86;
            if (ioctl(Fd, submitCode, urbHandle.AddrOfPinnedObject()) < 0)
            {
                throw new IOException($"USB async submit failed: {Marshal.GetLastWin32Error()}");
            }

            int pollResult;
            try
            {
                pollResult = await Task.Run(() =>
                {
                    var pfd = new PollFd { Fd = Fd, Events = POLLIN | POLLOUT };
                    return poll(ref pfd, 1, timeoutMs);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled while the URB is still queued: discard and drain it so the pinned
                // URB can be freed and the kernel does not keep a stale reference.
                DiscardPendingUrb(urbHandle);
                throw;
            }

            if (pollResult == 0)
            {
                // Timeout: discard the pending URB and drain it so the pinned URB can be freed.
                DiscardPendingUrb(urbHandle);
                return 0;
            }
            if (pollResult < 0)
            {
                var err = Marshal.GetLastWin32Error();
                return err == EINTR ? (uint)0 : throw new IOException($"USB async poll failed: {err}");
            }

            // Reap the completed URB and read its status/Actual_length.
            var reaped = IntPtr.Zero;
            var reapCode2 = (IntPtr.Size == 8) ? USBDEVFS_REAPURBNDELAY_X86_64 : USBDEVFS_REAPURBNDELAY_X86;
            if (ioctl(Fd, reapCode2, ref reaped) < 0)
            {
                throw new IOException($"USB async reap failed: {Marshal.GetLastWin32Error()}");
            }

            var completed = Marshal.PtrToStructure<Usbdevfs_urb>(reaped);
            if (completed.Status != 0)
            {
                var err = -completed.Status; // kernel reports negative errno in status
                return err is ETIMEDOUT or EPIPE
                    ? 0
                    : err is ENODEV or ESHUTDOWN or EPROTO
                    ? throw new UsbDeviceDisconnectedException($"USB async transfer failed: device disconnected (error: {err}).", err)
                    : (uint)0;
            }
            return (uint)Math.Max(0, completed.Actual_length);
        }
        finally
        {
            urbHandle.Free();
        }
    }

    /// <summary>
    /// Discards a pending (not yet completed) URB and drains it from the kernel queue.
    /// <para>丢弃一个尚未完成的 URB 并将其从内核队列中取出。</para>
    /// Used on timeout and on cancellation so the pinned URB can be freed without the
    /// kernel holding a stale reference.
    /// <para>在超时与取消时使用，以便在不留内核悬空引用的情况下释放固定的 URB。</para>
    /// </summary>
    private void DiscardPendingUrb(GCHandle urbHandle)
    {
        var drain = IntPtr.Zero;
        var discardCode = (IntPtr.Size == 8) ? USBDEVFS_DISCARDURB_X86_64 : USBDEVFS_DISCARDURB_X86;
        var reapCode = (IntPtr.Size == 8) ? USBDEVFS_REAPURBNDELAY_X86_64 : USBDEVFS_REAPURBNDELAY_X86;
        _ = ioctl(Fd, discardCode, urbHandle.AddrOfPinnedObject());
        _ = ioctl(Fd, reapCode, ref drain);
    }
}