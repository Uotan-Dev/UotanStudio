using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace UotanStudio.Comm.Transports.Tcp;

/// <summary>
/// Probes a bounded TCP port range with parallel connects and reports the ports that accepted one. The scan is
/// bounded by the caller's port range, per-probe deadline, concurrency and cancellation token; when an accept
/// callback approves a port the scan stops early, so a caller that can recognize its service does not pay for the
/// rest of the range.
/// <para>以并行连接探测有界 TCP 端口范围，并报告接受了连接的端口。扫描受调用方给出的端口范围、单次探测期限、
/// 并发数与取消令牌约束；接受回调认可某个端口时扫描提前结束，使能识别自身服务的调用方不必等待剩余范围。</para>
/// </summary>
internal static class TcpPortScanner
{
    /// <summary>
    /// Degrees of parallelism used when the caller does not pick one.
    /// <para>调用方未指定时使用的并发度。</para>
    /// </summary>
    internal const int DefaultConcurrency = 256;

    /// <summary>
    /// Scans <paramref name="firstPort"/> through <paramref name="lastPort"/> inclusive and returns the ports that
    /// completed a TCP connection, plus the port the accept callback approved first.
    /// <para>扫描 <paramref name="firstPort"/> 到 <paramref name="lastPort"/>（含两端），返回完成 TCP 连接的所有端口，
    /// 以及接受回调最先认可的端口。</para>
    /// </summary>
    /// <param name="host">Target host name or IP literal. <para>目标主机名或 IP 字面量。</para></param>
    /// <param name="firstPort">First port of the range, 1 through 65535.
    /// <para>范围的首个端口，1 到 65535。</para></param>
    /// <param name="lastPort">Last port of the range; must not precede <paramref name="firstPort"/>.
    /// <para>范围的末个端口；不得小于 <paramref name="firstPort"/>。</para></param>
    /// <param name="concurrency">Maximum probes in flight; larger values finish sooner but add network load.
    /// <para>同时在途的探测上限；值越大完成越快，但网络负载更高。</para></param>
    /// <param name="probeTimeout">Deadline of one probe; a port that neither accepts nor refuses by then counts as
    /// closed. <para>单次探测期限；届时既未接受也未拒绝的端口按关闭处理。</para></param>
    /// <param name="accept">Optional approval of an open port; a <see langword="true"/> result ends the scan.
    /// <para>可选的开端口认可回调；返回 <see langword="true"/> 时结束扫描。</para></param>
    /// <param name="cancellationToken">Cancels the whole scan; cancellation propagates.
    /// <para>取消整个扫描；取消会被传播。</para></param>
    /// <returns>The open ports in ascending order and the approved port, or zero when nothing was approved.
    /// <para>按升序排列的开端口，以及被认可的端口；没有认可时为 0。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="host"/> is blank.
    /// <para><paramref name="host"/> 为空。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">A port, the concurrency or the probe deadline is out of range,
    /// or <paramref name="lastPort"/> precedes <paramref name="firstPort"/>.
    /// <para>端口、并发数或探测期限超出范围，或 <paramref name="lastPort"/> 小于 <paramref name="firstPort"/>。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    internal static async Task<TcpPortScanResult> ScanAsync(string host, int firstPort, int lastPort,
        int concurrency, TimeSpan probeTimeout, Func<int, CancellationToken, Task<bool>>? accept = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (firstPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(firstPort));
        }

        if (lastPort < firstPort || lastPort > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(lastPort));
        }

        if (concurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(concurrency));
        }

        if (probeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(probeTimeout));
        }

        var open = new ConcurrentBag<int>();
        var accepted = new int[1];
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = stop.Token,
        };
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(firstPort, lastPort - firstPort + 1), options,
                async (port, token) =>
                {
                    if (!await IsListeningAsync(host, port, probeTimeout, token).ConfigureAwait(false))
                    {
                        return;
                    }

                    open.Add(port);
                    if (accept is null || !await accept(port, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    Interlocked.CompareExchange(ref accepted[0], port, 0);
                    await stop.CancelAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        var ports = open.ToArray();
        Array.Sort(ports);
        return new TcpPortScanResult(ports, Volatile.Read(ref accepted[0]));
    }

    private static async Task<bool> IsListeningAsync(string host, int port, TimeSpan probeTimeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(probeTimeout);
        using var socket = CreateSocket(host);
        try
        {
            await socket.ConnectAsync(CreateTarget(host, port), deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private static Socket CreateSocket(string host) =>
        IPAddress.TryParse(host, out var address)
            ? new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            : new Socket(SocketType.Stream, ProtocolType.Tcp);

    private static EndPoint CreateTarget(string host, int port) =>
        IPAddress.TryParse(host, out var address)
            ? new IPEndPoint(address, port)
            : new DnsEndPoint(host, port);
}

/// <summary>
/// Outcome of one bounded port scan: every port that accepted a TCP connection and the port an accept callback
/// approved, if any.
/// <para>一次有界端口扫描的结果：接受过 TCP 连接的全部端口，以及接受回调认可的端口（如有）。</para>
/// </summary>
/// <param name="OpenPorts">Ports that accepted a connection, ascending.
/// <para>接受过连接的端口，升序排列。</para></param>
/// <param name="AcceptedPort">The approved port, or zero when the scan ended without approval.
/// <para>被认可的端口；扫描未经认可结束时为 0。</para></param>
internal readonly record struct TcpPortScanResult(IReadOnlyList<int> OpenPorts, int AcceptedPort);