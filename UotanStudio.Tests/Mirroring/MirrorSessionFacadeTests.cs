using System.Text;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Mirroring;
using Xunit;
using AdbShellResult = UotanStudio.Comm.Protocols.Adb.ShellResult;
using HdcShellResult = UotanStudio.Comm.Protocols.Hdc.ShellResult;

namespace UotanStudio.Tests.Mirroring;

public sealed class MirrorSessionFacadeTests
{
    [Fact]
    public void StartScrcpyOverAdbServer_NullServer_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MirrorSessionFacade.StartScrcpyOverAdbServer(null!, "scrcpy-server.jar", new ScrcpyServerOptions()));
    }

    [Fact]
    public void StartScrcpyOverAdbServer_BlankJarPath_ThrowsArgumentException()
    {
        var server = new AdbServerServices("serial");

        Assert.Throws<ArgumentException>(() =>
            MirrorSessionFacade.StartScrcpyOverAdbServer(server, " ", new ScrcpyServerOptions()));
    }

    [Fact]
    public void StartScrcpyOverAdbServer_NullOptions_ThrowsArgumentNullException()
    {
        var server = new AdbServerServices("serial");

        Assert.Throws<ArgumentNullException>(() =>
            MirrorSessionFacade.StartScrcpyOverAdbServer(server, "scrcpy-server.jar", null!));
    }

    [Fact]
    public void StartScrcpyOverUsbSession_NullSession_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MirrorSessionFacade.StartScrcpyOverUsbSession(null!, "scrcpy-server.jar", new ScrcpyServerOptions()));
    }

    [Fact]
    public void StartScrcpyOverUsbSession_BlankJarPath_ThrowsArgumentException()
    {
        using var session = new StubAdbSession();

        Assert.Throws<ArgumentException>(() =>
            MirrorSessionFacade.StartScrcpyOverUsbSession(session, " ", new ScrcpyServerOptions()));
    }

    [Fact]
    public void StartScrcpyOverUsbSession_NullOptions_ThrowsArgumentNullException()
    {
        using var session = new StubAdbSession();

        Assert.Throws<ArgumentNullException>(() =>
            MirrorSessionFacade.StartScrcpyOverUsbSession(session, "scrcpy-server.jar", null!));
    }

    [Fact]
    public void StartScrcpyOverUsbSession_ServerManagedSession_ThrowsArgumentException()
    {
        using var session = new StubAdbSession { UsesServer = true };

        var error = Assert.Throws<ArgumentException>(() =>
            MirrorSessionFacade.StartScrcpyOverUsbSession(session, "scrcpy-server.jar", new ScrcpyServerOptions()));

        Assert.Equal("session", error.ParamName);
    }

    [Fact]
    public void StartOhscrcpyOverHdcSession_NullSession_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MirrorSessionFacade.StartOhscrcpyOverHdcSession(null!, "5XM0225C18001159"));
    }

    [Fact]
    public void StartOhscrcpyOverHdcSession_BlankSerial_ThrowsArgumentException()
    {
        using var session = new StubHdcSession();

        Assert.Throws<ArgumentException>(() =>
            MirrorSessionFacade.StartOhscrcpyOverHdcSession(session, " "));
    }

    [Fact]
    public void StartOhscrcpyOverHdcSession_NonHdcProtocolStub_ThrowsArgumentException()
    {
        using var session = new StubHdcSession();

        var error = Assert.Throws<ArgumentException>(() =>
            MirrorSessionFacade.StartOhscrcpyOverHdcSession(session, "5XM0225C18001159"));

        Assert.Equal("session", error.ParamName);
    }

    [Fact]
    public void StartOhscrcpyOverHdcSession_PortOutOfRange_ThrowsArgumentOutOfRangeException()
    {
        using var session = new StubHdcSession();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MirrorSessionFacade.StartOhscrcpyOverHdcSession(
                session, "5XM0225C18001159", localForwardPort: 70000));
    }

    [Fact]
    public void StartOhscrcpyOverHdcSession_InvalidCaptureOptions_ThrowsArgumentOutOfRangeException()
    {
        using var session = new StubHdcSession();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MirrorSessionFacade.StartOhscrcpyOverHdcSession(
                session, "5XM0225C18001159", new OhscrcpyLaunchOptions(FrameRate: 0)));
    }

    private sealed class StubHdcSession : IHdcDeviceSession
    {
        public string ProtocolName => "hdc";

        public bool IsConnected => true;

        public IHdcForwardManager Forwards => throw new NotSupportedException();

        public void Connect() => throw new NotSupportedException();

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public HdcShellResult ExecuteCommand(string command, int timeoutMs = 30000)
            => new(Encoding.UTF8.GetBytes("error: bundle not found"), [], -1);

        public IHdcShellClient CreateShellClient(string command) => throw new NotSupportedException();

        public IHdcDeviceServices CreateDeviceServices() => throw new NotSupportedException();

        public IHdcSyncClient CreateSyncClient() => throw new NotSupportedException();

        public IHdcAppClient CreateAppClient() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class StubAdbSession : IAdbDeviceSession
    {
        public bool UsesServer { get; init; }

        public string ProtocolName => "adb";

        public bool IsConnected => true;

        public uint PeerVersion => 0;

        public void Connect() => throw new NotSupportedException();

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IAdbStream OpenStream(string service) => throw new NotSupportedException();

        public Stream OpenServiceStream(string service) => throw new NotSupportedException();

        public IAdbShellClient CreateShellClient(string command, string? term = null, bool pty = false)
            => throw new NotSupportedException();

        public IAdbSyncClient CreateSyncClient(uint maxChunk = 64 * 1024) => throw new NotSupportedException();

        public IAdbDeviceServices CreateDeviceServices() => throw new NotSupportedException();

        public AdbShellResult ExecuteCommand(string command, int timeoutMs = 30000) => new([], [], 0);

        public ValueTask<AdbShellResult> ExecuteCommandAsync(string command,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new AdbShellResult([], [], 0));

        public void Dispose()
        {
        }
    }
}