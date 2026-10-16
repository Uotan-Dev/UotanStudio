using System.IO.Ports;
using UotanStudio.Comm.Transports.Serial;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Serial;

public class SerialTransportTests
{
    [Theory]
    [InlineData("", 5000)]
    [InlineData("COM1", 0)]
    [InlineData("COM1", -2)]
    public void Connect_ConfigurationRejected_DisposesUnpublishedPort(string name, int timeout)
    {
        var disposed = 0;
        using var transport = new SerialTransport(name, () =>
        {
            var port = new SerialPort();
            port.Disposed += (_, _) => disposed++;
            return port;
        })
        { DefaultTimeoutMs = timeout };

        Assert.Throws<ArgumentException>(transport.Connect);

        Assert.Equal(1, disposed);
        Assert.False(transport.IsConnected);
        transport.Disconnect();
        transport.Dispose();
        Assert.Equal(1, disposed);
    }

    [Fact]
    public async Task ConnectAsync_RepeatedConfigurationFailure_DisposesEveryCreatedPort()
    {
        var created = 0;
        var disposed = 0;
        using var transport = new SerialTransport("COM1", () =>
        {
            Interlocked.Increment(ref created);
            var port = new SerialPort();
            port.Disposed += (_, _) => Interlocked.Increment(ref disposed);
            return port;
        })
        { DefaultTimeoutMs = 0 };

        await Task.WhenAll(
            Assert.ThrowsAsync<ArgumentException>(() => transport.ConnectAsync(TestContext.Current.CancellationToken)),
            Assert.ThrowsAsync<ArgumentException>(() => transport.ConnectAsync(TestContext.Current.CancellationToken)));

        Assert.Equal(2, created);
        Assert.Equal(2, disposed);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_PreCancelled_DoesNotCreatePort()
    {
        var created = 0;
        using var transport = new SerialTransport("COM1", () =>
        {
            created++;
            return new SerialPort();
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ConnectAsync(cancellation.Token));

        Assert.Equal(0, created);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task Dispose_BeforeConnect_RejectsOperationsWithoutCreatingPort()
    {
        var created = 0;
        using var transport = new SerialTransport("COM1", () =>
        {
            created++;
            return new SerialPort();
        });
        transport.Dispose();
        transport.Dispose();
        transport.Disconnect();

        Assert.Throws<ObjectDisposedException>(transport.Connect);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.ConnectAsync(TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(transport.GetStream);
        Assert.Throws<ObjectDisposedException>(() => transport.Read(new byte[1], 0, 1, 10));
        Assert.Throws<ObjectDisposedException>(() => transport.Write(new byte[1], 0, 1, 10));
        Assert.Equal(0, created);
    }

    [Fact]
    public void DisconnectedTransport_ReadWriteAndStream_RejectWithoutPortAccess()
    {
        using var transport = new SerialTransport("COM1");

        Assert.Throws<InvalidOperationException>(transport.GetStream);
        Assert.Throws<InvalidOperationException>(() => transport.Read(new byte[1], 0, 1, 10));
        Assert.Throws<InvalidOperationException>(() => transport.Write(new byte[1], 0, 1, 10));
        Assert.False(transport.IsConnected);
        Assert.Equal(3000000, transport.BaudRate);
    }
}