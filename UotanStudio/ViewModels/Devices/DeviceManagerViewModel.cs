using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Devices;
using UotanStudio.Abstractions.Logging;
using UotanStudio.Services;
using UotanStudio.Services.Devices;

namespace UotanStudio.ViewModels.Devices
{
    /// <summary>
    /// <para>设备管理器视图模型，驱动设备管理器 UI，随连接设备能力动态调整操作按钮。</para>
    /// Device manager view model, driving the device-manager UI and dynamically adjusting action buttons based on the connected device's capabilities.
    /// </summary>
    public partial class DeviceManagerViewModel : ViewModelBase
    {
        private readonly LogService _log = LogService.Instance;
        private DeviceManager? _deviceManager;

        /// <summary>
        /// <para>当前已识别的设备列表。</para>
        /// List of currently identified devices.
        /// </summary>
        public ObservableCollection<DeviceInfo> Devices { get; } = new();

        /// <summary>
        /// <para>当前选中的设备，决定操作按钮启用状态与能力描述显示。</para>
        /// Currently selected device, determining the action button enablement and capability display.
        /// </summary>
        public DeviceInfo? SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (SetProperty(ref _selectedDevice, value))
                {
                    RefreshActionAvailability();
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(CanFlash));
                    OnPropertyChanged(nameof(CanErase));
                    OnPropertyChanged(nameof(CanReboot));
                    OnPropertyChanged(nameof(CanAdbShell));
                }
            }
        }

        private DeviceInfo? _selectedDevice;

        /// <summary>
        /// <para>是否已选中设备。</para>
        /// Whether a device is selected.
        /// </summary>
        public bool HasSelection => SelectedDevice != null;

        /// <summary>
        /// <para>能否执行刷写（当前选中设备能力支持）。</para>
        /// Whether flashing can be performed (supported by the selected device's capabilities).
        /// </summary>
        public bool CanFlash => SelectedDevice?.Capabilities.SupportsFastbootFlash ?? false;

        /// <summary>
        /// <para>能否执行擦除。</para>
        /// Whether erasing can be performed.
        /// </summary>
        public bool CanErase => SelectedDevice?.Capabilities.SupportsPartitionErase ?? false;

        /// <summary>
        /// <para>能否重启到不同模式。</para>
        /// Whether rebooting to different modes is supported.
        /// </summary>
        public bool CanReboot => SelectedDevice?.Capabilities.SupportsRebootModes ?? false;

        /// <summary>
        /// <para>能否打开 ADB shell。</para>
        /// Whether an ADB shell can be opened.
        /// </summary>
        public bool CanAdbShell => SelectedDevice?.Capabilities.SupportsAdbShell ?? false;

        /// <summary>
        /// <para>是否正在枚举设备。</para>
        /// Whether devices are being enumerated.
        /// </summary>
        public bool IsEnumerating
        {
            get => _isEnumerating;
            set => SetProperty(ref _isEnumerating, value);
        }
        private bool _isEnumerating;

        /// <summary>
        /// <para>设备管理器是否已初始化。</para>
        /// Whether the device manager has been initialized.
        /// </summary>
        public bool IsInitialized => _deviceManager != null;

        /// <summary>
        /// <para>构造设备管理器视图模型，自动启动设备管理器与枚举。</para>
        /// Constructs the device manager view model, automatically starting the device manager and enumeration.
        /// </summary>
        public DeviceManagerViewModel()
        {
            InitializeDeviceManager();
        }

        private void InitializeDeviceManager()
        {
            _deviceManager = new DeviceManager(_log);
            _deviceManager.DevicesChanged += OnDevicesChanged;
            _ = EnumerateDevicesAsync();
        }

        private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e)
        {
            // UI 线程刷新；此处简化为同步替换集合
            Devices.Clear();
            if (_deviceManager != null)
            {
                foreach (var d in _deviceManager.Devices) Devices.Add(d);
            }

            // 保持选中设备引用一致
            if (SelectedDevice != null && e.Device != null && e.Device.Serial == SelectedDevice.Serial)
            {
                _selectedDevice = e.Device;
                OnPropertyChanged(nameof(SelectedDevice));
                RefreshActionAvailability();
            }
        }

        /// <summary>
        /// <para>异步枚举设备。可由 UI“刷新”按钮调用。</para>
        /// Asynchronously enumerates devices. May be invoked by the UI "Refresh" button.
        /// </summary>
        [RelayCommand]
        public async Task EnumerateDevicesAsync()
        {
            if (IsEnumerating || _deviceManager == null) return;
            IsEnumerating = true;
            try
            {
                _log.Write(LogLevel.Info, "DeviceUI", "Enumerating devices...");
                await _deviceManager.EnumerateAsync().ConfigureAwait(false);
                _log.Write(LogLevel.Info, "DeviceUI", $"Enumeration complete: {Devices.Count} device(s)");
            }
            finally
            {
                IsEnumerating = false;
            }
        }

        /// <summary>
        /// <para>连接指定协议的设备。</para>
        /// Connects a device using the specified protocol.
        /// </summary>
        [RelayCommand]
        public async Task ConnectAsync(string protocol)
        {
            if (_deviceManager == null) return;
            if (!System.Enum.TryParse<DeviceProtocol>(protocol, true, out var proto))
            {
                _log.Write(LogLevel.Warn, "DeviceUI", $"Unknown protocol: {protocol}");
                return;
            }

            // 简化：直接构造一个占位 serial 并尝试连接
            var placeholderSerial = $"{proto}-device-0";
            var info = await _deviceManager.ConnectAsync(placeholderSerial, proto).ConfigureAwait(false);
            if (info != null)
            {
                SelectedDevice = info;
                _log.Write(LogLevel.Info, "DeviceUI", $"Connected to {placeholderSerial} via {proto}");
            }
        }

        /// <summary>
        /// <para>断开当前选中设备。</para>
        /// Disconnects the currently selected device.
        /// </summary>
        [RelayCommand]
        public async Task DisconnectAsync()
        {
            if (_deviceManager == null || SelectedDevice == null) return;
            var serial = SelectedDevice.Serial;
            await _deviceManager.DisconnectAsync(serial).ConfigureAwait(false);
            _log.Write(LogLevel.Info, "DeviceUI", $"Disconnected {serial}");
            SelectedDevice = null;
        }

        /// <summary>
        /// <para>刷写分区到当前选中设备。</para>
        /// Flashes a partition to the currently selected device.
        /// </summary>
        /// <param name="command">
        /// <para>格式 <c>partition|imagePath</c>。</para>
        /// Format <c>partition|imagePath</c>.
        /// </param>
        [RelayCommand]
        public async Task FlashAsync(string command)
        {
            if (_deviceManager == null || SelectedDevice == null) return;
            var parts = command.Split('|');
            if (parts.Length < 2)
            {
                _log.Write(LogLevel.Warn, "DeviceUI", "Flash command requires partition|imagePath");
                return;
            }

            var partition = parts[0];
            var imagePath = parts[1];
            var result = await _deviceManager.FlashPartitionAsync(SelectedDevice.Serial, partition, imagePath).ConfigureAwait(false);
            _log.Write(result.Success ? LogLevel.Info : LogLevel.Error, "DeviceUI",
                $"Flash {partition}: {(result.Success ? "OK" : result.Error)}");
        }

        /// <summary>
        /// <para>擦除当前选中设备的指定分区。</para>
        /// Erases the specified partition on the currently selected device.
        /// </summary>
        [RelayCommand]
        public async Task EraseAsync(string partition)
        {
            if (_deviceManager == null || SelectedDevice == null) return;
            var result = await _deviceManager.ErasePartitionAsync(SelectedDevice.Serial, partition).ConfigureAwait(false);
            _log.Write(result.Success ? LogLevel.Info : LogLevel.Error, "DeviceUI",
                $"Erase {partition}: {(result.Success ? "OK" : result.Error)}");
        }

        /// <summary>
        /// <para>重启当前选中设备到指定模式。</para>
        /// Reboots the currently selected device to the specified mode.
        /// </summary>
        [RelayCommand]
        public async Task RebootAsync(string mode)
        {
            if (_deviceManager == null || SelectedDevice == null) return;
            if (!System.Enum.TryParse<RebootMode>(mode, true, out var rebootMode))
            {
                _log.Write(LogLevel.Warn, "DeviceUI", $"Unknown reboot mode: {mode}");
                return;
            }

            var result = await _deviceManager.RebootAsync(SelectedDevice.Serial, rebootMode).ConfigureAwait(false);
            _log.Write(result.Success ? LogLevel.Info : LogLevel.Error, "DeviceUI",
                $"Reboot {mode}: {(result.Success ? "OK" : result.Error)}");
        }

        private void RefreshActionAvailability()
        {
            OnPropertyChanged(nameof(CanFlash));
            OnPropertyChanged(nameof(CanErase));
            OnPropertyChanged(nameof(CanReboot));
            OnPropertyChanged(nameof(CanAdbShell));
        }
    }
}
