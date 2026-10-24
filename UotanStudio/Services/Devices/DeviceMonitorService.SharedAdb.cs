using UotanStudio.Comm;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// One directly opened ADB session shared by every host consumer of the same physical device. An ADB
    /// connection multiplexes streams, so log capture, shell commands, property queries, the root probe and the
    /// mirror server must reuse one exclusive endpoint lease instead of opening a competing one.
    /// <para>同一台物理设备的所有宿主消费方共享的一次直连 ADB 会话。ADB 连接复用多条流，因此日志抓取、
    /// shell 命令、属性查询、root 探测与投屏服务端必须复用同一份独占端点租约，而不是各自再开一份。</para>
    /// </summary>
    internal sealed class SharedAdbSession(DeviceLease lease, IAdbDeviceSession session)
    {
        public DeviceLease Lease { get; } = lease;

        public IAdbDeviceSession Session { get; } = session;

        /// <summary>Borrowers still holding the session; the last one releases the lease and the transport.
        /// <para>仍持有会话的借用方数量；最后一个借用方释放租约与传输。</para></summary>
        public int References { get; set; }

        /// <summary>Whether the session left the pool because it failed; it is disposed once its borrowers return.
        /// <para>会话是否因失败离开池；其借用方全部归还后释放。</para></summary>
        public bool Discarded { get; set; }
    }

    /// <summary>
    /// A borrower of the shared session. Disposing it returns one reference and lets the last borrower release
    /// the lease, the session and the endpoint reservation; the mirror hands it over as the lease that owns the
    /// borrowed session.
    /// <para>共享会话的借用方。释放时归还一次引用；最后一个借用方释放租约、会话与端点占用。投屏把它作为
    /// 持有借用会话的租约接收。</para>
    /// </summary>
    internal sealed class SharedAdbScope : IDisposable, IAsyncDisposable
    {
        private readonly DeviceMonitorService _owner;
        private readonly SharedAdbSession _shared;
        private int _disposed;

        internal SharedAdbScope(DeviceMonitorService owner, SharedAdbSession shared)
        {
            _owner = owner;
            _shared = shared;
        }

        internal IAdbDeviceSession Session => _shared.Session;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.ReleaseSharedAdbSession(_shared);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private readonly Lock _sharedAdbLock = new();
    private readonly Lock _sharedAdbOpenLock = new();
    private readonly Dictionary<string, SharedAdbSession> _sharedAdbSessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether host ADB work for this device should reuse the shared direct session instead of opening a new
    /// exclusive transport. Server and network targets keep their per-call sessions.
    /// <para>该设备的宿主 ADB 工作是否应复用共享直连会话，而不是新开独占传输。server 与网络目标仍按每次调用开会话。</para>
    /// </summary>
    /// <param name="device">The device about to run ADB work. <para>即将执行 ADB 工作的设备。</para></param>
    /// <returns><see langword="true"/> when the direct session is shared.
    /// <para>直连会话应共享时为 <see langword="true"/>。</para></returns>
    internal static bool UsesSharedAdbSession(DetectedDevice device)
        => device.Protocol == DeviceProtocolKind.Adb
            && !device.Key.StartsWith("adb:", StringComparison.Ordinal)
            && !device.Key.StartsWith("adb-tcp:", StringComparison.Ordinal)
            && !DebugBridgeSessionOpener.AdbServerOwns(device.Serial);

    /// <summary>
    /// Borrows the shared direct ADB session for the device, opening and connecting it once when no borrower
    /// holds it. A second borrower never opens a competing transport, so a long log capture no longer makes
    /// every other host operation fail with a busy endpoint.
    /// <para>借用该设备的共享直连 ADB 会话；无人持有时只打开并连接一次。第二个借用方绝不会再开一份竞争的
    /// 传输，因此长时间的日志抓取不再让其它宿主操作以端点占用失败。</para>
    /// </summary>
    /// <param name="device">The direct-USB ADB device whose session is borrowed.
    /// <para>要借用其会话的直连 USB ADB 设备。</para></param>
    /// <param name="cancellationToken">Checked before the session is opened and before borrowing.
    /// <para>打开会话前与借用前检查。</para></param>
    /// <returns>The borrow scope; disposing it returns the session to the pool.
    /// <para>借用作用域；释放后会话归还池中。</para></returns>
    /// <exception cref="InvalidOperationException">The endpoint is held by another owner, so no session can be opened.
    /// <para>端点被其它持有者占用，无法打开会话。</para></exception>
    internal SharedAdbScope AcquireSharedAdbSession(DetectedDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();
        var key = SharedAdbKey(device);
        lock (_sharedAdbOpenLock)
        {
            SharedAdbSession? stale = null;
            lock (_sharedAdbLock)
            {
                if (_sharedAdbSessions.TryGetValue(key, out var existing))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (existing.Session.IsConnected)
                    {
                        existing.References++;
                        return new SharedAdbScope(this, existing);
                    }

                    _sharedAdbSessions.Remove(key);
                    existing.Discarded = true;
                    if (existing.References <= 0)
                    {
                        stale = existing;
                    }
                }
            }

            if (stale is not null)
            {
                DisposeSharedAdbSession(stale);
            }

            var opened = OpenAdbLease(device)
                ?? throw new InvalidOperationException(
                    $"Unable to open an ADB session for {device.Serial} on the direct route; the device is not present on USB.");
            var session = opened.Session;
            try
            {
                session.Connect();
                if (!session.IsConnected)
                {
                    throw new InvalidOperationException("ADB session is not connected.");
                }
            }
            catch
            {
                session.Dispose();
                CloseLease(opened.Lease);
                throw;
            }

            var shared = new SharedAdbSession(opened.Lease, session) { References = 1 };
            lock (_sharedAdbLock)
            {
                _sharedAdbSessions[key] = shared;
            }

            return new SharedAdbScope(this, shared);
        }
    }

    /// <summary>
    /// Takes a failed session out of the pool immediately, so the next borrower opens a fresh transport while
    /// the current borrowers finish. The session is disposed once its last borrower returns.
    /// <para>把失败的会话立即移出池，使下一个借用方在现有借用方结束前就能重新打开传输；会话在最后一个借用方
    /// 归还后释放。</para>
    /// </summary>
    /// <param name="device">The device whose session failed. <para>会话失败所对应的设备。</para></param>
    /// <param name="session">The failed session; only the pooled instance matching it is discarded.
    /// <para>失败的会话；只有与之匹配的池内实例会被丢弃。</para></param>
    internal void DiscardSharedAdbSession(DetectedDevice device, IAdbDeviceSession session)
    {
        if (device is null || !UsesSharedAdbSession(device))
        {
            return;
        }

        SharedAdbSession? closing = null;
        lock (_sharedAdbLock)
        {
            var key = SharedAdbKey(device);
            if (_sharedAdbSessions.TryGetValue(key, out var shared) && ReferenceEquals(shared.Session, session))
            {
                _sharedAdbSessions.Remove(key);
                shared.Discarded = true;
                if (shared.References <= 0)
                {
                    closing = shared;
                }
            }
        }

        if (closing is not null)
        {
            DisposeSharedAdbSession(closing);
        }
    }

    /// <summary>
    /// Releases every idle shared session and marks the borrowed ones for teardown, so application shutdown
    /// hands the USB interfaces back instead of keeping them claimed until the process exits.
    /// <para>释放所有空闲共享会话并把仍被借用的标记为待释放，使应用退出时把 USB 接口交还操作系统，而不是
    /// 占用到进程结束。</para>
    /// </summary>
    private void DisposeSharedAdbSessions()
    {
        List<SharedAdbSession> closing = [];
        lock (_sharedAdbLock)
        {
            foreach (var shared in _sharedAdbSessions.Values)
            {
                shared.Discarded = true;
                if (shared.References <= 0)
                {
                    closing.Add(shared);
                }
            }

            _sharedAdbSessions.Clear();
        }

        foreach (var shared in closing)
        {
            DisposeSharedAdbSession(shared);
        }
    }

    private static string SharedAdbKey(DetectedDevice device)
        => string.IsNullOrWhiteSpace(device.Key) ? device.Serial ?? string.Empty : device.Key;

    private void ReleaseSharedAdbSession(SharedAdbSession shared)
    {
        var closing = false;
        lock (_sharedAdbLock)
        {
            if (shared.References > 0)
            {
                shared.References--;
            }

            closing = shared.References <= 0 && shared.Discarded;
        }

        if (closing)
        {
            DisposeSharedAdbSession(shared);
        }
    }

    private static void DisposeSharedAdbSession(SharedAdbSession shared)
    {
        try
        {
            shared.Session.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CommTrace.Debug("adb-session", $"Disposing the shared ADB session failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            shared.Lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CommTrace.Debug("adb-session", $"Releasing the shared ADB lease failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}