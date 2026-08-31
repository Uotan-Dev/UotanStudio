using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;

namespace UotanStudio.Views.Workspace;

/// <summary>
/// <para>工作区视图：承载 Dock 布局，并在根 UserControl 接线系统文件拖拽——
/// 拖入文件逐个经工作区打开链路（Auto 模式按 <see cref="FileOpenMode.ResolveMode"/> 自动路由
/// 文本/十六进制/固件/分区等）；拖入目录则枚举其中已知固件扩展名文件导入工程镜像清单；
/// 混合拖拽只取文件部分。与 OS 拖拽的兼容性参照对比页的既有实现（双保险判定）。</para>
/// Workspace view: hosts the dock layout and wires OS file drag-and-drop on the root
/// UserControl — dropped files are opened one by one through the workspace open chain (Auto mode
/// routes via <see cref="FileOpenMode.ResolveMode"/> to text/hex/firmware/partition editors);
/// dropped folders are scanned for known firmware extensions and imported into the project image
/// list; mixed drags take only the file part. OS drag compatibility follows the compare page's
/// existing implementation (double-insurance detection).
/// </summary>
public partial class WorkspaceView : UserControl
{
    /// <summary>
    /// <para>创建工作区视图并初始化组件。</para>
    /// Creates the workspace view and initializes its components.
    /// </summary>
    public WorkspaceView()
    {
        InitializeComponent();
    }

    // ===== 系统文件拖拽 =====

    /// <summary>
    /// <para>拖拽悬停事件：按是否携带文件/文件夹路径决定光标与允许的拖放效果
    /// （无文件负载时禁止光标）。文件判定使用双保险，与 Drop 消费保持一致。</para>
    /// Drag-over event: sets the cursor and allowed effects according to whether the payload
    /// carries file/folder paths (forbids the cursor when it does not). The file check is the
    /// same double-insurance probe Drop consumes.
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasFilePayload(e.DataTransfer) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// <para>判定拖放数据是否携带文件/文件夹路径：先检查通用文件格式（应用内/进程内拖拽），
    /// 再以 <c>TryGetFiles</c> 直接探测（与 Drop 消费一致；Avalonia 12 的 Win32 后端把
    /// CF_HDROP 映射为 <c>Universal</c> 的 <c>DataFormat.File</c>，此处可命中），最后扫描
    /// 格式列表中的系统文件格式名（Windows 为 <c>FileDrop</c>）。</para>
    /// Determines whether the drag payload carries file/folder paths: checks the universal file
    /// format first (in-process drags), then probes <c>TryGetFiles</c> directly (matching what
    /// Drop consumes; Avalonia 12's Win32 backend maps CF_HDROP to a <c>Universal</c>
    /// <c>DataFormat.File</c>, which this catches), and finally scans the format list for the OS
    /// file-format name (<c>FileDrop</c> on Windows).
    /// </summary>
    private static bool HasFilePayload(IDataTransfer transfer)
    {
        if (transfer.Contains(DataFormat.File))
        {
            return true;
        }

        try
        {
            if (transfer.TryGetFiles() is { Length: > 0 })
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "WorkspaceView", ex.Message);
        }

        foreach (var format in transfer.Formats)
        {
            if (string.Equals(format.Identifier, "FileDrop", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <para>拖放事件：立即消费并解析拖入的文件/文件夹路径，再异步分发——
    /// 文件逐个打开（Auto 自动路由），目录枚举固件镜像导入工程，混合只取文件部分。
    /// 所有异常捕获并经日志服务记录，不阻断 UI 线程。</para>
    /// Drop event: consumes the payload immediately, resolves the dropped file/folder paths and
    /// dispatches asynchronously — files open one by one (Auto routing), folders are scanned for
    /// firmware images to import, mixed drags keep only the file part. All exceptions are caught
    /// and logged without blocking the UI thread.
    /// </summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        try
        {
            var paths = TryExtractPaths(e.DataTransfer);
            if (paths.Count == 0)
            {
                LogService.Instance.Write(LogLevel.Warn, "WorkspaceView",
                    LanguageService.Instance.GetString("Workspace_DropNoFiles"));
                return;
            }

            var files = paths.Where(File.Exists).ToArray();
            var directories = paths.Where(Directory.Exists).ToArray();

            // 混合拖拽只取文件部分：目录与文件同时出现时仅打开文件，不触发目录导入。
            // Mixed drags take only the file part: when files and folders arrive together, only
            // the files are opened and the folder import is skipped.
            if (files.Length > 0 && directories.Length > 0)
            {
                await OpenDocumentsAsync(files);
                return;
            }

            // 纯目录：枚举目录内已知固件扩展名文件（.img/.pac/.ops/.nb0 等）后导入工程镜像清单。
            // Folder-only drop: enumerate the known firmware extensions inside each folder and
            // import them into the project's image list.
            if (directories.Length > 0)
            {
                var images = directories.SelectMany(EnumerateFirmwareImages).ToArray();
                if (images.Length > 0 && ResolveMenuViewModel(this) is { } menu)
                {
                    await menu.ImportFilesAsync(images);
                }
            }

            // 纯文件：逐文件走工作区打开链路（Auto 模式按扩展名+内容自动路由）。
            // File-only drop: open each file through the workspace open chain (Auto mode routes
            // by extension plus content).
            if (files.Length > 0)
            {
                await OpenDocumentsAsync(files);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "WorkspaceView",
                $"{LanguageService.Instance.GetString("Workspace_DropOpenFailed")}: {ex.Message}");
        }
    }

    /// <summary>
    /// <para>逐个打开文件：视图模型就绪时直接调用 <see cref="WorkspaceViewModel.OpenDocumentAsync"/>
    /// （Auto 模式），否则退回 <see cref="OpenDocumentRequestMessage"/> 消息链路。</para>
    /// Opens the files one by one: calls <see cref="WorkspaceViewModel.OpenDocumentAsync"/> in
    /// Auto mode when the view model is available, otherwise falls back to the
    /// <see cref="OpenDocumentRequestMessage"/> message chain.
    /// </summary>
    private async Task OpenDocumentsAsync(IReadOnlyList<string> files)
    {
        if (DataContext is WorkspaceViewModel vm)
        {
            foreach (var file in files)
            {
                await vm.OpenDocumentAsync(file);
            }

            return;
        }

        // 视图模型未就绪时退回消息链路（App 侧转发到同一打开逻辑）。
        // Fall back to the message chain when the view model is not available (the app forwards
        // it to the same open logic).
        foreach (var file in files)
        {
            WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(file));
        }
    }

    /// <summary>
    /// <para>枚举目录（仅直接子级）内扩展名命中已知固件集合的文件。</para>
    /// Enumerates the files inside a folder (direct children only) whose extension matches the
    /// known firmware set.
    /// </summary>
    private static IEnumerable<string> EnumerateFirmwareImages(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (FileOpenMode.IsFirmwareExtension(Path.GetExtension(file)))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// <para>沿可视树向上解析当前 <see cref="MainViewModel"/>（主窗口 DataContext），
    /// 取菜单视图模型以复用 <see cref="MenuViewModel.ImportFilesAsync"/> 的目录导入链路。</para>
    /// Walks the visual tree upward to resolve the current <see cref="MainViewModel"/> (the main
    /// window's DataContext) and returns its menu view model, reusing
    /// <see cref="MenuViewModel.ImportFilesAsync"/> for the folder import chain.
    /// </summary>
    private static MenuViewModel? ResolveMenuViewModel(Visual? source)
    {
        for (var current = source; current is not null; current = current.GetVisualParent())
        {
            if (current.DataContext is MainViewModel mainVm)
            {
                return mainVm.MenuVM;
            }
        }

        return null;
    }

    /// <summary>
    /// <para>从拖放数据提取文件/文件夹路径：先按通用文件格式取条目，失败时再按平台文件
    /// 格式（<c>FileDrop</c>）取条目数据（宽容枚举 <see cref="IStorageItem"/>）。</para>
    /// Extracts file/folder paths from the drag payload: first via the universal file format, then
    /// via the platform file format (<c>FileDrop</c>) reading the item data as an enumerable of
    /// <see cref="IStorageItem"/>.
    /// </summary>
    private static IReadOnlyList<string> TryExtractPaths(IDataTransfer transfer)
    {
        var items = transfer.TryGetFiles();
        if (items is { Length: > 0 })
        {
            return items.Select(static item => item.Path.LocalPath).ToList();
        }

        foreach (var format in transfer.Formats)
        {
            if (!string.Equals(format.Identifier, "FileDrop", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var item in transfer.Items)
            {
                if (!item.Contains(format))
                {
                    continue;
                }

                try
                {
                    if (item.TryGetRaw(format) is System.Collections.IEnumerable enumerable)
                    {
                        var storage = enumerable.OfType<IStorageItem>().ToArray();
                        if (storage.Length > 0)
                        {
                            return storage.Select(static s => s.Path.LocalPath).ToList();
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Write(LogLevel.Error, "WorkspaceView", ex.Message);
                }
            }
        }

        return [];
    }
}
