using Avalonia.Threading;

namespace UotanStudio.ViewModels;

/// <summary>
/// Restartable UI debounce on a DispatcherTimer: the timer is created on first use and each Restart
/// restarts the countdown, so only the latest change fires the callback.
/// <para>基于 DispatcherTimer 的可重启 UI 防抖：第一次使用时创建定时器，之后每次 Restart 重新计时。</para>
/// </summary>
internal sealed class UiDebounce(Action callback, int intervalMs = 250)
{
    private DispatcherTimer? _timer;

    /// <summary>
    /// Restarts the countdown; the callback fires once when it elapses.
    /// <para>重新开始计时；到时后触发一次回调。</para>
    /// </summary>
    public void Restart()
    {
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMs) };
            _timer.Tick += OnTick;
        }

        _timer.Stop();
        _timer.Start();
    }

    /// <summary>
    /// Stops the countdown; a queued callback does not fire.
    /// <para>停止计时，已排队的回调不会触发。</para>
    /// </summary>
    public void Stop() => _timer?.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        callback();
    }
}