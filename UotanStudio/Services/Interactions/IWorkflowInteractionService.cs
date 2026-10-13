using UotanStudio.Projects.Interactions;

namespace UotanStudio.Services.Interactions;

/// <summary>Boundary for user interaction requests. Implementations must close requests on cancellation.
/// <para>用户交互请求的边界接口；实现必须在取消时关闭请求。</para></summary>
public interface IWorkflowInteractionService
{
    bool IsSupported { get; }
    Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken);
}

/// <summary>Noninteractive runs fail explicitly rather than inventing user consent.
/// <para>非交互运行会明确失败，而不会伪造用户同意。</para></summary>
public sealed class UnsupportedWorkflowInteractionService : IWorkflowInteractionService
{
    public static UnsupportedWorkflowInteractionService Instance { get; } = new();
    public bool IsSupported => false;
    public Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This execution host does not support user interaction.");
}

/// <summary>Serializes modal requests and distinguishes workflow cancellation from an interaction timeout.
/// <para>串行化模态请求，并区分工作流取消与交互超时。</para></summary>
/// <param name="presenter">The wrapped interaction service that presents each request.
/// <para>负责呈现每个请求的被包装交互服务。</para></param>
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