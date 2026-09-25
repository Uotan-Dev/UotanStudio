using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.Services;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using UotanStudio.Views.Shell;

namespace UotanStudio.Views.Nodes;

public partial class NodeEditorView
{
    private async void OnEncapsulateClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NodeEditorViewModel vm || vm.IsExecuting || vm.SelectedNodes.Count == 0
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        try
        {
            var preview = new CompositeInterfaceWindow(vm.AnalyzeSelection());
            if (await preview.ShowDialog<bool>(owner))
            {
                vm.EncapsulateSelection(preview.CompositeName, preview.GetBoundary());
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            vm.ExecutionStatus = LanguageService.Instance.GetString("Node_OperationFailed", ex.Message);
        }
    }

    private void OnExpandCompositeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NodeEditorViewModel vm)
        {
            try
            {
                vm.ExpandSelectedComposite();
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
            {
                vm.ExecutionStatus = LanguageService.Instance.GetString("Node_OperationFailed", ex.Message);
            }
        }
    }

    private void OnCollapseCompositeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NodeEditorViewModel vm)
        {
            vm.CollapseExpandedComposite();
        }
    }

    private void OnEditCompositeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NodeEditorViewModel { SelectedNode: CompositeNodeViewModel composite, IsExecuting: false } vm)
        {
            new VisualPluginEditorWindow(composite.Package, package => vm.ReplaceComposite(composite, package)).Show();
        }
    }
}