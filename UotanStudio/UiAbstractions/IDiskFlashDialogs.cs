using UotanStudio.DD.Writing;

namespace UotanStudio.UiAbstractions;

/// <summary>
/// Message windows the disk flasher needs; the host implements them so the view model never builds a window.
/// <para>磁盘烧录需要的消息窗口；由宿主实现，使视图模型不直接构造窗口。</para>
/// </summary>
public interface IDiskFlashDialogs
{
    /// <summary>
    /// Shows a blocking error message for a failed flash operation.
    /// <para>为失败的烧录操作显示阻塞式错误消息。</para>
    /// </summary>
    /// <param name="title">The window title.<para>窗口标题。</para></param>
    /// <param name="message">The failure detail.<para>失败详情。</para></param>
    /// <returns>A task that completes when the user dismissed the window.
    /// <para>用户关闭窗口后完成的任务。</para></returns>
    Task ShowErrorAsync(string title, string message);

    /// <summary>
    /// Shows a blocking message explaining why this host cannot write raw disks.
    /// <para>显示阻塞式消息，说明该宿主为何无法写入裸磁盘。</para>
    /// </summary>
    /// <param name="title">The window title.<para>窗口标题。</para></param>
    /// <param name="message">The explanation.<para>说明文本。</para></param>
    /// <returns>A task that completes when the user dismissed the window.
    /// <para>用户关闭窗口后完成的任务。</para></returns>
    Task ShowWarningAsync(string title, string message);
}

/// <summary>
/// Fallback dialogs used when no host shell is attached; every call completes immediately.
/// <para>未附加宿主 Shell 时使用的回退对话框；所有调用立即完成。</para>
/// </summary>
internal sealed class NullDiskFlashDialogs : IDiskFlashDialogs
{
    /// <summary>The shared no-op instance.<para>共享的空实现实例。</para></summary>
    internal static NullDiskFlashDialogs Instance { get; } = new();

    public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

    public Task ShowWarningAsync(string title, string message) => Task.CompletedTask;
}