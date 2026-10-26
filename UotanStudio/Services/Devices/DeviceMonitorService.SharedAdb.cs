using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Diagnostics;

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
        private int _disposed;

        public DeviceLease Lease { get; } = lease;

        public IAdbDeviceSession Session { get; } = session;

        /// <summary>Borrowers still holding the session; the last one releases the lease and the transport.
        /// <para>仍持有会话的借用方数量；最后一个借用方释放租约与传输。</para></summary>
        public int References { get; set; }

        /// <summary>Whether the session left the pool because it failed; it is disposed once its borrowers return.
        /// <para>会话是否因失败离开池；其借用方全部归还后释放。</para></summary>
        public bool Discarded { get; set; }

        /// <summary>Claims the single teardown of this session and its lease.
        /// <para>认领本会话及其租约的唯一一次拆除。</para>
        /// </summary>
        /// <returns><see langword="true"/> for the first caller only. <para>仅首次调用方得到 <see langword="true"/>。</para></returns>
        public bool TryBeginTeardown() => Interlocked.Exchange(ref _disposed, 1) == 0;
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
    /// exclusive transport. Server-owned targets keep their per-call sessions; every direct target, USB or network,
    /// shares one, because a direct ADB endpoint is leased exclusively and one connection already multiplexes streams.
    /// <para>该设备的宿主 ADB 工作是否应复用共享直连会话，而不是新开独占传输。server 拥有的目标仍按每次调用开会话；
    /// 所有直连目标无论 USB 还是网络都共享一条会话，因为直连 ADB 端点按独占租用，而一条连接本身已复用多条流。</para>
    /// </summary>
    /// <param name="device">The device about to run ADB work. <para>即将执行 ADB 工作的设备。</para></param>
    /// <returns><see langword="true"/> when the direct session is shared.
    /// <para>直连会话应共享时为 <see langword="true"/>。</para></returns>
    internal static bool UsesSharedAdbSession(DetectedDevice device)
        => device.Protocol == DeviceProtocolKind.Adb
            && !device.Key.StartsWith("adb:", StringComparison.Ordinal)
            && !DebugBridgeSessionOpener.AdbServerOwns(device.Serial);

    /// <summary>
    /// Borrows the shared direct ADB session for the device, opening and connecting it once when no borrower
    /// holds it. A second borrower never opens a competing transport, so a long log capture no longer makes
    /// every other host operation fail with a busy endpoint. A pooled session that lost its link is torn down
    /// before anything else is opened, because its exclusive endpoint reservation would otherwise refuse the
    /// replacement and leave every later command failing as busy.
    /// <para>借用该设备的共享直连 ADB 会话；无人持有时只打开并连接一次。第二个借用方绝不会再开一份竞争的
    /// 传输，因此长时间的日志抓取不再让其它宿主操作以端点占用失败。丢失链路的池内会话会先被拆除，否则它的独占端点
    /// 占用会拒绝替代会话，使之后每条命令都以占用失败告终。</para>
    /// </summary>
    /// <param name="device">The direct ADB device, USB or network, whose session is borrowed.
    /// <para>要借用其会话的直连 ADB 设备，可为 USB 或网络目标。</para></param>
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
                    stale = existing;
                }
            }

            if (stale is not null)
            {
                DisposeSharedAdbSession(stale);
            }

            var opened = OpenAdbLease(device)
                ?? throw new InvalidOperationException(
                    $"Unable to open an ADB session for {device.Serial} on the direct route; the endpoint is not " +
                    "present or another owner holds it.");
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
    /// Takes a failed session out of the pool immediately, so the next borrower opens a fresh transport. A session
    /// that already lost its link is torn down at once even while borrowers still hold it: it cannot serve them any
    /// more, and keeping its exclusive endpoint reservation would refuse the replacement. A session that is still
    /// connected stays alive for its borrowers and is disposed once the last one returns.
    /// <para>把失败的会话立即移出池，使下一个借用方重新打开传输。已经丢失链路的会话即使仍被借用也立刻拆除：它已无法
    /// 服务借用方，继续占用独占端点会拒绝替代会话。仍然连接的会话为借用方保留，并在最后一个借用方归还后释放。</para>
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
                if (!shared.Session.IsConnected || shared.References <= 0)
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

    /// <summary>
    /// Takes the pooled session of a device key out of the pool and disposes it once its borrowers return, so a
    /// replaced network endpoint does not keep an idle connection to the device open under the old key.
    /// <para>把某个设备键的池内会话移出池，并在其借用方归还后释放，使被替换的网络端点不会以旧键继续占用一条到设备的
    /// 空闲连接。</para>
    /// </summary>
    /// <param name="deviceKey">The stale device key, for example <c>adb-tcp:host:port</c>.
    /// <para>失效的设备键，例如 <c>adb-tcp:host:port</c>。</para></param>
    internal void DropSharedAdbSession(string deviceKey)
    {
        SharedAdbSession? closing = null;
        lock (_sharedAdbLock)
        {
            if (_sharedAdbSessions.TryGetValue(deviceKey, out var shared))
            {
                _sharedAdbSessions.Remove(deviceKey);
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
        if (!shared.TryBeginTeardown())
        {
            return;
        }

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