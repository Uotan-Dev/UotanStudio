using UotanStudio.ViewModels.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using UotanStudio.ViewModels.Devices;
#if DEBUG
using Dock.Avalonia.Diagnostics;
#endif

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
#if DEBUG
    private IDisposable? _dockDebugOverlay;
#endif

    public MainWindow() : this(new MainViewModel()) { }

    /// <summary>Creates a window that owns the explicitly supplied shell for its lifetime.</summary>
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
        Closed += (_, _) =>
        {
            CloseChildWindows();
            _nativeMenu?.Dispose();
            TitleBar.SizeChanged -= OnTitleBarSizeChanged;
            DeviceSelectorMenu.SizeChanged -= OnTitleBarSizeChanged;
            TopMenu.SizeChanged -= OnTitleBarSizeChanged;
            if (_selectorDevices is not null)
                _selectorDevices.CollectionChanged -= OnSelectorDevicesChanged;
            try { _ownedViewModel.Dispose(); }
            catch (Exception ex) { Services.LogService.Instance.Write(Services.LogLevel.Error, "Lifecycle", ex.Message); }
#if DEBUG
            _dockDebugOverlay?.Dispose();
            _dockDebugOverlay = null;
#endif
        };
        Closing += MainWindow_Closing;
        Opened += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel) _sessionInitialization = viewModel.InitializeAutoSaveAsync();
        };
#if DEBUG
        Opened += MainWindow_Opened;
#endif
    }

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

    private void OnSelectorDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshDeviceSelectorItems();

    private void RefreshDeviceSelectorItems()
    {

        for (var i = _selectorDeviceItems.Count - 1; i >= 0; i--)
        {
            var item = _selectorDeviceItems[i];
            if (_selectorDevices?.Contains((DeviceInfo)item.DataContext!) == true) continue;
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
                _selectorDeviceItems.Add(item);
            }

            if (DeviceSelector.Items.IndexOf(item) == i + 1) continue;
            DeviceSelector.Items.Remove(item);
            DeviceSelector.Items.Insert(i + 1, item);
        }
    }

#if DEBUG
    private void MainWindow_Opened(object? sender, EventArgs e)
    {
        _dockDebugOverlay?.Dispose();
        _dockDebugOverlay = this.AttachDockDebugOverlay();
    }
#endif

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