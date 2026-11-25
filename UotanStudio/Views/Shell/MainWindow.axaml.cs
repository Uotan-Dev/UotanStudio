using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

public partial class MainWindow : Window
{
    private readonly MainViewModel _ownedViewModel;
    private bool _closeAfterSave;
    private bool _closing;
    private Task _sessionInitialization = Task.CompletedTask;
    private ObservableCollection<DeviceInfo>? _selectorDevices;
    private readonly List<MenuItem> _selectorDeviceItems = [];
    private readonly MainWindowNativeMenu? _nativeMenu;

    public MainWindow() : this(new MainViewModel()) { }

    /// <summary>
    /// Creates a window that owns the explicitly supplied shell for its lifetime.
    /// <para>创建在其生命周期内持有显式提供的外壳的窗口。</para>
    /// </summary>
    /// <param name="viewModel">
    /// The shell model instance this window owns for its lifetime.
    /// <para>该窗口在其生命周期内持有的外壳模型实例。</para>
    /// </param>
    internal MainWindow(MainViewModel viewModel)
    {

        _ownedViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.AttachMainWindow(this);
        if (OperatingSystem.IsMacOS())
        {
            _nativeMenu = new MainWindowNativeMenu(this, TopMenu);
            TitleBar.SizeChanged += OnTitleBarSizeChanged;
            DeviceSelectorMenu.SizeChanged += OnTitleBarSizeChanged;
            TopMenu.SizeChanged += OnTitleBarSizeChanged;
        }
        DeviceSelector.DataContextChanged += (_, _) => BindDeviceSelector();
        BindDeviceSelector();

        // Linux 上装饰由 Avalonia 自绘，X11 后端会把标题栏上的按下直接转成窗口移动，事件到不了应用层，
        // 双击最大化因此失效；这里在自绘顶栏上补回该手势。Windows/macOS 由系统处理，无需干预。
        // On Linux the decorations are app-drawn and the X11 backend turns a title bar press into a window
        // move before managed code sees it, so double-click-to-maximise is lost; restore it on our own top bar.
        // Windows and macOS handle this in the system frame, so they are left alone.
        if (OperatingSystem.IsLinux())
        {
            // 顶栏空白处原本不参与命中测试，事件会穿透到下层。Linux 需要它自己接收双击，
            // 因此只在这里补上背景；Windows/macOS 必须保持穿透，否则系统拿不到 HTCAPTION 拖动。
            // The empty title bar area is normally not hit-testable and lets presses fall through. Linux
            // needs it to receive the double-click itself, so the background is added only here; Windows
            // and macOS must keep falling through or the system never sees the HTCAPTION drag.
            TitleBar.Background = Brushes.Transparent;
            Views.Behaviors.WindowTitleBarBehavior.Attach(this, TitleBar);
        }
        Closed += (_, _) =>
        {
            CloseChildWindows();
            _nativeMenu?.Dispose();
            TitleBar.SizeChanged -= OnTitleBarSizeChanged;
            DeviceSelectorMenu.SizeChanged -= OnTitleBarSizeChanged;
            TopMenu.SizeChanged -= OnTitleBarSizeChanged;
            if (_selectorDevices is not null)
                _selectorDevices.CollectionChanged -= OnSelectorDevicesChanged;
            foreach (var item in _selectorDeviceItems)
            {
                ((DeviceInfo)item.DataContext!).PropertyChanged -= OnSelectorDevicePropertyChanged;
            }
            _selectorDeviceItems.Clear();
            try { _ownedViewModel.Dispose(); }
            catch (Exception ex) { Services.LogService.Instance.Write(Services.LogLevel.Error, "Lifecycle", ex.Message); }
        };
        Closing += MainWindow_Closing;
        Opened += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel) _sessionInitialization = viewModel.InitializeAutoSaveAsync();
        };
    }

    /// <summary>
    /// Pre-builds all bottom tab pages on the current (UI) thread and installs them before the window is shown, so
    /// the first switch to flash, delivery, node editor or settings does not pay XAML parsing and construction cost.
    /// <para>在当前（UI）线程预构建全部底栏页签并在窗口显示前装入，使首次切换到烧录、交付、节点编辑或设置时
    /// 不再承担 XAML 解析与构造开销。</para>
    /// </summary>
    internal void InstallPrebuiltTopLevelPages()
        => MainContentView.InstallPrebuiltPages(ViewModelViewRegistry.CreateAllTopLevelPages());

    private void CloseChildWindows()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        foreach (var window in desktop.Windows.ToArray())
        {
            if (ReferenceEquals(window, this) || !window.IsVisible) continue;
            try
            {
                window.Close();

                if (window.IsVisible) window.Hide();
            }
            catch (Exception ex) { Services.LogService.Instance.Write(Services.LogLevel.Warn, "Lifecycle", ex.Message); }
        }
    }

    private void OnTitleBarSizeChanged(object? sender, SizeChangedEventArgs e)
    {

        var leftInset = TitleBarControls.Margin.Left + (TopMenu.IsVisible ? TopMenu.Bounds.Width : 0) + 12;
        var rightInset = DeviceSelectorMenu.Bounds.Width + DeviceSelectorMenu.Margin.Right + 12;
        MacWindowTitle.Margin = new Thickness(Math.Max(leftInset, rightInset), 0);
    }

    private void BindDeviceSelector()
    {
        if (_selectorDevices is not null)
            _selectorDevices.CollectionChanged -= OnSelectorDevicesChanged;
        _selectorDevices = (DeviceSelector.DataContext as MainViewModel)?.DeviceVM.Devices;
        if (_selectorDevices is not null)
            _selectorDevices.CollectionChanged += OnSelectorDevicesChanged;
        RefreshDeviceSelectorItems();
    }

    private void OnWindowEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key } item || item.IsEffectivelyEnabled)
        {
            return;
        }

        item.IsChecked = DataContext is MainViewModel shell && shell.MenuVM.WorkspacePanels.IsPanelVisible(key);
    }

    private void OnSelectorDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshDeviceSelectorItems();

    private void RefreshDeviceSelectorItems()
    {

        for (var i = _selectorDeviceItems.Count - 1; i >= 0; i--)
        {
            var item = _selectorDeviceItems[i];
            var device = (DeviceInfo)item.DataContext!;
            if (_selectorDevices?.Contains(device) == true) continue;
            device.PropertyChanged -= OnSelectorDevicePropertyChanged;
            DeviceSelector.Items.Remove(item);
            _selectorDeviceItems.RemoveAt(i);
        }

        if (_selectorDevices is null) return;
        var theme = (ControlTheme)Resources["DeviceSelectorItemTheme"]!;
        for (var i = 0; i < _selectorDevices.Count; i++)
        {
            var device = _selectorDevices[i];
            var item = _selectorDeviceItems.FirstOrDefault(row => ReferenceEquals(row.DataContext, device));
            if (item is null)
            {
                item = new MenuItem { DataContext = device, Theme = theme };
                device.PropertyChanged += OnSelectorDevicePropertyChanged;
                _selectorDeviceItems.Add(item);
            }

            item.Header = device.DisplayName;
            if (DeviceSelector.Items.IndexOf(item) == i + 1) continue;
            DeviceSelector.Items.Remove(item);
            DeviceSelector.Items.Insert(i + 1, item);
        }
    }

    /// <summary>
    /// Keeps one selector entry's header in step with the device entry, including slot enrichment that
    /// arrives after the item was created.
    /// <para>让某个选择器条目的标题跟随设备条目变化，包括条目创建之后才到达的槽位补充信息。</para>
    /// </summary>
    private void OnSelectorDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or nameof(DeviceInfo.DisplayName)) || sender is not DeviceInfo device)
        {
            return;
        }

        foreach (var item in _selectorDeviceItems)
        {
            if (!ReferenceEquals(item.DataContext, device))
            {
                continue;
            }

            item.Header = device.DisplayName;
            return;
        }
    }


    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_closeAfterSave && DataContext is MainViewModel viewModel)
        {
            e.Cancel = true;
            if (ViewModels.Workspace.ImageViewModel.Instance.IsOperating)
            {
                Services.LogService.Instance.Write(Services.LogLevel.Warn, "Images", "Wait for the image operation before closing. 请等待镜像操作完成后关闭。");
                return;
            }
            if (_closing) return;
            _closing = true;
            try
            {
                await _sessionInitialization;
                if (!await viewModel.FlushAutoSaveAsync())
                {
                    viewModel.ResumeAutoSave();
                    return;
                }
                _closeAfterSave = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(Close);
            }
            catch (Exception ex)
            {
                Services.LogService.Instance.Write(Services.LogLevel.Error, "AutoSave", ex.Message);
                viewModel.ResumeAutoSave();
            }
            finally
            {
                _closing = false;
            }
            return;
        }
    }
}