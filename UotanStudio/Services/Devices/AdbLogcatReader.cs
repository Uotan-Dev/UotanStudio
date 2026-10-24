using System.Globalization;

namespace UotanStudio.Services.Devices;

internal sealed class AdbLogcatReader(Func<string, Action<string>, CancellationToken, Task> executeShell)
{
    internal const string LogcatCommand = "logcat -b all -v threadtime -v year '*:V'";
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
            await executeShell(LogcatCommand, line => HandleLine(line, onEntry, token), token).ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await AwaitProcessRefreshAsync().ConfigureAwait(false);
        }
    }

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
            // The process-name table is cosmetic enrichment; a failed lookup keeps the stream running and only
            // means that entries are shown without a process name.
            LogService.Instance.Write(LogLevel.Debug, "AdbLog",
                $"Reading the logcat process-name table failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        Volatile.Write(ref _processNames, names);
    }
}