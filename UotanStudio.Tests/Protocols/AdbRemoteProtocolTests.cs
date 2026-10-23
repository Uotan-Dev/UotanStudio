using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Tcp;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class AdbRemoteProtocolTests
{
    [Fact]
    public void StlsVersion_Constants_MatchAosp()
    {
        Assert.Equal(0x01000000u, AdbConstants.StlsVersionMin);
        Assert.Equal(0x01000000u, AdbConstants.StlsVersion);
        Assert.True(AdbConstants.StlsVersion >= AdbConstants.StlsVersionMin);
    }

    [Fact(Timeout = 15000)]
    public void Connect_PlainTransportReceivesStls_ThrowsTlsRequiredWithoutTimeoutWait()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.False(cancellationToken.IsCancellationRequested);
        var transport = new QueueTransport();
        transport.Enqueue(AdbMessaging.Serialize(new AdbMessage(AdbCommand.Stls, AdbConstants.StlsVersion, 0)));
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);

        var stopwatch = Stopwatch.StartNew();
        var exception = Assert.Throws<AdbTlsRequiredException>(session.Connect);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "The TLS-required failure must not wait for the handshake timeout.");
        Assert.Contains("TLS", exception.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 15000)]
    public void Connect_TransportClosesBeforeCnxn_ThrowsIOExceptionWithoutTimeoutWait()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.False(cancellationToken.IsCancellationRequested);
        var transport = new QueueTransport();
        transport.Complete();
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);

        var stopwatch = Stopwatch.StartNew();
        var exception = Assert.Throws<IOException>(session.Connect);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "A closed transport must fault the handshake immediately.");
        Assert.IsNotType<TimeoutException>(exception);
    }

    [Fact(Timeout = 15000)]
    public void Connect_CnxnSplitAcrossReads_CompletesHandshake()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.False(cancellationToken.IsCancellationRequested);
        var transport = new QueueTransport();
        var cnxn = AdbMessaging.Serialize(new AdbMessage(AdbCommand.Cnxn, AdbConstants.ProtocolVersion,
            AdbConstants.MaxPayload, Encoding.UTF8.GetBytes("device::features=shell_v2,stat_v2")));
        for (var offset = 0; offset < cnxn.Length; offset += 3)
        {
            transport.Enqueue(cnxn[offset..Math.Min(offset + 3, cnxn.Length)]);
        }

        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);

        session.Connect();

        Assert.Equal(AdbConstants.ProtocolVersion, session.PeerVersion);
        Assert.NotNull(session.PeerBanner);
        Assert.Contains("features=", session.PeerBanner!, StringComparison.Ordinal);
    }

    [Fact(Timeout = 20000)]
    public void Connect_LargeFrameSplitAcrossReads_CompletesHandshake()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.False(cancellationToken.IsCancellationRequested);
        var payload = new byte[128 * 1024];
        Array.Fill(payload, (byte)0x5a);
        var transport = new QueueTransport();
        var large = AdbMessaging.Serialize(new AdbMessage(AdbCommand.Sync, 0, 0, payload));
        for (var offset = 0; offset < large.Length; offset += 4096)
        {
            transport.Enqueue(large[offset..Math.Min(offset + 4096, large.Length)]);
        }

        var cnxn = AdbMessaging.Serialize(new AdbMessage(AdbCommand.Cnxn, AdbConstants.ProtocolVersion,
            AdbConstants.MaxPayload, Encoding.UTF8.GetBytes("device::features=shell_v2")));
        for (var offset = 0; offset < cnxn.Length; offset += 4096)
        {
            transport.Enqueue(cnxn[offset..Math.Min(offset + 4096, cnxn.Length)]);
        }

        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);

        session.Connect();

        Assert.Equal(AdbConstants.ProtocolVersion, session.PeerVersion);
    }

    [Fact(Timeout = 15000)]
    public async Task PairAsync_GeneratedCommand_PutsPasswordBeforeEndpoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new SingleReplyAdbServer(
            _ => Okay("Successfully paired to 192.0.2.7:45678 [guid=abcdef]"));

        var response = await AdbServerServices.PairAsync("192.0.2.7", 45678, "abc123", "127.0.0.1",
            server.Port, cancellationToken);

        Assert.Contains("Successfully paired", response, StringComparison.Ordinal);
        Assert.Equal("host:pair:abc123:192.0.2.7:45678", await server.RequestTask);
    }

    [Fact(Timeout = 15000)]
    public async Task PairAsync_OkayThenFail_SurfacesServerFailureText()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new SingleReplyAdbServer(_ => FailAfterOkay("Wrong password"));

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            AdbServerServices.PairAsync("192.0.2.7", 45678, "abc123", "127.0.0.1", server.Port, cancellationToken));

        Assert.Contains("Wrong password", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Invalid ADB server response length", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairAsync_PasswordWithColon_IsRejectedBeforeConnecting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AdbServerServices.PairAsync("192.0.2.7", 45678, "abc:123", "127.0.0.1", 1,
                TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15000)]
    public async Task TcpTransport_Read_ReturnsArrivedBytesAndZeroAtEof()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var acceptTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
        using var transport = new AdbTcpTransport("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await acceptTask;
        var stream = server.GetStream();
        var buffer = new byte[16];

        await stream.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
        Assert.Equal(3, transport.Read(buffer, 0, buffer.Length, 2000));

        await stream.WriteAsync(new byte[] { 4, 5, 6, 7 }, cancellationToken);
        Assert.Equal(4, await transport.ReadAsync(buffer, 0, buffer.Length, 2000, cancellationToken));

        server.Client.Shutdown(SocketShutdown.Send);
        Assert.Equal(0, transport.Read(buffer, 0, buffer.Length, 2000));
    }

    [Fact(Timeout = 15000)]
    public async Task TcpTransport_ReadWithoutArrivingBytes_TimesOut()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var acceptTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
        using var transport = new AdbTcpTransport("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await acceptTask;
        Assert.NotNull(server);
        var buffer = new byte[1];

        Assert.Throws<TimeoutException>(() => transport.Read(buffer, 0, buffer.Length, 200));
    }

    private static string Okay(string payload) =>
        "OKAY" + Encoding.UTF8.GetByteCount(payload).ToString("X4", CultureInfo.InvariantCulture) + payload;

    private static string FailAfterOkay(string message) =>
        "OKAY" + "FAIL" + Encoding.UTF8.GetByteCount(message).ToString("X4", CultureInfo.InvariantCulture) + message;

    private sealed class QueueTransport : ICommTransport
    {
        private readonly Lock _gate = new();
        private readonly Queue<byte[]> _chunks = new();
        private readonly SemaphoreSlim _available = new(0);
        private byte[]? _pending;
        private int _pendingOffset;
        private bool _eof;

        public bool IsConnected { get; private set; } = true;

        public void Enqueue(byte[] chunk)
        {
            lock (_gate)
            {
                _chunks.Enqueue(chunk);
            }

            _available.Release();
        }

        public void Complete()
        {
            lock (_gate)
            {
                _eof = true;
                IsConnected = false;
            }

            _available.Release();
        }

        public void Connect()
        {
        }

        public void Disconnect() => Complete();

        public void Reset()
        {
        }

        public int Read(byte[] buffer, int offset, int length, int timeoutMs)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (TryReadLocked(buffer, offset, length, out var read))
                    {
                        return read;
                    }

                    if (_eof)
                    {
                        return 0;
                    }
                }

                if (!_available.Wait(timeoutMs > 0 ? timeoutMs : Timeout.Infinite))
                {
                    throw new TimeoutException("No ADB bytes arrived.");
                }
            }
        }

        public async Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (TryReadLocked(buffer, offset, length, out var read))
                    {
                        return read;
                    }

                    if (_eof)
                    {
                        return 0;
                    }
                }

                if (timeoutMs > 0)
                {
                    if (!await _available.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken)
                            .ConfigureAwait(false))
                    {
                        throw new TimeoutException("No ADB bytes arrived.");
                    }
                }
                else
                {
                    await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public long Write(byte[] data, int offset, int length, int timeoutMs) => length;

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default) => Task.FromResult<long>(length);

        public Stream GetStream() => throw new NotSupportedException();

        public void Dispose() => Complete();

        private bool TryReadLocked(byte[] buffer, int offset, int length, out int read)
        {
            read = 0;
            if (_pending is null)
            {
                if (_chunks.Count == 0)
                {
                    return false;
                }

                _pending = _chunks.Dequeue();
                _pendingOffset = 0;
                _available.Wait(0);
            }

            var count = Math.Min(length, _pending.Length - _pendingOffset);
            Array.Copy(_pending, _pendingOffset, buffer, offset, count);
            _pendingOffset += count;
            if (_pendingOffset == _pending.Length)
            {
                _pending = null;
            }

            read = count;
            return true;
        }
    }

    private sealed class SingleReplyAdbServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<string, string> _respond;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource<string> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serve;
        private bool _disposed;

        public SingleReplyAdbServer(Func<string, string> respond)
        {
            _respond = respond;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serve = ServeAsync();
        }

        public int Port { get; }

        public Task<string> RequestTask => _request.Task;

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            _listener.Stop();
            try
            {
                await _serve.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            _lifetime.Dispose();
        }

        private async Task ServeAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
            var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, _lifetime.Token);
            _request.TrySetResult(request);
            var response = Encoding.ASCII.GetBytes(_respond(request));
            await stream.WriteAsync(response, _lifetime.Token);
        }

        private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, cancellationToken);
            if (!int.TryParse(Encoding.ASCII.GetString(header), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var length) || length is < 1 or > 65535)
            {
                throw new InvalidDataException("Invalid test request length.");
            }

            var body = new byte[length];
            await stream.ReadExactlyAsync(body, cancellationToken);
            return Encoding.ASCII.GetString(body);
        }
    }
}