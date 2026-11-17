using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Transports.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Regression tests for Android 4.4-era devices whose adbd banner advertises no shell_v2 feature.
/// <para>针对横幅未宣告 shell_v2 特性的 Android 4.4 时代设备的回归测试。</para>
/// </summary>
public sealed class AdbLegacyShellTests
{
    // Banner captured from the log for the Xiaomi Mi 3 "pisces" (Android 4.4.4): no features= field.
    private const string LegacyBanner = "device::ro.product.name=pisces;ro.product.model=MI 3;ro.product.device=pisces;";

    private const string ModernBanner = "device::ro.product.name=pisces;features=shell_v2,cmd";

    [Fact(Timeout = 15000)]
    public async Task ExecuteAsync_LegacyDeviceWithoutShellV2_UsesLegacyShellAndReturnsOutput()
    {
        var transport = new LegacyShellTransport(LegacyBanner);
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        var shell = session.CreateShellClient("getprop ro.product.model");

        var result = await shell.ExecuteAsync(2000, TestContext.Current.CancellationToken);

        Assert.Equal("MI 3", Encoding.UTF8.GetString(result.Stdout).Trim());
        Assert.Empty(result.Stderr);
        var openServices = transport.OpenServices;
        Assert.Contains(openServices, s => s.StartsWith("shell:", StringComparison.Ordinal));
        Assert.DoesNotContain(openServices, s => s.StartsWith("shell,v2:", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15000)]
    public async Task ExecuteAsync_ModernDevice_StillUsesShellV2()
    {
        var transport = new LegacyShellTransport(ModernBanner);
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        var shell = session.CreateShellClient("getprop ro.product.model");

        var result = await shell.ExecuteAsync(2000, TestContext.Current.CancellationToken);

        Assert.Equal("MI 3", Encoding.UTF8.GetString(result.Stdout).Trim());
        var openServices = transport.OpenServices;
        Assert.Contains(openServices, s => s.StartsWith("shell,v2:", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15000)]
    public async Task ExecuteAsync_LegacyDevice_StreamsRawOutputToCallback()
    {
        var transport = new LegacyShellTransport(LegacyBanner);
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        var shell = session.CreateShellClient("echo hi");
        var chunks = new List<string>();

        var exit = await shell.ExecuteStreamingAsync(
            data => chunks.Add(Encoding.UTF8.GetString(data)),
            _ => throw new InvalidOperationException("Legacy stderr callback must not be used."),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Contains("MI 3", string.Concat(chunks), StringComparison.Ordinal);
    }

    /// <summary>
    /// Fake device that rejects shell,v2 with CLSE (matching the captured log) and serves shell: with
    /// plain WRTE packets followed by CLSE, the way Android 4.4 adbd behaves.
    /// <para>假设备：对 shell,v2 直接回 CLSE（与日志一致），对 shell: 用普通 WRTE 包后跟 CLSE 应答，
    /// 模拟 Android 4.4 adbd 的行为。</para>
    /// </summary>
    private sealed class LegacyShellTransport : ICommTransport
    {
        private readonly Lock _gate = new();
        private readonly Queue<byte[]> _inbound = new();
        private readonly Queue<string> _openServices = new();
        private readonly SemaphoreSlim _available = new(0);
        private byte[]? _pending;
        private int _pendingOffset;
        private bool _eof;
        private uint _nextRemoteId = 100;

        public LegacyShellTransport(string banner)
        {
            Deliver(AdbMessaging.Serialize(new AdbMessage(AdbCommand.Cnxn,
                AdbConstants.ProtocolVersionMin, 4096, Encoding.UTF8.GetBytes(banner))));
        }

        public bool IsConnected => !_eof;

        public IReadOnlyList<string> OpenServices
        {
            get { lock (_gate) { return [.. _openServices]; } }
        }

        public void Connect() { }
        public void Disconnect() => Complete();
        public void Reset() { }

        public void Complete()
        {
            lock (_gate) { _eof = true; }
            _available.Release();
        }

        public int Read(byte[] buffer, int offset, int length, int timeoutMs)
            => ReadAsync(buffer, offset, length, timeoutMs, TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult();

        public async Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (TryReadLocked(buffer, offset, length, out var read)) return read;
                    if (_eof) return 0;
                }

                if (timeoutMs > 0)
                {
                    if (!await _available.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken)
                            .ConfigureAwait(false))
                        throw new TimeoutException("No ADB bytes arrived.");
                }
                else
                {
                    await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public long Write(byte[] data, int offset, int length, int timeoutMs)
        {
            HandleWrite(data.AsSpan(offset, length).ToArray());
            return length;
        }

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            HandleWrite(data.AsSpan(offset, length).ToArray());
            return Task.FromResult<long>(length);
        }

        public Stream GetStream() => throw new NotSupportedException();

        public void Dispose() => Complete();

        private byte[]? _pendingOpenHeader;

        private void HandleWrite(byte[] frame)
        {
            if (frame.Length == AdbConstants.HeaderSize &&
                Encoding.ASCII.GetString(frame, 0, 4) == "OPEN")
            {
                _pendingOpenHeader = frame;
                return;
            }

            if (_pendingOpenHeader is not { } header)
            {
                return;
            }

            _pendingOpenHeader = null;
            var arg0 = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            var service = Encoding.UTF8.GetString(frame).TrimEnd('\0');
            lock (_gate) { _openServices.Enqueue(service); }

            var remoteId = _nextRemoteId++;
            if (service.StartsWith("shell,v2:", StringComparison.Ordinal))
            {
                Deliver(AdbMessaging.Serialize(new AdbMessage(AdbCommand.Okay, remoteId, arg0, [])));
                var output = Encoding.UTF8.GetBytes("MI 3\n");
                var stdoutFrame = new byte[5 + output.Length];
                stdoutFrame[0] = 1;
                BinaryPrimitives.WriteInt32LittleEndian(stdoutFrame.AsSpan(1), output.Length);
                output.CopyTo(stdoutFrame, 5);
                var exitFrame = new byte[] { 3, 1, 0, 0, 0, 0 };
                Deliver(AdbMessaging.Serialize(new AdbMessage(AdbCommand.Wrte, remoteId, arg0, stdoutFrame)));
                Deliver(AdbMessaging.Serialize(new AdbMessage(AdbCommand.Wrte, remoteId, arg0, exitFrame)));
                Deliver(AdbMessaging.Serialize(new AdbMessage(AdbCommand.Clse, remoteId, arg0, [])));
                return;
            }

            if (service.StartsWith("shell:", StringComparison.Ordinal))
            {
                Deliver(AdbMessaging.Serialize(new AdbMessage(AdbCommand.Okay, remoteId, arg0, [])));
                Deliver(AdbMessaging.Serialize(new AdbMessage(AdbCommand.Wrte, remoteId, arg0,
                    Encoding.UTF8.GetBytes("MI 3\n"))));
                Deliver(AdbMessaging.Serialize(new AdbMessage(AdbCommand.Clse, remoteId, arg0, [])));
            }
        }

        private void Deliver(byte[] frame)
        {
            lock (_gate) { _inbound.Enqueue(frame); }
            _available.Release();
        }

        private bool TryReadLocked(byte[] buffer, int offset, int length, out int read)
        {
            read = 0;
            if (_pending is null)
            {
                if (_inbound.Count == 0) return false;
                _pending = _inbound.Dequeue();
                _pendingOffset = 0;
                _available.Wait(0);
            }

            var count = Math.Min(length, _pending.Length - _pendingOffset);
            Array.Copy(_pending, _pendingOffset, buffer, offset, count);
            _pendingOffset += count;
            if (_pendingOffset == _pending.Length) _pending = null;
            read = count;
            return true;
        }
    }
}