using Avalonia.Controls;
using Avalonia.Input;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>文件管理器编辑器视图：左侧文件树 + 右侧选中条目详情；双击目录展开/收起，双击文件保持选中。</para>
/// File-manager editor view: a left file tree plus a right entry-details pane; double-clicking toggles
/// a directory's expansion and keeps a file selected.
/// </summary>
public partial class FileManagerEditorView : UserControl
{
    /// <summary>
    /// <para>创建文件管理器编辑器视图并初始化组件。</para>
    /// Creates the file-manager editor view and initializes its components.
    /// </summary>
    public FileManagerEditorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// <para>树选中变更 → 同步视图模型选中条目。</para>
    /// Tree selection changes sync the view-model selected entry.
    /// </summary>
    private void OnFileTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is FileManagerEditorViewModel vm)
        {
            vm.SelectedEntry = FileTree.SelectedItem as FileManagerEntryViewModel;
        }
    }

    /// <summary>
    /// <para>双击目录展开/收起；双击文件保持选中（供详情区展示）。</para>
    /// Double-clicking a directory toggles its expansion; double-clicking a file keeps it selected for
    /// the details pane.
    /// </summary>
    private void OnFileTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control { DataContext: FileManagerEntryViewModel entry })
        {
            if (entry.IsDirectory)
            {
                entry.IsExpanded = !entry.IsExpanded;
            }
            else if (DataContext is FileManagerEditorViewModel vm)
            {
                vm.SelectedEntry = entry;
            }
        }
    }
}