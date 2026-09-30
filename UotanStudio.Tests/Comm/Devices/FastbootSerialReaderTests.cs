using System.Reflection;
using UotanStudio.Comm;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public sealed class FastbootSerialReaderTests
{
    private const string ProbeDeviceKey = "native|Native|18D1|4EE7|FF|42|03||physical-path";

    private sealed class RecordingSink : IUsbLogger
    {
        public List<string> Messages { get; } = [];

        public void Log(string message) => Messages.Add(message);
    }

    public class KitProxy : DispatchProxy
    {
        internal Func<string, IUsbDeviceSession?>? OpenByKey { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ICommKit.OpenUsbDeviceSessionByKey))
            {
                return OpenByKey!((string)args![0]!);
            }

            throw new InvalidOperationException("Unexpected CommKit call.");
        }
    }

    public class SessionProxy : DispatchProxy
    {
        internal UsbDeviceInfo? Info { get; set; }

        internal Exception? WriteFailure { get; set; }

        internal bool Disposed { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_DeviceInfo")
            {
                return Info!;
            }

            if (targetMethod?.Name == "get_DefaultTimeoutMs")
            {
                return 0;
            }

            if (targetMethod?.Name == nameof(IDisposable.Dispose))
            {
                Disposed = true;
                return null;
            }

            if (WriteFailure is not null && targetMethod?.Name == nameof(IUsbDeviceSession.Write))
            {
                throw WriteFailure;
            }

            throw new InvalidOperationException($"Unexpected session call: {targetMethod?.Name}.");
        }
    }

    private static UsbDeviceInfo ProbeTarget(byte interfaceProtocol) => new()
    {
        ApiName = "native",
        SourceApiKind = UsbApiKind.Native,
        DevicePath = "physical-path",
        DeviceKey = ProbeDeviceKey,
        VendorId = 0x18D1,
        ProductId = 0x4EE7,
        InterfaceClass = 0xff,
        InterfaceSubClass = 0x42,
        InterfaceProtocol = interfaceProtocol,
        InterfaceMetadataObserved = true
    };

    private static ICommKit Kit(Func<string, IUsbDeviceSession?> openByKey)
    {
        var kit = DispatchProxy.Create<ICommKit, KitProxy>();
        ((KitProxy)(object)kit).OpenByKey = openByKey;
        return kit;
    }

    private static (IUsbDeviceSession Session, SessionProxy Proxy) Session(UsbDeviceInfo info, Exception? writeFailure = null)
    {
        var session = DispatchProxy.Create<IUsbDeviceSession, SessionProxy>();
        var proxy = (SessionProxy)(object)session;
        proxy.Info = info;
        proxy.WriteFailure = writeFailure;
        return (session, proxy);
    }

    [Fact]
    public void Read_NoSessionForKey_ReturnsNullWithoutFailureReport()
    {
        var sink = new RecordingSink();

        var serial = FastbootSerialReader.Read(Kit(_ => null), ProbeTarget(3), sink);

        Assert.Null(serial);
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public void Read_DeviceIsNotFastboot_ReturnsNullWithoutFailureReport()
    {
        var sink = new RecordingSink();
        var (session, proxy) = Session(ProbeTarget(1));

        var serial = FastbootSerialReader.Read(Kit(_ => session), ProbeTarget(1), sink);

        Assert.Null(serial);
        Assert.Empty(sink.Messages);
        Assert.True(proxy.Disposed);
    }

    [Fact]
    public void Read_SessionOpenThrows_ReturnsNullAndReportsClaimExceptionToSink()
    {
        var sink = new RecordingSink();

        var serial = FastbootSerialReader.Read(
            Kit(_ => throw new UnauthorizedAccessException("Access to the USB device path was denied.")),
            ProbeTarget(3),
            sink);

        Assert.Null(serial);
        var line = Assert.Single(sink.Messages);
        Assert.StartsWith("[ERROR] ", line, StringComparison.Ordinal);
        Assert.Contains(ProbeDeviceKey, line, StringComparison.Ordinal);
        Assert.Contains(nameof(UnauthorizedAccessException), line, StringComparison.Ordinal);
        Assert.Contains("Access to the USB device path was denied.", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_GetVarWriteThrows_ReturnsNullAndReportsBusyFailureToSink()
    {
        var sink = new RecordingSink();
        var (session, proxy) = Session(
            ProbeTarget(3),
            new IOException("bulk OUT rejected: interface claimed by another driver"));

        var serial = FastbootSerialReader.Read(Kit(_ => session), ProbeTarget(3), sink);

        Assert.Null(serial);
        var line = Assert.Single(sink.Messages);
        Assert.StartsWith("[ERROR] ", line, StringComparison.Ordinal);
        Assert.Contains(ProbeDeviceKey, line, StringComparison.Ordinal);
        Assert.Contains(nameof(IOException), line, StringComparison.Ordinal);
        Assert.Contains("interface claimed by another driver", line, StringComparison.Ordinal);
        Assert.True(proxy.Disposed);
    }

    [Fact]
    public void Read_NullArguments_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => FastbootSerialReader.Read(null!, ProbeTarget(3)));
        Assert.Throws<ArgumentNullException>(() => FastbootSerialReader.Read(Kit(_ => null), null!));
    }
}