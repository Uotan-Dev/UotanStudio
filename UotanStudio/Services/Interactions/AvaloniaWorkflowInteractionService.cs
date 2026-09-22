using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using UotanStudio.Projects.Interactions;
using UotanStudio.Views.Interactions;

namespace UotanStudio.Services.Interactions;

/// <summary>Presents a form on the UI thread and closes it before completing cancellation.</summary>
public sealed class AvaloniaWorkflowInteractionService : IWorkflowInteractionService, IWorkflowFeedbackService
{
    private readonly Func<Window?> _owner;
    private readonly ContentControl? _embeddedHost;
    private readonly bool _hasOwnerProvider;

    public AvaloniaWorkflowInteractionService(Func<Window?>? owner = null, ContentControl? embeddedHost = null)
    {
        _owner = owner ?? (() => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow);
        _embeddedHost = embeddedHost;
        _hasOwnerProvider = owner is not null;
    }

    public bool IsSupported => _embeddedHost is not null || _hasOwnerProvider || Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime;
    public bool SupportsFeedback => _hasOwnerProvider || Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime;

    public async Task NotifyAsync(string title, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var owner = _owner() ?? throw new NotSupportedException("No feedback window is available.");
            var progress = new AvaloniaInteractionProgress(owner, title, message, cancellationToken, showProgress: false);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) => { timer.Stop(); progress.Close(); };
            timer.Start();
        });
    }

    public async Task<IInteractionProgress> OpenProgressAsync(string title, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => (IInteractionProgress)new AvaloniaInteractionProgress(
            _owner() ?? throw new NotSupportedException("No feedback window is available."), title, message, cancellationToken));
    }

    public async Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => PresentAsync(request, cancellationToken));
    }

    private async Task<InteractionResult> PresentAsync(InteractionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<InteractionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = new InteractionFormView(new InteractionFormState(request.Form));
        Window? window = null;
        object? previous = null;
        if (_embeddedHost is null)
        {
            var owner = _owner() ?? throw new NotSupportedException("No interaction window is available.");
            window = new Window
            {
                Title = string.IsNullOrEmpty(request.Source) ? request.Form.Title : $"{request.Form.Title} — {request.Source}",
                Width = 560,
                Height = 560,
                MinWidth = 320,
                MinHeight = 240,
                Content = view,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            window.Closed += (_, _) => completion.TrySetResult(new InteractionResult { Outcome = InteractionOutcome.Cancelled });
            _ = window.ShowDialog(owner);
        }
        else
        {
            previous = _embeddedHost.Content;
            _embeddedHost.Content = view;
            _embeddedHost.IsVisible = true;
        }
        view.Completed += (_, result) => completion.TrySetResult(result);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => completion.TrySetCanceled(cancellationToken)));
        try
        {
            return await completion.Task;
        }
        finally
        {
            window?.Close();
            if (_embeddedHost is not null)
            {
                _embeddedHost.Content = previous;
                _embeddedHost.IsVisible = previous is not null;
            }
        }
    }
}