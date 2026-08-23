using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.ViewModels;

namespace UotanStudio.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        // 默认选中 Workspace Tab（索引 1：Device=0, Workspace=1）
        MainTabs.SelectedIndex = 1;
        WeakReferenceMessenger.Default.Register<NavigateTabMessage>(this, OnNavigateTab);
        WeakReferenceMessenger.Default.Register<ClearCanvasMessage>(this, OnClearCanvas);
    }

    private void OnNavigateTab(object recipient, NavigateTabMessage message)
    {
        // Device=0, Workspace=1, Node=2, Log=3, Settings=4
        if (message.TabIndex is >= 0 and < 5)
        {
            MainTabs.SelectedIndex = message.TabIndex;
        }
    }

    private void OnClearCanvas(object recipient, ClearCanvasMessage message)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.NodeEditorVM.ClearAllCommand.Execute(null);
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        WeakReferenceMessenger.Default.Unregister<NavigateTabMessage>(this);
        WeakReferenceMessenger.Default.Unregister<ClearCanvasMessage>(this);
    }
}