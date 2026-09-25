using System.IO.Ports;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Serial;

public class SerialPtyTests
{
    [Fact]
    public async Task ReadWriteAsync_PseudoTerminal_PreservesBinaryBytesInBothDirections()
    {
        await using var pty = await SerialPty.CreateAsync();
        using var transport = new SerialTransport(pty.SlavePath, 115200, handshake: Handshake.None);
        await transport.ConnectAsync(pty.Token);
        var bytes = Enumerable.Range(0, 512).Select(value => (byte)value).ToArray();

        await pty.WriteAsync(bytes);
        Assert.Equal(bytes, await ReadExactlyAsync(transport, bytes.Length, pty.Token));
        Assert.Equal(bytes.Length, await transport.WriteAsync(bytes, 0, bytes.Length, 1000, pty.Token));
        Assert.Equal(bytes, await pty.ReadAsync(bytes.Length));
        Assert.True(transport.IsConnected);
    }

    [Fact]
    public async Task ReadAsync_TimeoutAndReconnect_NextConnectionRemainsUsable()
    {
        await using var pty = await SerialPty.CreateAsync();
        using var transport = new SerialTransport(pty.SlavePath, 115200, handshake: Handshake.None);
        await transport.ConnectAsync(pty.Token);

        await Assert.ThrowsAsync<TimeoutException>(() => transport.ReadAsync(new byte[1], 0, 1, 100, pty.Token));
        transport.Disconnect();
        Assert.False(transport.IsConnected);
        await transport.ConnectAsync(pty.Token);
        await pty.WriteAsync([0x42]);

        Assert.Equal(new byte[] { 0x42 }, await ReadExactlyAsync(transport, 1, pty.Token));
        Assert.Equal(115200, transport.BaudRate);
    }

    [Fact]
    public async Task WriteAsync_PreCancelled_DoesNotReachPseudoTerminal()
    {
        await using var pty = await SerialPty.CreateAsync();
        using var transport = new SerialTransport(pty.SlavePath, 115200, handshake: Handshake.None);
        await transport.ConnectAsync(pty.Token);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.WriteAsync([0x42], 0, 1, 1000, cancellation.Token));

        Assert.False(await pty.HasPendingOutputAsync());
        Assert.Equal(1, await transport.WriteAsync([0x43], 0, 1, 1000, pty.Token));
        Assert.Equal(new byte[] { 0x43 }, await pty.ReadAsync(1));
    }

    [Fact]
    public async Task ReadAsync_CancelledWithoutData_CompletesBeforeBufferReuse()
    {
        await using var pty = await SerialPty.CreateAsync();
        using var transport = new SerialTransport(pty.SlavePath, 115200, handshake: Handshake.None);
        await transport.ConnectAsync(pty.Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(pty.Token);
        cancellation.CancelAfter(50);
        var buffer = new byte[1];

        var error = await Assert.ThrowsAnyAsync<Exception>(() => transport.ReadAsync(buffer, 0, 1, 200, cancellation.Token));

        Assert.True(error is OperationCanceledException or TimeoutException);
        buffer[0] = 0xA5;
        await pty.WriteAsync([0x42]);
        Assert.Equal(new byte[] { 0x42 }, await ReadExactlyAsync(transport, 1, pty.Token));
        Assert.Equal(0xA5, buffer[0]);
    }

    private static async Task<byte[]> ReadExactlyAsync(SerialTransport transport, int length, CancellationToken cancellationToken)
    {
        var bytes = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await transport.ReadAsync(bytes, offset, length - offset, 1000, cancellationToken);
            Assert.InRange(read, 1, length - offset);
            offset += read;
        }
        return bytes;
    }
}