using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using UotanStudio.Fonts;
using UotanStudio.Services;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;

namespace UotanStudio.Views.Workspace;

public partial class TerminalView : UserControl
{
    private TerminalViewModel? _model;
    private TerminalSessionViewModel? _session;
    private DockTerminal? _terminal;

    public TerminalView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindModel();
        Loaded += (_, _) =>
        {
            if (!Design.IsDesignMode) _model?.Initialize();
            FocusTerminal();
        };
        AddHandler(KeyDownEvent, OnTerminalKeyDown, RoutingStrategies.Tunnel);
    }

    private void BindModel()
    {

        if (DataContext is not TerminalViewModel model || ReferenceEquals(_model, model)) return;
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }
        ClearTerminal();
        _model = model;
        _model.PropertyChanged += OnModelChanged;
        BindSession();
        if (IsLoaded && !Design.IsDesignMode) _model.Initialize();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalViewModel.Session)) BindSession();
    }

    private void ClearTerminal()
    {
        if (_session is not null) _session.PropertyChanged -= OnSessionChanged;
        SessionHost.Children.Clear();
        _terminal?.Dispose();
        _terminal = null;
        _session = null;
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalSessionViewModel.IsRunning)) UpdateButtons();
    }

    private void BindSession()
    {
        ClearTerminal();
        _session = _model?.Session;
        if (_session is not { } session)
        {
            UpdateButtons();
            return;
        }
        session.PropertyChanged += OnSessionChanged;
        var terminal = _terminal = new DockTerminal
        {
            Process = string.Empty,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"),
            FontSize = 13,
            BufferSize = 10000,
            Options = new XTerm.Options.TerminalOptions { MinimumContrastRatio = 4.5 },
            Margin = new Thickness(8, 4, 0, 4),
            AllowWindowOps = false
        };
        terminal.Bind(Iciclecreek.Terminal.TerminalView.BackgroundProperty, new DynamicResourceExtension("PageBackground"));
        terminal.Bind(Iciclecreek.Terminal.TerminalView.ForegroundProperty, new DynamicResourceExtension("TextForeground"));
        terminal.Bind(Iciclecreek.Terminal.TerminalView.CursorColorProperty, new DynamicResourceExtension("TextForeground"));
        terminal.PropertyChanged += (_, e) =>
        {
            if (e.Property == Iciclecreek.Terminal.TerminalView.CurrentDirectoryProperty && terminal.CurrentDirectory is { Length: > 0 } cwd)
                session.WorkingDirectory = cwd;
        };
        terminal.ProcessExited += (_, e) =>
        {
            session.ReportExit(e.ExitCode);
            UpdateButtons();
        };
        var scrollBar = new ScrollBar { Orientation = Avalonia.Layout.Orientation.Vertical, Width = 12 };
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12") };
        Grid.SetColumn(scrollBar, 1);
        root.Children.Add(terminal);
        root.Children.Add(scrollBar);
        var scrolling = false;
        void UpdateScrollBar()
        {
            scrollBar.Maximum = terminal.IsAlternateBuffer ? 0 : terminal.MaxScrollback;
            scrollBar.ViewportSize = terminal.ViewportLines;
            scrollBar.IsEnabled = scrollBar.Maximum > 0;
            if (!scrolling) scrollBar.Value = terminal.ViewportY;
        }
        terminal.PropertyChanged += (_, e) =>
        {
            if (e.Property == Iciclecreek.Terminal.TerminalView.ViewportYProperty || e.Property == Iciclecreek.Terminal.TerminalView.MaxScrollbackProperty
                || e.Property == Iciclecreek.Terminal.TerminalView.ViewportLinesProperty || e.Property == Iciclecreek.Terminal.TerminalView.IsAlternateBufferProperty)
                UpdateScrollBar();
        };
        scrollBar.Scroll += (_, e) =>
        {
            scrolling = true;
            try { terminal.ViewportY = (int)Math.Round(e.NewValue); }
            finally { scrolling = false; }
        };
        var menu = new ContextMenu();
        var copy = new MenuItem();
        copy.Bind(HeaderedSelectingItemsControl.HeaderProperty, new DynamicResourceExtension("Menu_Copy"));
        copy.Click += async (_, _) => await terminal.CopyAsync();
        var paste = new MenuItem();
        paste.Bind(HeaderedSelectingItemsControl.HeaderProperty, new DynamicResourceExtension("Menu_Paste"));
        paste.Click += async (_, _) => await terminal.PasteAsync();
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        terminal.ContextMenu = menu;
        // The control's own right-click gesture copies the selection and then clears it, so the menu
        // that opens a moment later has nothing highlighted and Copy acts on an empty selection. The
        // gesture is claimed here first: the selection is copied through the same public path so the
        // clipboard matches what is highlighted, the highlight survives, and the menu is opened by us.
        // 控件内置的右键手势会先复制选区再清空它，菜单随后打开时已无高亮、复制也无内容可复制。这里先接管该手势：
        // 用同一公开路径复制以保持剪贴板与高亮一致，保留高亮，并由我们打开菜单。
        terminal.AddHandler(InputElement.PointerPressedEvent, (_, e) => OnTerminalPointerPressed(terminal, menu, e),
            RoutingStrategies.Tunnel);
        terminal.Loaded += StartOnLoaded;
        SessionHost.Children.Add(root);
        UpdateButtons();
        async void StartOnLoaded(object? sender, RoutedEventArgs e)
        {
            terminal.Loaded -= StartOnLoaded;
            await StartSessionAsync(session, terminal);
        }
    }

    /// <summary>Claims the right-button press so the built-in gesture cannot clear the selection before
    /// the context menu appears. <para>接管右键按下，使内置手势无法在上下文菜单出现前清空选区。</para></summary>
    /// <param name="terminal">The terminal that was pressed.<para>被按下的终端。</para></param>
    /// <param name="menu">The menu to open.<para>要打开的菜单。</para></param>
    /// <param name="e">The pointer press being routed.<para>正在路由的指针按下事件。</para></param>
    private static void OnTerminalPointerPressed(DockTerminal terminal, ContextMenu menu, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(terminal).Properties.IsRightButtonPressed) return;
        // Without a selection the built-in gesture only pastes, which is left untouched.
        // 没有选区时内置手势只做粘贴，保持原样。
        if (!terminal.Terminal.Selection.HasSelection) return;
        e.Handled = true;
        _ = OpenTerminalMenuAsync(terminal, menu);
    }

    /// <summary>Copies the highlighted text and opens the context menu while the highlight stays visible,
    /// so the menu acts on what the user can still see. <para>复制高亮文本并打开上下文菜单，同时保留高亮，
    /// 使菜单作用的正是用户仍能看到的选区。</para></summary>
    /// <param name="terminal">The terminal whose selection is copied.<para>选区被复制的终端。</para></param>
    /// <param name="menu">The menu to open.<para>要打开的菜单。</para></param>
    private static async Task OpenTerminalMenuAsync(DockTerminal terminal, ContextMenu menu)
    {
        await terminal.CopyAsync();
        menu.Open(terminal);
    }

    private async Task StartSessionAsync(TerminalSessionViewModel session, DockTerminal terminal)
    {
        try
        {
            var columns = terminal.CharWidth > 0 ? (int)(terminal.Bounds.Width / terminal.CharWidth) : 80;
            var rows = terminal.CharHeight > 0 ? (int)(terminal.Bounds.Height / terminal.CharHeight) : 24;
            await session.StartAsync(columns, rows);
            if (_terminal != terminal || _session != session || session.Connection is not { } connection) return;
            terminal.AttachConnection(connection);

            connection.Resize(terminal.Terminal.Cols, terminal.Terminal.Rows);
            terminal.Refresh();
            UpdateButtons();
            FocusTerminal();
        }
        catch (Exception ex)
        {
            if (_session != session) return;
            if (_model is { } model) model.ErrorMessage = LanguageService.Instance.GetString("Terminal_StartFailed", ex.Message);
            LogService.Instance.Write(LogLevel.Error, "Terminal", ex.Message);
        }
    }

    private void UpdateButtons()
    {
        var hasSession = _terminal is not null;
        CopyTerminalButton.IsEnabled = hasSession;
        PasteTerminalButton.IsEnabled = _session?.IsRunning == true;
        ClearTerminalButton.IsEnabled = hasSession;
        TerminalBottomButton.IsEnabled = hasSession;
    }

    private void FocusTerminal()
    {
        if (IsLoaded && _terminal is { } terminal)
            Dispatcher.UIThread.Post(() => { if (terminal.IsEffectivelyVisible) terminal.Focus(); }, DispatcherPriority.Input);
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_terminal is { } terminal) await terminal.CopyAsync();
    }
    private async void OnPasteClick(object? sender, RoutedEventArgs e)
    {
        if (_terminal is { } terminal) { await terminal.PasteAsync(); terminal.Focus(); }
    }
    private void OnClearClick(object? sender, RoutedEventArgs e) => _terminal?.ClearScreen();
    private void OnBottomClick(object? sender, RoutedEventArgs e) => _terminal?.FollowTail();
    private async void OnTerminalKeyDown(object? sender, KeyEventArgs e)
    {
        if (_terminal is not { IsKeyboardFocusWithin: true } terminal) return;
        var clipboardModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control | KeyModifiers.Shift;
        if (e.KeyModifiers != clipboardModifiers || e.Key is not (Key.C or Key.V)) return;
        e.Handled = true;
        if (e.Key == Key.C) await terminal.CopyAsync();
        else await terminal.PasteAsync();
    }
}