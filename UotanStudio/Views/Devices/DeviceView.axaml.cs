using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using UotanStudio.Services;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Views.Devices;

/// <summary>
/// Device page view, providing ADB/Fastboot device list and connection management interface.
/// <para>设备页面视图，提供 ADB/Fastboot 设备列表与连接管理界面。</para>
/// </summary>
public partial class DeviceView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceView"/> class.
    /// <para>初始化 <see cref="DeviceView"/> 类的新实例。</para>
    /// </summary>
    public DeviceView()
    {
        InitializeComponent();
        SizeChanged += OnViewSizeChanged;
        LayoutUpdated += OnToolbarLayoutUpdated;
    }

    private bool _isCompact;

    private void OnToolbarLayoutUpdated(object? sender, EventArgs e)
    {
        const double minimumRemoteWidth = 312;
        const double preferredRemoteWidth = 400;
        const double spacing = 16;
        var available = DeviceToolbarScrollViewer.Bounds.Width;
        if (available <= 0)
        {
            return;
        }
        var actionsWidth = DeviceActionsPanel.Children
            .Where(control => control.IsVisible)
            .Sum(control => control.DesiredSize.Width);
        var toolbarWidth = Math.Max(available, actionsWidth + spacing + minimumRemoteWidth);
        var remoteWidth = Math.Min(preferredRemoteWidth, toolbarWidth - actionsWidth - spacing);
        if (double.IsNaN(DeviceToolbarGrid.Width) || Math.Abs(DeviceToolbarGrid.Width - toolbarWidth) > 0.5)
        {
            DeviceToolbarGrid.Width = toolbarWidth;
        }
        if (Math.Abs(RemoteConnectionPanel.Width - remoteWidth) > 0.5)
        {
            RemoteConnectionPanel.Width = remoteWidth;
        }
    }

    private void OnViewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 720;
        if (_isCompact == compact)
        {
            return;
        }
        _isCompact = compact;
        DeviceContentGrid.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "300,*");
        DeviceContentGrid.RowDefinitions = new RowDefinitions(compact ? "Auto,*" : "*");
        DeviceContentGrid.ColumnSpacing = compact ? 0 : 20;
        DeviceContentGrid.RowSpacing = compact ? 12 : 0;
        DeviceListPanel.MaxHeight = compact ? 240 : double.PositiveInfinity;
        Grid.SetColumn(DeviceDetailPanel, compact ? 0 : 1);
        Grid.SetRow(DeviceDetailPanel, compact ? 1 : 0);
    }

    private void OnHdcOperationMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuDevice(sender) is not null && DataContext is DeviceViewModel vm
            && sender is MenuItem { CommandParameter: string operation }
            && vm.HdcOperationCommand.CanExecute(operation))
            vm.HdcOperationCommand.Execute(operation);
    }

    /// <summary>
    /// Gets the device behind a context-menu item and selects it in the view model.
    /// <para>取出右键菜单项对应的设备并在视图模型中选中它。</para>
    /// </summary>
    private DeviceInfo? SelectMenuDevice(object? sender)
    {
        if (sender is not MenuItem { DataContext: DeviceInfo device })
        {
            return null;
        }

        if (DataContext is DeviceViewModel vm)
        {
            vm.SelectedDevice = device;
        }

        return device;
    }

    /// <summary><para>连接右键菜单设备。</para>Connect the device from the context menu.</summary>
    private void OnConnectMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteForMenuDevice(sender, static vm => vm.ConnectCommand, null);

    /// <summary><para>断开右键菜单设备。</para>Disconnect the device from the context menu.</summary>
    private void OnDisconnectMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteForMenuDevice(sender, static vm => vm.DisconnectCommand, null);

    /// <summary><para>将右键菜单设备重启到指定模式。</para>Reboot the context-menu device to the given mode.</summary>
    private void OnRebootMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectMenuDevice(sender) is not null && DataContext is DeviceViewModel vm
            && sender is MenuItem { CommandParameter: string mode })
        {
            if (vm.RebootCommand.CanExecute(mode)) vm.RebootCommand.Execute(mode);
        }
    }

    /// <summary><para>为右键菜单设备打开 ADB Shell。</para>Open an ADB shell for the context-menu device.</summary>
    private void OnOpenAdbShellMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteForMenuDevice(sender, static vm => vm.OpenAdbShellCommand, null);

    /// <summary><para>读取右键菜单 Fastboot 设备信息。</para>Read Fastboot info for the context-menu device.</summary>
    private void OnFastbootReadInfoMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteForMenuDevice(sender, static vm => vm.FastbootReadInfoCommand, null);

    /// <summary><para>对右键菜单设备执行快速刷写（boot 分区）。</para>Quick-flash the context-menu device (boot partition).</summary>
    private void OnQuickFlashMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteForMenuDevice(sender, static vm => vm.QuickFlashCommand, "boot");

    /// <summary><para>备份右键菜单 Fastboot 设备的 boot 分区。</para>Back up the context-menu Fastboot device's boot partition.</summary>
    private void OnBackupMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteForMenuDevice(sender, static vm => vm.BackupPartitionCommand, "boot");

    /// <summary><para>还原右键菜单 Fastboot 设备的 boot 分区。</para>Restore the context-menu Fastboot device's boot partition.</summary>
    private void OnRestoreMenuClick(object? sender, RoutedEventArgs e)
        => ExecuteForMenuDevice(sender, static vm => vm.RestorePartitionCommand, "boot");

    private void ExecuteForMenuDevice(object? sender,
        Func<DeviceViewModel, System.Windows.Input.ICommand> getCommand, object? parameter)
    {
        if (SelectMenuDevice(sender) is null || DataContext is not DeviceViewModel vm) return;
        var command = getCommand(vm);
        if (command.CanExecute(parameter)) command.Execute(parameter);
    }
}