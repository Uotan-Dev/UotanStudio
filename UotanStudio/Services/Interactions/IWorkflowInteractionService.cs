using UotanStudio.Projects.Interactions;

namespace UotanStudio.Services.Interactions;

/// <summary>Host boundary for user interaction. Implementations must close requests on cancellation.</summary>
public interface IWorkflowInteractionService
{
    bool IsSupported { get; }
    Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken);
}

/// <summary>Noninteractive runs fail explicitly rather than inventing user consent.</summary>
public sealed class UnsupportedWorkflowInteractionService : IWorkflowInteractionService
{
    public static UnsupportedWorkflowInteractionService Instance { get; } = new();
    public bool IsSupported => false;
    public Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This execution host does not support user interaction.");
}

/// <summary>Serializes modal requests and distinguishes workflow cancellation from an interaction timeout.</summary>
public sealed class QueuedWorkflowInteractionService(IWorkflowInteractionService presenter) : IWorkflowInteractionService, IWorkflowFeedbackService
{
    private readonly SemaphoreSlim _queue = new(1, 1);
    public bool IsSupported => presenter.IsSupported;
    public bool SupportsFeedback => presenter is IWorkflowFeedbackService { SupportsFeedback: true };

    public Task NotifyAsync(string title, string message, CancellationToken cancellationToken)
        => Feedback.NotifyAsync(title, message, cancellationToken);

    public Task<IInteractionProgress> OpenProgressAsync(string title, string message, CancellationToken cancellationToken)
        => Feedback.OpenProgressAsync(title, message, cancellationToken);

    private IWorkflowFeedbackService Feedback => presenter as IWorkflowFeedbackService
        ?? throw new NotSupportedException("This interaction host does not support nonmodal feedback.");

    public async Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken)
    {
        _ = new InteractionFormState(request.Form);
        if (request.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Invalid interaction timeout.");
        }
        await _queue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (request.Timeout is { } duration)
            {
                linked.CancelAfter(duration);
            }
            try
            {
                var result = await presenter.ShowAsync(request, linked.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                linked.Token.ThrowIfCancellationRequested();
                if (result.Outcome != InteractionOutcome.Submitted)
                {
                    return new InteractionResult { Outcome = result.Outcome };
                }
                var submitted = new InteractionFormState(request.Form);
                foreach (var (id, value) in result.Values)
                {
                    submitted.SetValue(id, value);
                }
                return submitted.Submit();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && linked.IsCancellationRequested)
            {
                return new InteractionResult { Outcome = InteractionOutcome.TimedOut };
            }
        }
        finally
        {
            _queue.Release();
        }
    }
}