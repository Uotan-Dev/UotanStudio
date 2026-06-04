using Avalonia.Controls;

namespace UotanStudio.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        MainTabs.SelectedIndex = 3;
    }

    private void OnSelectionChanged(object? sender, PageSelectionChangedEventArgs e)
    {

    }
}