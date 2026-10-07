using System.Globalization;

namespace UotanStudio.Services.Devices;

internal sealed class AdbLogcatReader(Func<string, Action<string>, CancellationToken, Task> executeShell)
{
    internal const string LogcatCommand = "logcat -b all -v threadtime -v year '*:V'";
    private IReadOnlyDictionary<int, string> _processNames = new Dictionary<int, string>();

    internal async Task RunAsync(Action<AdbLogcatEntry> onEntry, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await RefreshProcessNamesAsync(lifetime.Token).ConfigureAwait(false);
        var tracking = TrackProcessesAsync(lifetime.Token);
        try
        {
            await executeShell(LogcatCommand,
                line => onEntry(AdbLogcatEntry.Parse(line, Volatile.Read(ref _processNames))),
                lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try { await tracking.ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private async Task TrackProcessesAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await RefreshProcessNamesAsync(cancellationToken).ConfigureAwait(false);
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
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            names.Clear();
        }
        Volatile.Write(ref _processNames, names);
    }
}