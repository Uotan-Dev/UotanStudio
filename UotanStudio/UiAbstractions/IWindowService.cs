namespace UotanStudio.UiAbstractions;

/// <summary>
/// Identifies one reusable host tool window owned by the shell.
/// <para>标识 Shell 持有的一种可复用宿主工具窗口。</para>
/// </summary>
public enum HostWindowKind
{
    /// <summary>The device manager window. <para>设备管理器窗口。</para></summary>
    Device,

    /// <summary>The global log window. <para>全局日志窗口。</para></summary>
    Log,

    /// <summary>The USB tree window. <para>USB 树窗口。</para></summary>
    UsbTree,

    /// <summary>The key library window. <para>密钥库窗口。</para></summary>
    KeyManager,

    /// <summary>The visual plugin manager window. <para>可视化插件管理窗口。</para></summary>
    VisualPluginManager,

    /// <summary>The APK signing window. <para>APK 签名窗口。</para></summary>
    ApkSigning,

    /// <summary>The boot image patch window. <para>Boot 镜像修补窗口。</para></summary>
    BootPatch,

    /// <summary>The AVB footer and A/B OTA tool window. <para>AVB 尾部与 A/B OTA 工具窗口。</para></summary>
    AvbTool
}

/// <summary>
/// New-project form surface consumed by view models: creation callback plus progress reporting, with no
/// dependency on the concrete window type.
/// <para>视图模型消费的新建项目表单接口：提供创建回调与进度上报，不依赖具体窗口类型。</para>
/// </summary>
public interface INewProjectDialog
{
    /// <summary>
    /// Runs the caller's creation flow while the modal form stays open, receiving the submitted form and
    /// the cancellation token triggered by the cancel button.
    /// <para>模态表单保持打开时运行调用方的创建流程，接收提交的表单与取消按钮触发的取消令牌。</para>
    /// </summary>
    Func<NewProjectFormResult, CancellationToken, Task>? CreateWithCancellationAsync { get; set; }

    /// <summary>
    /// Begins one byte-counted progress stage; samples from earlier stages are discarded.
    /// <para>开始一个按字节计量的进度阶段；较早阶段的样本会被丢弃。</para>
    /// </summary>
    /// <param name="status">The localized status text shown for the stage. <para>该阶段显示的本地化状态文本。</para></param>
    /// <returns>The reporter receiving processed/total byte samples. <para>接收已处理/总字节样本的上报器。</para></returns>
    IProgress<(long Processed, long Total)> BeginByteProgress(string status);

    /// <summary>
    /// Clears the accumulated progress samples and statistics text.
    /// <para>清空累计的进度样本与统计文本。</para>
    /// </summary>
    void ResetProgressStats();

    /// <summary>
    /// Updates the progress panel; a byte sample overrides the percentage when its total is positive.
    /// <para>更新进度面板；字节样本总量为正时覆盖百分比。</para>
    /// </summary>
    /// <param name="status">The localized status text. <para>本地化状态文本。</para></param>
    /// <param name="percent">The explicit percentage, or null for indeterminate progress. <para>显式百分比；为 null 时显示不确定进度。</para></param>
    /// <param name="bytes">The optional byte sample used for speed and remaining-time statistics. <para>可选的字节样本，用于速度与剩余时间统计。</para></param>
    /// <param name="statistics">The statistics line override, or null to keep the computed text. <para>统计行覆盖文本；为 null 时保留计算文本。</para></param>
    void ReportProgress(string status, double? percent = null, (long Processed, long Total)? bytes = null,
        string? statistics = null);

    /// <summary>
    /// Shows the form modally against the current owner window; no-op when no owner is available.
    /// <para>相对当前所有者窗口以模态方式显示表单；没有所有者窗口时不做任何操作。</para>
    /// </summary>
    /// <returns>A task that completes when the modal form closes. <para>模态表单关闭后完成的任务。</para></returns>
    Task ShowDialogAsync();
}

/// <summary>
/// Host window operations used by view models so they never construct view types directly.
/// <para>视图模型使用的宿主窗口操作，使其不再直接构造视图类型。</para>
/// </summary>
public interface IWindowService
{
    /// <summary>
    /// Whether an owner window is currently available for modal dialogs.
    /// <para>当前是否存在可用于模态对话框的所有者窗口。</para>
    /// </summary>
    bool HasOwnerWindow { get; }

    /// <summary>
    /// Creates (but does not show) a new-project form.
    /// <para>创建（但不显示）新建项目表单。</para>
    /// </summary>
    /// <param name="mode">The form mode (new solution or sub-project). <para>表单模式（新建解决方案或子项目）。</para></param>
    /// <param name="baseDirectory">The default base directory, or null for the application directory. <para>默认基础目录；为 null 时使用应用目录。</para></param>
    /// <param name="reservedNames">Names treated as taken when probing the default name. <para>默认名探测时视为占用的名称。</para></param>
    /// <returns>The form surface the caller configures and shows. <para>供调用方配置并显示的表单接口。</para></returns>
    INewProjectDialog CreateNewProjectDialog(NewProjectWindowMode mode, string? baseDirectory,
        IEnumerable<string>? reservedNames);

    /// <summary>
    /// Opens or focuses the scheme editor window, optionally loading a scheme file.
    /// <para>打开或聚焦方案编辑器窗口，可选载入方案文件。</para>
    /// </summary>
    /// <param name="path">The scheme file to load, or null to keep the current document. <para>要载入的方案文件；为 null 时保留当前文档。</para></param>
    /// <returns>A task that completes after the window is shown and the file is loaded. <para>窗口显示并完成文件载入后完成的任务。</para></returns>
    Task ShowSchemeEditorAsync(string? path);

    /// <summary>
    /// Opens or focuses the scheme operator window, optionally loading a scheme file.
    /// <para>打开或聚焦方案执行器窗口，可选载入方案文件。</para>
    /// </summary>
    /// <param name="path">The scheme file to load, or null to keep the current document. <para>要载入的方案文件；为 null 时保留当前文档。</para></param>
    /// <returns>A task that completes after the window is shown and the file is loaded. <para>窗口显示并完成文件载入后完成的任务。</para></returns>
    Task ShowSchemeOperatorAsync(string? path);

    /// <summary>
    /// Shows the wireless-debugging QR pairing window modally against the current owner.
    /// <para>相对当前所有者以模态方式显示无线调试扫码配对窗口。</para>
    /// </summary>
    /// <returns>A task that completes when the pairing window closes. <para>配对窗口关闭后完成的任务。</para></returns>
    Task ShowQrPairingAsync();

    /// <summary>
    /// Shows or focuses one reusable host tool window.
    /// <para>显示或聚焦一个可复用宿主工具窗口。</para>
    /// </summary>
    /// <param name="kind">The tool window to show. <para>要显示的工具窗口。</para></param>
    void ShowHostWindow(HostWindowKind kind);

    /// <summary>
    /// Shows or focuses the checksum window, optionally switching its target file.
    /// <para>显示或聚焦校验器窗口，可选切换目标文件。</para>
    /// </summary>
    /// <param name="filePath">The file to select, or null to keep the current state. <para>要选中的文件；为 null 时保留当前状态。</para></param>
    void ShowChecksumWindow(string? filePath = null);

    /// <summary>
    /// Shows the About dialog against the main window.
    /// <para>相对主窗口显示关于对话框。</para>
    /// </summary>
    void ShowAboutDialog();
}

/// <summary>
/// No-op window service used when a view model is constructed without a host shell.
/// <para>未通过宿主 Shell 构造视图模型时使用的空窗口服务。</para>
/// </summary>
internal sealed class NullWindowService : IWindowService
{
    /// <summary>
    /// The shared no-op instance.
    /// <para>共享的空实现实例。</para>
    /// </summary>
    internal static NullWindowService Instance { get; } = new();

    public bool HasOwnerWindow => false;

    public INewProjectDialog CreateNewProjectDialog(NewProjectWindowMode mode, string? baseDirectory,
        IEnumerable<string>? reservedNames) => throw new NotSupportedException("No host window service is attached.");

    public Task ShowSchemeEditorAsync(string? path) => Task.CompletedTask;

    public Task ShowSchemeOperatorAsync(string? path) => Task.CompletedTask;

    public Task ShowQrPairingAsync() => Task.CompletedTask;

    public void ShowHostWindow(HostWindowKind kind)
    {
    }

    public void ShowChecksumWindow(string? filePath = null)
    {
    }

    public void ShowAboutDialog()
    {
    }
}