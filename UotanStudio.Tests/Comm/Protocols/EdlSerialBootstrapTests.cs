using System.IO.Ports;
using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public class EdlSerialBootstrapTests
{
    private static SerialConnectionOptions Settings() => new()
    {
        PortName = "COM7",
        BaudRate = 115200,
        Handshake = Handshake.None
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectAsync_ExistingFirehose_ReturnsConfiguredOwnedSession(bool loader)
    {
        using var directory = new TempDirectory();
        var path = loader ? directory.WriteBytes("programmer.bin", [1, 2, 3, 4]) : null;
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("<?xml version='1.0'?><data></data>".PadRight(48));
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")];
        var settings = Settings();
        var opens = 0;

        var session = await EdlSerialBootstrap.ConnectCoreAsync(settings, path,
            new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false }, (selected, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.Same(settings, selected);
                opens++;
                if (path is not null)
                {
                    Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                }
                return Task.FromResult<ICommTransport>(transport);
            }, TestContext.Current.CancellationToken);
        using (session)
        {
            Assert.True(session.IsConfigured);
            Assert.Equal(1, opens);
            Assert.Equal(0, transport.DisposeCount);
            Assert.Contains("<configure ", Encoding.UTF8.GetString(Assert.Single(transport.Writes)), StringComparison.Ordinal);
            if (path is not null)
            {
                using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
        Assert.Equal(1, transport.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectAsync_MissingOrEmptyProgrammer_DoesNotOpenPort(bool empty)
    {
        using var directory = new TempDirectory();
        var path = empty ? directory.WriteBytes("programmer.bin", []) : Path.Combine(directory.Path, "missing.bin");
        var opens = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => EdlSerialBootstrap.ConnectCoreAsync(Settings(), path, null, (_, _) =>
        {
            opens++;
            throw new InvalidOperationException("Must not open.");
        }, TestContext.Current.CancellationToken));
        Assert.Equal(0, opens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectAsync_CancelledOrDisconnectedAfterOpen_DisposesBeforeHandshake(bool cancelled)
    {
        using var transport = new ScriptedCommTransport();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(() => EdlSerialBootstrap.ConnectCoreAsync(Settings(), null, null, (_, _) =>
        {
            if (cancelled)
            {
                cancellation.Cancel();
            }
            else
            {
                transport.Disconnect();
            }
            return Task.FromResult<ICommTransport>(transport);
        }, cancellation.Token));
        Assert.Equal(1, transport.DisposeCount);
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }
}