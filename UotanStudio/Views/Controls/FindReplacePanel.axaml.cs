using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UotanStudio.Views.Controls;

/// <summary>Local find/replace UI, without document operations.</summary>
public partial class FindReplacePanel : UserControl
{
    public event EventHandler? CloseRequested;

    public bool IsScopeDropDownOpen => ScopeBox.IsDropDownOpen;

    public FindReplacePanel()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            var compact = Bounds.Width < 300;
            Grid.SetColumn(ScopeBox, compact ? 0 : 1);
            Grid.SetRow(ScopeBox, compact ? 1 : 0);
            Grid.SetColumnSpan(ScopeBox, compact ? 2 : 1);
            ScopeBox.Margin = compact ? new Thickness(0, 3, 0, 0) : new Thickness(4, 0, 0, 0);
        };
    }

    public void FocusFind(bool showReplace)
    {
        ReplaceToggle.IsChecked = showReplace;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
