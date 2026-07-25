using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.ViewModels;

namespace UotanStudio.Pages.DockView;

public partial class OutputView : UserControl
{
    public OutputView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// <para>清空按钮点击事件，清空当前输出集合。</para>
    /// Clear button click handler, clearing the current output collection.
    /// </summary>
    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OutputViewModel vm)
        {
            vm.Clear();
        }
    }
}