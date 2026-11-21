using UotanStudio.Comm.Protocols.Sunxi;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm;

/// <summary>
/// Headless entry point that drives an Allwinner device from FEL through the FES service so a
/// <see cref="SunxiFlashPlan"/> can be executed without the desktop UI. It is a thin composition of the
/// same protocol pieces the GUI uses: open a WinUSB bulk endpoint, handshake in FEL, upload the FES
/// first-stage loader and U-Boot service, wait for the re-enumerated FES endpoint, then hand the live
/// <see cref="SunxiFesSession"/> to the caller.
/// <para>无界面入口，把全志设备从 FEL 引导到 FES 服务，从而在无桌面 UI 的情况下执行
/// <see cref="SunxiFlashPlan"/>。它只是把 GUI 所用的协议组件薄薄组合起来：打开 WinUSB 批量端点，在 FEL
/// 下握手，上传 FES 一级加载器与 U-Boot 服务，等待重新枚举后的 FES 端点，然后把活动的
/// <see cref="SunxiFesSession"/> 交给调用方。</para>
/// </summary>
public sealed class SunxiFlashFacade : IDisposable
{
    private readonly ushort _vid;
    private readonly ushort _pid;
    private IUsbDeviceSession? _felUsb;
    private IUsbDeviceSession? _fesUsb;
    private SunxiDeviceSession? _device;
    private bool _disposed;

    /// <summary>Creates a facade bound to one Allwinner USB identity (normally 1F3A:EFE8).
    /// <para>创建绑定到某个全志 USB 身份（通常为 1F3A:EFE8）的门面。</para></summary>
    public SunxiFlashFacade(ushort vid = 0x1F3A, ushort pid = 0xEFE8)
    {
        _vid = vid;
        _pid = pid;
    }

    /// <summary>The FEL session while the device still runs the boot ROM.
    /// <para>设备仍运行 boot ROM 时的 FEL 会话。</para></summary>
    public SunxiFelSession? Fel => _device?.IsFel == true ? _device.Fel : null;

    /// <summary>The FES session after the service has been started and the device re-enumerated.
    /// <para>服务启动且设备重新枚举后的 FES 会话。</para></summary>
    public SunxiFesSession? Fes => _device?.IsFes == true ? _device.Fes : null;

    /// <summary>Opens the device in FEL mode and completes the identity handshake.
    /// <para>以 FEL 模式打开设备并完成身份握手。</para></summary>
    public async Task<SunxiDeviceIdentity> OpenFelAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var usb = CommKit.OpenDeviceSession(UsbApiKind.Auto,
            new UsbDeviceFilter { VendorId = _vid, ProductId = _pid })
            ?? throw new InvalidOperationException(
                $"No Allwinner device {_vid:X4}:{_pid:X4} was found. Check the USB cable, FEL key and driver binding.");
        _felUsb = usb;
        _device = await SunxiDeviceSession.OpenAsync(new SunxiEfexUsbTransport(usb), cancellationToken)
            .ConfigureAwait(false);
        if (!_device.IsFel)
        {
            throw new InvalidOperationException(
                $"The device reports mode {_device.Mode}; a fresh FEL endpoint is required to boot the FES service.");
        }

        return _device.Identity;
    }

    /// <summary>Opens the device and completes the identity handshake, accepting either FEL or FES mode.
    /// Useful when the device is already running the FES service from an earlier handoff.
    /// <para>打开设备并完成身份握手，接受 FEL 或 FES 任一模式。适用于设备已经因先前的切换而运行 FES 服务的情形。</para></summary>
    public async Task<SunxiDeviceIdentity> OpenExistingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var usb = CommKit.OpenDeviceSession(UsbApiKind.Auto,
            new UsbDeviceFilter { VendorId = _vid, ProductId = _pid })
            ?? throw new InvalidOperationException(
                $"No Allwinner device {_vid:X4}:{_pid:X4} was found. Check the USB cable and driver binding.");
        _felUsb = usb;
        _device = await SunxiDeviceSession.OpenAsync(new SunxiEfexUsbTransport(usb), cancellationToken)
            .ConfigureAwait(false);
        return _device.Identity;
    }

    /// <summary>Uploads the FES first-stage loader and U-Boot service over FEL, then waits for the
    /// re-enumerated endpoint to answer the FES handshake. Ports the GUI's reconnect loop into this
    /// headless facade.
    /// <para>经 FEL 上传 FES 一级加载器与 U-Boot 服务，然后等待重新枚举的端点应答 FES 握手。把 GUI 的重连循环
    /// 移植到这个无界面门面中。</para></summary>
    public async Task<SunxiFesSession> BootFesAsync(ReadOnlyMemory<byte> fes1Image, SunxiServiceImages images,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var fel = Fel ?? throw new InvalidOperationException("Call OpenFelAsync first; no FEL session is open.");

        var handoff = new SunxiFesHandoff(async token =>
        {
            _felUsb?.Dispose();
            _felUsb = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var wait = deadline.Token;
            Exception? last = null;
            while (true)
            {
                wait.ThrowIfCancellationRequested();
                var usb = CommKit.OpenDeviceSession(UsbApiKind.Auto,
                    new UsbDeviceFilter { VendorId = _vid, ProductId = _pid });
                if (usb is not null)
                {
                    try
                    {
                        var device = await SunxiDeviceSession.OpenAsync(new SunxiEfexUsbTransport(usb), wait)
                            .ConfigureAwait(false);
                        if (device.IsFes)
                        {
                            _fesUsb = usb;
                            _device = device;
                            return device.Fes;
                        }

                        last = new InvalidOperationException($"The device reports mode {device.Mode} instead of FES.");
                        usb.Dispose();
                    }
                    catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException
                                                   or SunxiEfexException or OperationCanceledException)
                    {
                        last = ex;
                        usb.Dispose();
                    }
                }

                await Task.Delay(500, wait).ConfigureAwait(false);
            }
        });

        await handoff.InitialiseDramAsync(fel, fes1Image, cancellationToken).ConfigureAwait(false);
        return await handoff.StartServiceAsync(fel, images, 20, cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SunxiFlashFacade));
        }
    }

    /// <summary>Releases both the FEL and FES bulk endpoints.
    /// <para>释放 FEL 与 FES 两条批量端点。</para></summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _felUsb?.Dispose();
        _fesUsb?.Dispose();
        _felUsb = null;
        _fesUsb = null;
    }
}