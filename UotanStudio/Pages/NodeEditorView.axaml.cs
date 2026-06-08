using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.ViewModels;

namespace UotanStudio.Pages;

public partial class NodeEditorView : UserControl
{
    public NodeEditorView()
    {
        InitializeComponent();
    }

    private void OnToolboxItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is NodeToolboxItem item)
        {
            if (DataContext is NodeEditorViewModel vm)
            {
                var node = item.CreateNode();
                vm.AddNode(node);
            }
        }
    }
}
