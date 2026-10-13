using Avalonia.Controls;
using Avalonia.Threading;

namespace UotanStudio.Services.Interactions;

/// <summary>An invocation-owned progress surface; cancellation disposes it even if close is skipped.
/// <para>由单次调用持有的进度反馈接口；即使跳过关闭，取消时也会将其释放。</para></summary>
public interface IInteractionProgress : IAsyncDisposable
{
    Task UpdateAsync(string message, double? progress, CancellationToken cancellationToken);
}

/// <summary>Provides nonmodal feedback capabilities.
/// <para>提供非模态反馈的能力接口。</para></summary>
public interface IWorkflowFeedbackService
{
    bool SupportsFeedback => true;
    Task NotifyAsync(string title, string message, CancellationToken cancellationToken);
    Task<IInteractionProgress> OpenProgressAsync(string title, string message, CancellationToken cancellationToken);
}

internal sealed class AvaloniaInteractionProgress : IInteractionProgress
{
    private readonly Window _window;
    private readonly TextBlock _message;
    private readonly ProgressBar _progress;
    private readonly CancellationTokenRegistration _registration;
    private bool _closed;

    internal AvaloniaInteractionProgress(Window owner, string title, string message, CancellationToken cancellationToken, bool showProgress = true)
    {
        _message = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        _progress = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 1, IsVisible = showProgress };
        var panel = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(16) };
        panel.Children.Add(_message);
        panel.Children.Add(_progress);
        _window = new Window
        {
            Title = title,
            Width = 400,
            Height = 180,
            MinWidth = 240,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        _window.Closed += (_, _) => { _closed = true; _registration.Dispose(); };
        _window.Show(owner);
        _registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(Close));
    }

    public async Task UpdateAsync(string message, double? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (progress is { } value && (!double.IsFinite(value) || value is < 0 or > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(progress));
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_closed)
            {
                _message.Text = message;
                _progress.IsIndeterminate = progress is null;
                _progress.Value = progress ?? 0;
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(Close);
        _registration.Dispose();
    }

    internal void Close()
    {
        if (!_closed)
        {
            _closed = true;
            _window.Close();
        }
    }

    public override string ToString() => "Progress";
}