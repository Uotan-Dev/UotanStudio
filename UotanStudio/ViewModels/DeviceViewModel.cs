using CommunityToolkit.Mvvm.Input;
using FirmwareKit.Comm.Fastboot.Usb;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using UotanStudio.Services;

namespace UotanStudio.ViewModels
{
    /// <summary>
    /// <para>设备视图模型，管理 ADB/Fastboot 设备列表与连接状态。</para>
    /// Device view model, managing ADB/Fastboot device list and connection status.
    /// </summary>
    public partial class DeviceViewModel : ViewModelBase
    {
        private ObservableCollection<DeviceInfo> _devices = [];
        private DeviceInfo? _selectedDevice;
        private bool _isRefreshing;
        private string _statusText = string.Empty;

        /// <summary>
        /// <para>获取或设置已检测到的设备列表。</para>
        /// Gets or sets the list of detected devices.
        /// </summary>
        public ObservableCollection<DeviceInfo> Devices
        {
            get => _devices;
            set => SetProperty(ref _devices, value);
        }

        /// <summary>
        /// <para>获取或设置当前选中的设备。</para>
        /// Gets or sets the currently selected device.
        /// </summary>
        public DeviceInfo? SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (SetProperty(ref _selectedDevice, value))
                    OnPropertyChanged(nameof(DeviceDetail));
            }
        }

        /// <summary>
        /// <para>获取或设置是否正在刷新设备列表。</para>
        /// Gets or sets whether the device list is currently refreshing.
        /// </summary>
        public bool IsRefreshing
        {
            get => _isRefreshing;
            set => SetProperty(ref _isRefreshing, value);
        }

        /// <summary>
        /// <para>获取或设置状态文本。</para>
        /// Gets or sets the status text.
        /// </summary>
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        /// <summary>
        /// <para>获取 ADB 模式设备视图集合。</para>
        /// Gets the view collection of ADB-mode devices.
        /// </summary>
        public ObservableCollection<DeviceInfo> AdbDevices { get; } = [];

        /// <summary>
        /// <para>获取 Fastboot 模式设备视图集合。</para>
        /// Gets the view collection of Fastboot-mode devices.
        /// </summary>
        public ObservableCollection<DeviceInfo> FastbootDevices { get; } = [];

        /// <summary>
        /// <para>获取是否没有任何设备（用于显示空状态提示）。</para>
        /// Gets whether there are no devices (used to show the empty-state hint).
        /// </summary>
        public bool HasNoDevices => !IsRefreshing && Devices.Count == 0;

        /// <summary>
        /// <para>获取选中设备的详情文本。</para>
        /// Gets the detail text of the selected device.
        /// </summary>
        public string DeviceDetail => SelectedDevice is null
            ? string.Empty
            : $"Serial: {SelectedDevice.Serial}\nMode: {SelectedDevice.Mode}\nState: {SelectedDevice.State}\nModel: {SelectedDevice.Model}\nAndroid: {SelectedDevice.AndroidVersion}";

        /// <summary>
        /// <para>异步刷新设备列表，调用 AdbService 枚举 ADB/Fastboot 设备。</para>
        /// Asynchronously refreshes the device list by invoking AdbService to enumerate ADB/Fastboot devices.
        /// </summary>
        [RelayCommand]
        private async Task RefreshDevicesAsync()
        {
            if (IsRefreshing) return;
            IsRefreshing = true;
            StatusText = LanguageService.Instance.GetString("Device_Scanning");
            OnPropertyChanged(nameof(HasNoDevices));
            try
            {
                // 优先枚举 Fastboot USB 设备（FirmwareKit.Comm.Fastboot.Usb.UsbManager）
                var fbDevices = UsbManager.GetAllDevices() ?? new();
                Devices.Clear();
                foreach (var d in fbDevices)
                {
                    Devices.Add(new DeviceInfo
                    {
                        Serial = d.SerialNumber ?? d.DevicePath ?? string.Empty,
                        State = "fastboot",
                        Mode = "Fastboot"
                    });
                }

                // 补充 ADB 设备（通过 AdbService，与 Fastboot 合并呈现）
                var adbList = await AdbService.Instance.GetDevicesAsync();
                foreach (var d in adbList)
                    Devices.Add(new DeviceInfo { Serial = d.Serial, State = d.State, Mode = d.Mode });

                StatusText = Devices.Count == 0
                    ? LanguageService.Instance.GetString("Device_NoDevices")
                    : $"{Devices.Count}{LanguageService.Instance.GetString("Device_DeviceCount")}";

                // 并发拉取每个已识别 ADB 设备的属性，丰富详情面板
                var adbDevices = Devices.Where(x => x.Mode == "ADB").ToList();
                if (adbDevices.Count > 0)
                    await Task.WhenAll(adbDevices.Select(EnrichDeviceInfoAsync));
            }
            catch (OperationCanceledException)
            {
                StatusText = LanguageService.Instance.GetString("Device_NoDevices");
            }
            finally
            {
                IsRefreshing = false;
                RebuildGroups();
                OnPropertyChanged(nameof(HasNoDevices));
            }
        }

        /// <summary>
        /// <para>根据设备 Mode 重新构建 ADB/Fastboot 分组。</para>
        /// Rebuilds ADB/Fastboot groups based on device Mode.
        /// </summary>
        private void RebuildGroups()
        {
            AdbDevices.Clear();
            FastbootDevices.Clear();
            foreach (var d in Devices)
            {
                if (d.Mode == "Fastboot")
                    FastbootDevices.Add(d);
                else
                    AdbDevices.Add(d);
            }
        }

        /// <summary>
        /// <para>异步拉取设备 getprop 属性填充 Model/AndroidVersion 字段。</para>
        /// Asynchronously retrieves device getprop properties to populate Model/AndroidVersion fields.
        /// </summary>
        private async Task EnrichDeviceInfoAsync(DeviceInfo device)
        {
            if (device.Mode != "ADB") return;
            try
            {
                var props = await AdbService.Instance.GetPropertiesAsync(device.Serial);
                if (props.TryGetValue("ro.product.model", out var model))
                    device.Model = model;
                if (props.TryGetValue("ro.build.version.release", out var version))
                    device.AndroidVersion = version;
                // 触发详情刷新（若当前选中即此设备）
                if (SelectedDevice == device)
                    OnPropertyChanged(nameof(DeviceDetail));
            }
            catch
            {
                // 属性拉取失败不影响主流程
            }
        }

        /// <summary>
        /// <para>连接到选中的设备（验证可达性）。</para>
        /// Connects to the selected device (verifies reachability).
        /// </summary>
        [RelayCommand]
        private async Task ConnectAsync()
        {
            if (SelectedDevice == null) return;
            StatusText = $"{LanguageService.Instance.GetString("Device_Connecting")} {SelectedDevice.Serial}";
            try
            {
                if (SelectedDevice.Mode == "ADB")
                {
                    var r = await AdbService.Instance.ExecuteAdbAsync(SelectedDevice.Serial, "get-state");
                    SelectedDevice.State = r.Success ? "device" : "offline";
                }
                else
                {
                    var r = await AdbService.Instance.ExecuteFastbootAsync(SelectedDevice.Serial, "getvar product");
                    SelectedDevice.State = r.Success ? "fastboot" : "offline";
                }
                StatusText = SelectedDevice.State == "offline"
                    ? $"{LanguageService.Instance.GetString("Device_Connect")} {SelectedDevice.Serial} FAIL"
                    : $"{LanguageService.Instance.GetString("Device_Connect")} {SelectedDevice.Serial} OK";
                OnPropertyChanged(nameof(DeviceDetail));
            }
            catch
            {
                StatusText = $"{LanguageService.Instance.GetString("Device_Connect")} {SelectedDevice.Serial} FAIL";
            }
        }

        /// <summary>
        /// <para>断开选中的设备（ADB 端 kill-server）。</para>
        /// Disconnects the selected device (ADB kill-server).
        /// </summary>
        [RelayCommand]
        private async Task DisconnectAsync()
        {
            if (SelectedDevice == null) return;
            StatusText = $"{LanguageService.Instance.GetString("Device_Disconnect")} {SelectedDevice.Serial}...";
            try
            {
                if (SelectedDevice.Mode == "ADB")
                    await AdbService.Instance.ExecuteAdbAsync(SelectedDevice.Serial, "wait-for-disconnect");
                SelectedDevice.State = "offline";
                StatusText = $"{LanguageService.Instance.GetString("Device_Disconnect")} {SelectedDevice.Serial} OK";
                OnPropertyChanged(nameof(DeviceDetail));
            }
            catch
            {
                StatusText = $"{LanguageService.Instance.GetString("Device_Disconnect")} {SelectedDevice.Serial} FAIL";
            }
        }

        /// <summary>
        /// <para>异步重启选中设备到指定模式。</para>
        /// Asynchronously reboots the selected device to the specified mode.
        /// </summary>
        /// <param name="mode">重启模式（system/bootloader/recovery/edl）。</param>
        [RelayCommand]
        private async Task RebootAsync(string? mode)
        {
            if (SelectedDevice == null || string.IsNullOrEmpty(mode)) return;
            StatusText = $"{LanguageService.Instance.GetString("Device_Reboot")} {SelectedDevice.Serial} -> {mode}...";
            try
            {
                var r = await AdbService.Instance.RebootAsync(SelectedDevice.Serial, mode);
                StatusText = r.Success
                    ? $"{LanguageService.Instance.GetString("Device_Reboot")} {SelectedDevice.Serial} -> {mode} OK"
                    : $"{LanguageService.Instance.GetString("Device_Reboot")} {SelectedDevice.Serial} FAIL";
            }
            catch
            {
                StatusText = $"{LanguageService.Instance.GetString("Device_Reboot")} {SelectedDevice.Serial} FAIL";
            }
        }
    }

    /// <summary>
    /// <para>设备信息模型。</para>
    /// Device information model.
    /// </summary>
    public class DeviceInfo : ViewModelBase
    {
        private string _serial = string.Empty;
        private string _state = string.Empty;
        private string _mode = "ADB";
        private string _model = string.Empty;
        private string _androidVersion = string.Empty;

        /// <summary>设备序列号。</summary>
        public string Serial
        {
            get => _serial;
            set => SetProperty(ref _serial, value);
        }
        /// <summary>设备状态（device/offline/fastboot 等）。</summary>
        public string State
        {
            get => _state;
            set => SetProperty(ref _state, value);
        }
        /// <summary>连接模式（ADB/Fastboot）。</summary>
        public string Mode
        {
            get => _mode;
            set => SetProperty(ref _mode, value);
        }
        /// <summary>设备型号（ro.product.model）。</summary>
        public string Model
        {
            get => _model;
            set => SetProperty(ref _model, value);
        }
        /// <summary>Android 版本（ro.build.version.release）。</summary>
        public string AndroidVersion
        {
            get => _androidVersion;
            set => SetProperty(ref _androidVersion, value);
        }
        /// <summary>显示名称。</summary>
        public string DisplayName => string.IsNullOrEmpty(Model)
            ? $"{Serial} ({State})"
            : $"{Serial} - {Model} ({State})";
    }
}
