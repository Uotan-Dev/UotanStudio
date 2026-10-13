using System.Diagnostics.CodeAnalysis;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Discovery;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Devices.Services.Sessions;

/// <summary>Session factories registered by the controller by default. <para>控制器默认注册的会话工厂。</para></summary>
internal static class DefaultDeviceSessionFactories
{
    public static readonly IDeviceSessionFactory[] All =
    [
        new AdbDeviceSessionFactory(),
        new HdcDeviceSessionFactory(),
        new FastbootDeviceSessionFactory(),
        new EdlDeviceSessionFactory(),
        new BootromDeviceSessionFactory(),
        new MtkDeviceSessionFactory()
    ];
}

/// <summary>Guards shared by session factories; failure must leave the leased transport untouched.
/// <para>会话工厂共用的守卫；失败时必须保持已租约的传输原样不动。</para></summary>
internal static class LeasedTransport
{
    public static bool TryUnwrap<T>(IAsyncDisposable transport, [NotNullWhen(true)] out T? value)
        where T : class, IDisposable
    {
        value = transport switch
        {
            OwnedDeviceTransport<T> owned => owned.Value,
            T direct => direct,
            _ => null
        };
        return value is not null;
    }

    public static T Require<T>(IAsyncDisposable transport, DeviceSessionKind kind) where T : class, IDisposable =>
        TryUnwrap<T>(transport, out var value)
            ? value
            : throw new InvalidOperationException($"The lease does not carry the exact transport required for a {kind} session.");

    /// <summary>Only an observed protocol on the endpoint authorizes the session; never re-infers from serials.
    /// <para>只有端点上观测到的协议才能授权该会话；绝不按序列号重新推断。</para></summary>
    /// <param name="endpoint">The endpoint carrying the observed protocol. <para>携带已观测协议的端点。</para></param>
    /// <param name="protocol">The protocol string the endpoint must match. <para>端点必须匹配的协议字符串。</para></param>
    public static void RequireProtocol(DeviceEndpoint endpoint, string protocol)
    {
        if (!string.Equals(endpoint.Protocol, protocol, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"The endpoint does not carry an observed {protocol} protocol.");
        }
    }
}

internal sealed class AdbDeviceSessionFactory : IDeviceSessionFactory
{
    public DeviceSessionKind Kind => DeviceSessionKind.Adb;

    public Task<object> CreateAsync(DeviceEndpoint endpoint, IAsyncDisposable transport, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LeasedTransport.RequireProtocol(endpoint, "adb");
        if (LeasedTransport.TryUnwrap<AdbSession>(transport, out var serverSession))
        {
            return Task.FromResult<object>(serverSession);
        }

        var usb = LeasedTransport.Require<IUsbDeviceSession>(transport, Kind);
        return Task.FromResult<object>(AdbSession.ForDirectUsb(usb));
    }
}

internal sealed class HdcDeviceSessionFactory : IDeviceSessionFactory
{
    public DeviceSessionKind Kind => DeviceSessionKind.Hdc;

    public Task<object> CreateAsync(DeviceEndpoint endpoint, IAsyncDisposable transport, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LeasedTransport.RequireProtocol(endpoint, "hdc");
        if (LeasedTransport.TryUnwrap<HdcSession>(transport, out var serverSession))
        {
            return Task.FromResult<object>(serverSession);
        }

        var usb = LeasedTransport.Require<IUsbDeviceSession>(transport, Kind);
        if (!new HdcFactory().TryCreate(usb, out var session) || session is not HdcSession hdcSession)
        {
            throw new IOException("The device does not expose an HDC interface.");
        }

        return Task.FromResult<object>(hdcSession);
    }
}

internal sealed class FastbootDeviceSessionFactory : IDeviceSessionFactory
{
    public DeviceSessionKind Kind => DeviceSessionKind.Fastboot;

    public Task<object> CreateAsync(DeviceEndpoint endpoint, IAsyncDisposable transport, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LeasedTransport.RequireProtocol(endpoint, "fastboot");
        var usb = LeasedTransport.Require<IUsbDeviceSession>(transport, Kind);
        if (!new FastbootFactory().TryCreate(usb, out var session) || session is not FastbootSession fastbootSession)
        {
            throw new IOException("The device does not expose a fastboot interface.");
        }

        return Task.FromResult<object>(fastbootSession);
    }
}

internal sealed class EdlDeviceSessionFactory : IDeviceSessionFactory
{
    public DeviceSessionKind Kind => DeviceSessionKind.Edl;

    public Task<object> CreateAsync(DeviceEndpoint endpoint, IAsyncDisposable transport, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LeasedTransport.RequireProtocol(endpoint, "edl");
        if (LeasedTransport.TryUnwrap<SerialTransport>(transport, out var serial))
        {
            return Task.FromResult<object>(new EdlSession(serial));
        }

        var usb = LeasedTransport.Require<IUsbDeviceSession>(transport, Kind);
        return Task.FromResult<object>(new EdlSession(new UsbDeviceTransport(usb)));
    }
}

internal sealed class BootromDeviceSessionFactory : IDeviceSessionFactory
{
    public DeviceSessionKind Kind => DeviceSessionKind.Bootrom;

    public Task<object> CreateAsync(DeviceEndpoint endpoint, IAsyncDisposable transport, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LeasedTransport.RequireProtocol(endpoint, "rkdl");
        var usb = LeasedTransport.Require<IUsbDeviceSession>(transport, Kind);
        return Task.FromResult<object>(new RkdlSession(new UsbDeviceTransport(usb)));
    }
}

internal sealed class MtkDeviceSessionFactory : IDeviceSessionFactory
{
    public DeviceSessionKind Kind => DeviceSessionKind.Mtk;

    public Task<object> CreateAsync(DeviceEndpoint endpoint, IAsyncDisposable transport, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LeasedTransport.RequireProtocol(endpoint, "mtk");
        var usb = LeasedTransport.Require<IUsbDeviceSession>(transport, Kind);
        var classification = MtkModeClassifier.Classify(usb.DeviceInfo);
        if (classification.Mode == MtkUsbMode.LoggingOnly)
        {
            throw new NotSupportedException($"The endpoint must not receive MTK commands: {classification.Reason}");
        }

        var session = new MtkSession(new UsbDeviceTransport(usb));
        session.NoteModeDiscovery(classification);
        return Task.FromResult<object>(session);
    }
}