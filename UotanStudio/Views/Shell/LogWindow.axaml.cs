using UotanStudio.ViewModels.Shell;
using Avalonia.Controls;
using UotanStudio.ViewModels;

namespace UotanStudio.Views.Shell;

/// <summary>
/// <para>全局日志独立窗口，展示统一日志服务推送的日志条目。</para>
/// Standalone global log window, displaying log entries pushed by the unified log service.
/// </summary>
public partial class LogWindow : Window
{
    /// <summary>
    /// <para>日志窗口共享的日志视图模型，保持单实例订阅，避免多窗口重复订阅日志服务。</para>
    /// Shared log view model for the log window, kept as a single instance to avoid duplicate subscriptions to the log service.
    /// </summary>
    private static readonly LogViewModel SharedViewModel = new();

    /// <summary>
    /// <para>初始化日志窗口，并将数据上下文指向共享的 <see cref="LogViewModel"/>。</para>
    /// Initializes the log window, binding the data context to the shared <see cref="LogViewModel"/>.
    /// </summary>
    public LogWindow()
    {
        InitializeComponent();
        DataContext = SharedViewModel;
    }
}