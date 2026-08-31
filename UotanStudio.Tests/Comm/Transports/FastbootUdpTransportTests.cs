using System.Net;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Transports.Udp;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports;

/// <summary>
/// Fastboot over UDP transport tests against a fake in-process UDP target that implements the
/// AOSP packet handshake (DeviceQuery / Initialization) and echoes Fastboot data / responses.
/// <para>基于进程内假 UDP 对端的 Fastboot UDP 传输测试：实现 AOSP 数据包握手
/// （DeviceQuery / Initialization）并回显 Fastboot 数据/响应。</para>
/// </summary>
public class FastbootUdpTransportTests
{
    private const byte PacketIdDeviceQuery = 0x01;
    private const byte PacketIdInitialization = 0x02;
    private const byte PacketIdFastboot = 0x03;

    [Fact]
    public void FastbootUdpTransport_Constructor_ValidatesArguments()
    {
        Assert.Throws<ArgumentException>(() => new FastbootUdpTransport("  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FastbootUdpTransport("127.0.0.1", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FastbootUdpTransport("127.0.0.1", 5554, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FastbootUdpTransport("127.0.0.1", 5554, 1000, 0));
    }

    [Fact]
    public async Task FastbootUdpTransport_HandshakeAndWrite_ReachesTarget()
    {
        using var target = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)target.Client.LocalEndPoint).Port;
        var received = new List<byte[]>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var targetTask = RunFakeTargetAsync(target, received, cts.Token);

        using var client = new FastbootUdpTransport("127.0.0.1", port, timeoutMs: 1000, maxTransmissionAttempts: 3);
        Assert.True(client.IsConnected);

        var data = Encoding.ASCII.GetBytes("getvar:version");
        var written = client.Write(data, 0, data.Length, 2000);
        Assert.Equal(data.Length, written);

        // Wait until the target observed the fastboot data packet.
        // 等待对端观察到 fastboot 数据包。
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            if (received.Any(p => p.SequenceEqual(data)))
            {
                break;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Contains(received, p => p.SequenceEqual(data));
    }

    [Fact]
    public async Task FastbootUdpTransport_Read_ReturnsTargetPayload()
    {
        using var target = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)target.Client.LocalEndPoint).Port;
        var received = new List<byte[]>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var targetTask = RunFakeTargetAsync(target, received, cts.Token);

        using var client = new FastbootUdpTransport("127.0.0.1", port, timeoutMs: 1000, maxTransmissionAttempts: 3);

        var expected = Encoding.ASCII.GetBytes("INFOthis is a read payload");
        var buffer = new byte[expected.Length + 16];
        var read = client.Read(buffer, 0, buffer.Length, 2000);

        Assert.Equal(expected.Length, read);
        Assert.Equal(expected, buffer[..read]);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task FastbootUdpTransport_GetStream_IsNotSupported()
    {
        using var target = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)target.Client.LocalEndPoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var targetTask = RunFakeTargetAsync(target, [], cts.Token);

        using var client = new FastbootUdpTransport("127.0.0.1", port, timeoutMs: 1000, maxTransmissionAttempts: 3);

        Assert.Throws<NotSupportedException>(() => client.GetStream());
    }

    /// <summary>
    /// Runs the fake UDP target: DeviceQuery -> seq 0, Initialization -> version 1 / size 2048,
    /// Fastboot empty payload -> echo payload bytes, Fastboot with payload -> empty ACK.
    /// <para>运行假 UDP 对端：DeviceQuery 回 seq 0；Initialization 回版本 1/大小 2048；
    /// Fastboot 空负载回显负载字节；Fastboot 带负载回空 ACK。</para>
    /// </summary>
    private static async Task RunFakeTargetAsync(UdpClient target, List<byte[]> received, CancellationToken ct)
    {
        var remote = new IPEndPoint(IPAddress.Loopback, 0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var rx = await target.ReceiveAsync(ct);
                if (rx.Buffer.Length < 4)
                {
                    continue;
                }

                var id = rx.Buffer[0];
                var seq = (ushort)((rx.Buffer[2] << 8) | rx.Buffer[3]);
                var payload = rx.Buffer[4..];
                byte[] response;

                switch (id)
                {
                    case PacketIdDeviceQuery:
                        // [id, flag, seqHi, seqLo, seq=0 (2 bytes)] — the client needs >=4 bytes
                        // on the wire and a 2-byte collected payload.
                        // [id, flag, seqHi, seqLo, seq=0（2 字节）]——客户端需要线缆上 >=4 字节，
                        // 且收集到 2 字节负载。
                        response = [PacketIdDeviceQuery, 0x00, 0x00, 0x00, 0x00, 0x00];
                        break;
                    case PacketIdInitialization:
                        // version=1, packetSize=2048 (0x0800) as the collected 4-byte payload.
                        // 收集负载 4 字节：版本 1、包大小 2048（0x0800）。
                        response = [PacketIdInitialization, 0x00, 0x00, 0x00, 0x00, 0x01, 0x08, 0x00];
                        break;
                    case PacketIdFastboot:
                        if (payload.Length == 0)
                        {
                            // Read request: echo the read payload back.
                            // 读取请求：回显读取负载。
                            var readPayload = Encoding.ASCII.GetBytes("INFOthis is a read payload");
                            response = new byte[4 + readPayload.Length];
                            response[0] = PacketIdFastboot;
                            response[1] = 0x00;
                            response[2] = (byte)(seq >> 8);
                            response[3] = (byte)seq;
                            readPayload.CopyTo(response, 4);
                        }
                        else
                        {
                            // Write request: record the payload and send an empty ACK.
                            // 写入请求：记录负载并回空 ACK。
                            lock (received)
                            {
                                received.Add([.. payload]);
                            }

                            response = [PacketIdFastboot, 0x00, (byte)(seq >> 8), (byte)seq];
                        }

                        break;
                    default:
                        continue;
                }

                await target.SendAsync(response.AsMemory(), rx.RemoteEndPoint, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (SocketException)
        {
            // Expected when the socket is closed at teardown.
        }
    }
}
