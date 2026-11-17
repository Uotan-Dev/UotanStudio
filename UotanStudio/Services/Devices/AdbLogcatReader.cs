using System.Globalization;

namespace UotanStudio.Services.Devices;

internal sealed class AdbLogcatReader(Func<string, Action<string>, CancellationToken, Task> executeShell)
{
    internal const string LogcatCommand = "logcat -b all -v threadtime -v year '*:V'";
    internal const string LegacyLogcatCommand = "logcat -b all -v threadtime '*:V'";
    internal const string DefaultBucketLogcatCommand = "logcat -v threadtime '*:V'";

    /// <summary>
    /// The logcat invocations tried in order: the newest format first, then without the year format, and
    /// finally without an explicit bucket list. Android 4.4 exposes only main/system/radio/events ring
    /// buffers, so <c>-b all</c> fails there ("Unable to open log device '/dev/log/all'") while the
    /// default bucket selection the platform applies still streams logs.
    /// <para>依次尝试的 logcat 调用：先进格式，其次去掉 year 格式，最后去掉显式缓冲区列表。
    /// Android 4.4 只有 main/system/radio/events 环形缓冲区，因此 <c>-b all</c> 在那里失败
    /// （“Unable to open log device '/dev/log/all'”），而平台默认的缓冲区选择仍然能输出日志。</para>
    /// </summary>
    internal static readonly IReadOnlyList<string> CommandLadder =
        [LogcatCommand, LegacyLogcatCommand, DefaultBucketLogcatCommand];

    private const long ProcessNameRefreshIntervalMilliseconds = 10_000;

    private IReadOnlyDictionary<int, string> _processNames = new Dictionary<int, string>();
    private long _lastProcessRefreshMilliseconds;
    private int _processRefreshGate;
    private Task? _processRefreshTask;

    internal async Task RunAsync(Action<AdbLogcatEntry> onEntry, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = lifetime.Token;
        await RefreshProcessNamesAsync(token).ConfigureAwait(false);
        Interlocked.Exchange(ref _lastProcessRefreshMilliseconds, Environment.TickCount64);
        try
        {
            for (var index = 0; index < CommandLadder.Count; index++)
            {
                var command = CommandLadder[index];
                var attempt = new StreamAttempt(this);
                await executeShell(command, line => attempt.Accept(line, onEntry, token), token)
                    .ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!attempt.Rejected)
                {
                    return;
                }

                if (index + 1 < CommandLadder.Count)
                {
                    LogService.Instance.Write(LogLevel.Info, "AdbLog",
                        $"The device refused '{command}'; retrying logcat with '{CommandLadder[index + 1]}'.");
                }
            }
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await AwaitProcessRefreshAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tracks one logcat invocation. A device that refuses it prints an error and its usage banner and
    /// then exits, so those lines are swallowed and the caller advances along <see cref="CommandLadder"/>;
    /// once ordinary output arrives the attempt is no longer treated as a refusal.
    /// <para>跟踪一次 logcat 调用。设备拒绝时会打印错误和用法横幅后退出，这些行被吞掉，调用方继续
    /// 沿 <see cref="CommandLadder"/> 前进；一旦收到普通输出，本次尝试便不再视为拒绝。</para>
    /// </summary>
    private sealed class StreamAttempt(AdbLogcatReader owner)
    {
        private bool _streaming;

        public bool Rejected { get; private set; }

        public void Accept(string line, Action<AdbLogcatEntry> onEntry, CancellationToken token)
        {
            if (Rejected)
            {
                return;
            }

            if (!_streaming)
            {
                if (LooksLikeRejection(line))
                {
                    Rejected = true;
                    return;
                }

                if (LooksLikeUsageBanner(line))
                {
                    return;
                }

                _streaming = true;
            }

            owner.HandleLine(line, onEntry, token);
        }
    }

    internal static bool LooksLikeFormatRejection(string line) =>
        line.Contains("Invalid parameter to -v", StringComparison.OrdinalIgnoreCase)
        || line.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
            && line.Contains("-v", StringComparison.Ordinal);

    /// <summary>
    /// Recognises every way a device refuses a logcat invocation: an unsupported <c>-v</c> format, a
    /// rejected option, or a ring buffer this Android release does not expose.
    /// <para>识别设备拒绝 logcat 调用的所有情形：不支持的 <c>-v</c> 格式、被拒绝的选项，
    /// 或该 Android 版本未提供的环形缓冲区。</para>
    /// </summary>
    /// <param name="line">One line of device output. <para>设备输出的一行。</para></param>
    /// <returns>True when the device refused the invocation. <para>设备拒绝该调用时为 true。</para></returns>
    internal static bool LooksLikeRejection(string line) =>
        LooksLikeFormatRejection(line)
        || line.Contains("Unable to open log device", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Unknown option", StringComparison.OrdinalIgnoreCase)
        || line.Contains("invalid option", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Detects the usage banner a device prints after refusing the invocation, so a rejection never
    /// reaches the log view as entries.
    /// <para>识别设备拒绝调用后打印的用法横幅，使拒绝信息不会作为条目进入日志视图。</para>
    /// </summary>
    /// <param name="line">One line of device output. <para>设备输出的一行。</para></param>
    /// <returns>True for a usage-banner line. <para>用法横幅行为 true。</para></returns>
    private static bool LooksLikeUsageBanner(string line)
    {
        var text = line.AsSpan().Trim();
        return text.IsEmpty
            || text.StartsWith("Usage:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("options include:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("filterspecs are", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("where <tag>", StringComparison.OrdinalIgnoreCase)
            || (text.Length > 0 && text[0] == '-' && (text.Length < 2 || !char.IsAsciiDigit(text[1])));
    }

    /// <summary>
    /// Publishes one streamed line, refreshing the process-name table when an unknown pid appears.
    /// <para>发布一条流式输出行；出现未知 pid 时刷新进程名表。</para>
    /// </summary>
    /// <param name="line">One line of device output. <para>设备输出的一行。</para></param>
    /// <param name="onEntry">Sink for the parsed entry. <para>解析后条目的接收方。</para></param>
    /// <param name="cancellationToken">Cancels the refresh. <para>取消刷新。</para></param>
    private void HandleLine(string line, Action<AdbLogcatEntry> onEntry, CancellationToken cancellationToken)
    {
        var names = Volatile.Read(ref _processNames);
        var entry = AdbLogcatEntry.Parse(line, names);
        if (entry.ProcessId > 0 && !names.ContainsKey(entry.ProcessId))
        {
            RequestProcessNameRefresh(cancellationToken);
        }

        onEntry(entry);
    }

    private void RequestProcessNameRefresh(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _processRefreshGate, 1, 0) != 0)
        {
            return;
        }

        var refresh = Task.Run(() => RefreshProcessNamesWhenStaleAsync(cancellationToken));
        Volatile.Write(ref _processRefreshTask, refresh);
    }

    private async Task RefreshProcessNamesWhenStaleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastProcessRefreshMilliseconds) < ProcessNameRefreshIntervalMilliseconds)
            {
                return;
            }

            Interlocked.Exchange(ref _lastProcessRefreshMilliseconds, now);
            await RefreshProcessNamesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogService.Instance.Write(LogLevel.Debug, "AdbLog",
                $"Refreshing the logcat process-name table failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _processRefreshGate, 0);
        }
    }

    private async Task AwaitProcessRefreshAsync()
    {
        var refresh = Volatile.Read(ref _processRefreshTask);
        if (refresh is null)
        {
            return;
        }

        try
        {
            await refresh.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RefreshProcessNamesAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        Dictionary<int, string> names = [];
        try
        {
            await executeShell("ps -A -o PID,NAME", line =>
            {
                var text = line.AsSpan().Trim();
                var separator = text.IndexOfAny(' ', '\t');
                if (separator < 0 || names.Count >= 16384
                    || !int.TryParse(text[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) return;
                var name = text[(separator + 1)..].Trim();
                if (pid > 0 && !name.IsEmpty && name.Length <= 1024) names[pid] = name.ToString();
            }, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            LogService.Instance.Write(LogLevel.Debug, "AdbLog",
                $"Reading the logcat process-name table failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        Volatile.Write(ref _processNames, names);
    }
}