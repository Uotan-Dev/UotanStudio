namespace UotanStudio.Messages;

/// <summary>
/// 一键刷写向导的刷写确认请求消息，由向导视图弹出是/否确认对话框，并把结果写回
/// <see cref="Confirmation"/>（TaskCompletionSource）。若向导窗口已关闭，则视图模型
/// 会在释放时主动置为 false，避免悬挂等待。
/// <para>Flash-wizard confirmation request message: the wizard view shows a yes/no
/// confirmation dialog and writes the result back through <see cref="Confirmation"/>
/// (a TaskCompletionSource). If the wizard window was closed meanwhile, the view model
/// sets the result to false on dispose so the waiting call never hangs.</para>
/// </summary>
public class FlashWizardConfirmRequestMessage(string title, string message, TaskCompletionSource<bool> confirmation)
{
    /// <summary>确认对话框标题。 Dialog title.</summary>
    public string Title { get; } = title;

    /// <summary>确认对话框正文。 Dialog message body.</summary>
    public string Message { get; } = message;

    /// <summary>确认结果回写器（true=继续，false=取消）。 Confirmation result (true=proceed, false=cancel).</summary>
    public TaskCompletionSource<bool> Confirmation { get; } = confirmation;
}