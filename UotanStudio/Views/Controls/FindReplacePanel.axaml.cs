using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels;

namespace UotanStudio.Views.Controls;

/// <summary>Find/replace controls connected to workspace search.<para>接入工作区搜索的查找替换控件。</para></summary>
public partial class FindReplacePanel : UserControl
{
    private readonly LocalizedOptionSet _scopeOptions = new(
        "FindReplace_CurrentDocument", "FindReplace_Selection",
        "FindReplace_OpenDocuments", "FindReplace_Project");

    public event EventHandler? CloseRequested;
    internal event EventHandler? OptionsChanged;
    internal event Action<SearchAction>? ActionRequested;

    internal TextSearchOptions Options => new(FindBox.Text ?? "", ReplaceBox.Text ?? "",
        MatchCaseToggle.IsChecked == true, WholeWordToggle.IsChecked == true,
        RegexToggle.IsChecked == true, PreserveCaseToggle.IsChecked == true);
    internal int Scope => ScopeBox.SelectedIndex;

    internal void SetStatus(string message, bool ready)
    {
        ResultStatus.Text = message;
        PreviousButton.IsEnabled = NextButton.IsEnabled = ready;
        ReplaceButton.IsEnabled = ReplaceAllButton.IsEnabled = ready;
    }

    internal void SetQuery(string text) => FindBox.Text = text;

    public bool IsScopeDropDownOpen => ScopeBox.IsDropDownOpen;

    public FindReplacePanel()
    {
        InitializeComponent();
        ScopeBox.ItemsSource = _scopeOptions.Options;
        ScopeBox.SelectedIndex = 0;
        FindBox.TextChanged += (_, _) => OptionsChanged?.Invoke(this, EventArgs.Empty);
        ReplaceBox.TextChanged += (_, _) => OptionsChanged?.Invoke(this, EventArgs.Empty);
        ScopeBox.SelectionChanged += (_, _) => OptionsChanged?.Invoke(this, EventArgs.Empty);
        foreach (var toggle in new[] { MatchCaseToggle, WholeWordToggle, RegexToggle, PreserveCaseToggle })
            toggle.PropertyChanged += (_, e) =>
            {
                if (e.Property == ToggleButton.IsCheckedProperty) OptionsChanged?.Invoke(this, EventArgs.Empty);
            };
        FindBox.KeyDown += OnInputKeyDown;
        ReplaceBox.KeyDown += OnInputKeyDown;
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
    private void OnPreviousClick(object? sender, RoutedEventArgs e) => ActionRequested?.Invoke(SearchAction.Previous);
    private void OnNextClick(object? sender, RoutedEventArgs e) => ActionRequested?.Invoke(SearchAction.Next);
    private void OnReplaceClick(object? sender, RoutedEventArgs e) => ActionRequested?.Invoke(SearchAction.Replace);
    private void OnReplaceAllClick(object? sender, RoutedEventArgs e) => ActionRequested?.Invoke(SearchAction.ReplaceAll);

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var action = ReferenceEquals(sender, ReplaceBox) ? SearchAction.Replace
            : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? SearchAction.Previous : SearchAction.Next;
        ActionRequested?.Invoke(action);
        e.Handled = true;
    }
}

internal enum SearchAction { Previous, Next, Replace, ReplaceAll }