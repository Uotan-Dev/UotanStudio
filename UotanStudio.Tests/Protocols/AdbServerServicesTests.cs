using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Protocols.Adb;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Checks the adb server control-socket exchange against a fake server that, like the real one, answers one
/// host request per connection and then closes it.
/// <para>用“每条连接只应答一条 host 请求、随后关闭”的假 server 校验 adb server 控制套接字交互。</para>
/// </summary>
public sealed class AdbServerServicesTests
{
    [Fact(Timeout = 20000)]
    public async Task ListTargets_ServerServesOneHostRequestPerConnection_ReturnsTargets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string listing =
            "emulator-5554          device product:sdk_gphone64_x86_64 model:sdk model device:emu64xa transport_id:1";
        using var server = new SingleReplyAdbServer(request => request switch
        {
            "host:version" => Okay("0029"),
            "host:devices-l" => Okay(listing),
            _ => Fail("unknown host service"),
        }, expectedConnections: 2);
        var services = new AdbServerServices(string.Empty, "127.0.0.1", server.Port);

        var targets = await Task.Run(services.ListTargets, cancellationToken);

        var target = Assert.Single(targets);
        Assert.Equal("emulator-5554", target.Serial);
        Assert.Equal("device", target.State);
        Assert.Equal("TCP", target.Transport);
    }

    [Fact(Timeout = 20000)]
    public async Task ListTargets_ServerClosesWithoutReply_ThrowsDiagnosableIOException()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var server = new SingleReplyAdbServer(_ => null, expectedConnections: 1);
        var services = new AdbServerServices(string.Empty, "127.0.0.1", server.Port);

        var error = await Assert.ThrowsAsync<IOException>(
            () => Task.Run(services.ListTargets, cancellationToken));

        Assert.Contains("host:version", error.Message, StringComparison.Ordinal);
        Assert.Contains("closed the connection", error.Message, StringComparison.Ordinal);
    }

    private static string Okay(string payload) =>
        "OKAY" + Encoding.UTF8.GetByteCount(payload).ToString("X4", CultureInfo.InvariantCulture) + payload;

    private static string Fail(string message) =>
        "FAIL" + Encoding.UTF8.GetByteCount(message).ToString("X4", CultureInfo.InvariantCulture) + message;

    private sealed class SingleReplyAdbServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _serve;

        internal SingleReplyAdbServer(Func<string, string?> reply, int expectedConnections)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serve = ServeAsync(reply, expectedConnections);
        }

        internal int Port { get; }

        public void Dispose()
        {
            _lifetime.Cancel();
            _listener.Stop();
            try
            {
                _serve.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            _lifetime.Dispose();
        }

        private async Task ServeAsync(Func<string, string?> reply, int expectedConnections)
        {
            try
            {
                for (var accepted = 0; accepted < expectedConnections; accepted++)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                    var stream = client.GetStream();
                    if (await ReadRequestAsync(stream, _lifetime.Token) is not { } request)
                    {
                        continue;
                    }

                    if (reply(request) is { } response)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _lifetime.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static async Task<string?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, cancellationToken);
            if (!int.TryParse(Encoding.ASCII.GetString(header), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var length) || length is <= 0 or > 65535)
            {
                return null;
            }

            var body = new byte[length];
            await stream.ReadExactlyAsync(body, cancellationToken);
            return Encoding.ASCII.GetString(body);
        }
    }
}
