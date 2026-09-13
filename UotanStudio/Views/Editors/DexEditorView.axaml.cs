using Avalonia.Controls;
using Avalonia.Platform.Storage;
using UotanStudio.Services;

namespace UotanStudio.Views.Editors;

/// <summary>
/// <para>DEX 编辑器视图：承载反汇编文本与容器工具栏；资源导出/替换经系统文件选择器完成后调用视图模型。</para>
/// The DEX editor view: hosts the disassembly text and container toolbar; resource export/replace
/// goes through system file pickers and then calls the view model.
/// </summary>
public partial class DexEditorView : UserControl
{
    /// <summary>
    /// <para>创建 DEX 编辑器视图并初始化组件。</para>
    /// Creates the DEX editor view and initializes its components.
    /// </summary>
    public DexEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is not DexEditorView view) return;
        if (view._viewModel is not null) view._viewModel.ResourceOperationRequested -= OnResourceOperationRequested;
        view._viewModel = view.DataContext as UotanStudio.ViewModels.Editors.DexEditorViewModel;
        if (view._viewModel is not null) view._viewModel.ResourceOperationRequested += OnResourceOperationRequested;
    }

    private UotanStudio.ViewModels.Editors.DexEditorViewModel? _viewModel;

    private async void OnResourceOperationRequested(object? sender, bool replace)
    {
        if (sender is not UotanStudio.ViewModels.Editors.DexEditorViewModel vm || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        try
        {
            if (replace)
            {
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = LanguageService.Instance.GetString("Dock_Dex_ResourceReplace"),
                    AllowMultiple = false,
                });
                if (files.Count > 0 && files[0].TryGetLocalPath() is { } sourcePath)
                {
                    await vm.ReplaceResourceAsync(sourcePath);
                }
            }
            else
            {
                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = LanguageService.Instance.GetString("Dock_Dex_ResourceExport"),
                    SuggestedFileName = System.IO.Path.GetFileName(vm.SelectedResource?.Name),
                });
                if (file?.TryGetLocalPath() is { } outputPath)
                {
                    await vm.ExportResourceAsync(outputPath);
                }
            }
        }
        catch (Exception ex)
        {
            vm.CompileError = ex.Message;
        }
    }
}
