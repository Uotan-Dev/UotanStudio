using System.IO.Ports;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class MtkSessionTests
{
    private static SerialConnectionOptions ExplicitSerial() => new()
    {
        PortName = "COM7",
        BaudRate = 115200,
        Handshake = Handshake.None
    };

    [Fact]
    public async Task ConnectSerialAsync_ValidLoader_OpensOnceWithRetainedResourcesAndTransfersOwnership()
    {
        using var dir = new TempDirectory();
        var options = BootstrapOptions(dir);
        var serial = ExplicitSerial();
        using var transport = new ScriptedCommTransport();
        EnqueueBootstrap(transport, false);
        var opens = 0;

        var session = await MtkXFlashBootstrap.ConnectSerialCoreAsync(serial, options, (settings, token) =>
        {
            token.ThrowIfCancellationRequested();
            opens++;
            Assert.Same(serial, settings);
            Assert.Throws<IOException>(() => File.Open(options.LoaderPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            return Task.FromResult<ICommTransport>(transport);
        }, TestContext.Current.CancellationToken);
        using (session)
        {
            Assert.Equal(1, opens);
            Assert.Equal(0, transport.DisposeCount);
            Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
            using var exclusive = File.Open(options.LoaderPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        Assert.Equal(1, transport.DisposeCount);
    }

    [Theory]
    [InlineData("loader")]
    [InlineData("settings")]
    [InlineData("cancel")]
    public async Task ConnectSerialAsync_LocalPreflightFailure_DoesNotOpenPort(string failure)
    {
        using var dir = new TempDirectory();
        var options = BootstrapOptions(dir);
        var serial = ExplicitSerial();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (failure == "loader")
        {
            options = options with { StageOneRegionIndex = 999 };
        }
        else if (failure == "settings")
        {
            serial = serial with { BaudRate = 0 };
        }
        else
        {
            cancellation.Cancel();
        }
        var opens = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => MtkXFlashBootstrap.ConnectSerialCoreAsync(serial, options, (_, _) =>
        {
            opens++;
            throw new InvalidOperationException("Must not open.");
        }, cancellation.Token));
        Assert.Equal(0, opens);
        using var exclusive = File.Open(options.LoaderPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task ConnectSerialAsync_CancelledAfterOpen_DisposesTransportBeforeHandshake()
    {
        using var dir = new TempDirectory();
        var options = BootstrapOptions(dir);
        using var transport = new ScriptedCommTransport();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MtkXFlashBootstrap.ConnectSerialCoreAsync(
            ExplicitSerial(), options, (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult<ICommTransport>(transport);
            }, cancellation.Token));

        Assert.Equal(1, transport.DisposeCount);
        Assert.Empty(transport.Writes);
        using var exclusive = File.Open(options.LoaderPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task ConnectSerialAsync_OpenFailure_ReleasesLoaderWithoutRetry()
    {
        using var dir = new TempDirectory();
        var options = BootstrapOptions(dir);
        var error = new IOException("Open failed.");
        var opens = 0;

        var actual = await Assert.ThrowsAsync<IOException>(() => MtkXFlashBootstrap.ConnectSerialCoreAsync(
            ExplicitSerial(), options, (_, _) =>
            {
                opens++;
                return Task.FromException<ICommTransport>(error);
            }, TestContext.Current.CancellationToken));

        Assert.Same(error, actual);
        Assert.Equal(1, opens);
        using var exclusive = File.Open(options.LoaderPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}