using System.Net;
using System.Net.Sockets;
using UotanStudio.Comm.Transports.Tcp;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Covers the bounded TCP port scan that recovers adbd's secure connect port when the device advertises no mDNS
/// instance: the listener is found, an accepted port ends the scan, cancellation propagates and arguments are checked.
/// <para>覆盖设备不广播任何 mDNS 实例时用于恢复 adbd 安全连接端口的有界 TCP 端口扫描：能找到监听端口、被接受的端口
/// 会结束扫描、取消会被传播、参数会被校验。</para>
/// </summary>
public sealed class TcpPortScannerTests
{
    [Fact]
    public async Task ScanAsync_ListenerOnLoopback_ReportsItsPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var first = Math.Max(1, port - 2);
            var last = Math.Min(65535, port + 2);

            var result = await TcpPortScanner.ScanAsync("127.0.0.1", first, last, 4,
                TimeSpan.FromMilliseconds(250), accept: null, TestContext.Current.CancellationToken);

            Assert.Contains(port, result.OpenPorts);
            Assert.Equal(0, result.AcceptedPort);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ScanAsync_AcceptCallback_ApprovesListenerAndStops()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var result = await TcpPortScanner.ScanAsync("127.0.0.1", port, Math.Min(65535, port + 256), 8,
                TimeSpan.FromMilliseconds(250), (open, _) => Task.FromResult(open == port),
                TestContext.Current.CancellationToken);

            Assert.Equal(port, result.AcceptedPort);
            Assert.Contains(port, result.OpenPorts);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ScanAsync_CanceledToken_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TcpPortScanner.ScanAsync("127.0.0.1", 1,
            65535, 8, TimeSpan.FromMilliseconds(250), accept: null, cancellation.Token));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 9)]
    [InlineData(10, 65536)]
    public async Task ScanAsync_InvalidRange_Throws(int firstPort, int lastPort)
        => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TcpPortScanner.ScanAsync("127.0.0.1",
            firstPort, lastPort, 4, TimeSpan.FromMilliseconds(100), accept: null,
            TestContext.Current.CancellationToken));

    [Fact]
    public async Task ScanAsync_BlankHost_Throws() =>
        await Assert.ThrowsAnyAsync<ArgumentException>(() => TcpPortScanner.ScanAsync(" ", 1, 10, 4,
            TimeSpan.FromMilliseconds(100), accept: null, TestContext.Current.CancellationToken));
}