using Avalonia.Controls;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.ViewModels.Flash;

public sealed partial class FlashViewModel
{
    private readonly LocalizedOptionSet _rootFileSourceOptions = LocalizedOptionSet.FromOptions(
        LocalizedOption.FromText("Magisk"),
        LocalizedOption.FromText("Alpha"),
        LocalizedOption.FromText("KernelSU"),
        LocalizedOption.FromText("SukiSU"),
        LocalizedOption.FromText("APatch"),
        LocalizedOption.FromResource("BootPatch_CustomFile"));

    private Control? _surface;

    /// <summary>
    /// The attached page control; every named control is resolved from its name scope.
    /// <para>已附加的页面控件；所有具名控件都从它的名称域解析。</para>
    /// </summary>
    private Control Surface => _surface ?? throw new InvalidOperationException(
        "The flash view model is not attached to a page. 烧录视图模型尚未附加到页面。");

    /// <summary>
    /// Attaches the page whose controls the flash orchestration reads and writes.
    /// <para>附加页面，刷写编排从该页面读取并写回控件。</para>
    /// </summary>
    /// <param name="surface">The page control created by XAML.<para>由 XAML 创建的页面控件。</para></param>
    internal void Attach(Control surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _surface = surface;
        CaptureControlIntent();
        InitializeSurface();
        RefreshDeviceUiGate();
    }

    /// <summary>
    /// Resolves one named control from the attached page, or returns a detached dummy control if not found.
    /// <para>从已附加页面解析一个具名控件，未找到时返回分离的虚拟控件。</para>
    /// </summary>
    /// <typeparam name="T">The expected control type.<para>期望的控件类型。</para></typeparam>
    /// <param name="name">The XAML name.<para>XAML 名称。</para></param>
    /// <returns>The resolved control or a detached dummy; a view model that is not attached to a page reads and
    /// writes that dummy instead of failing, so page state stays usable without a view.
    /// <para>解析到的控件或分离的虚拟控件；未附加到页面的视图模型读写该虚拟控件而不是失败，因此页面状态在没有视图时
    /// 仍然可用。</para></returns>
    private T Field<T>(string name) where T : Control, new() =>
        _surface?.FindControl<T>(name) ?? new T();

    private void InitializeSurface()
    {
        Field<ComboBox>("RootFileSourceBox").ItemsSource = _rootFileSourceOptions.Options;
        Field<ComboBox>("RootFileSourceBox").SelectedIndex = 0;
        Field<ComboBox>("RootVersionBox").ItemsSource = _rootVersionLabels;
        Field<ComboBox>("RootVersionBox").SelectionChanged += OnRootVersionChanged;
        Field<CheckBox>("AddRootBox").IsCheckedChanged += (_, _) => UpdateRootControlsState();
        RefreshRootPartitionOptions();
        UpdateRootControlsState();

        Field<ComboBox>("EdlSectorSizeBox").ItemsSource = _edlSectorSizeOptions.Options;
        Field<ComboBox>("EdlSectorSizeBox").SelectedIndex = 0;
        Field<ComboBox>("EdlSlotBox").ItemsSource = _edlSlotOptions.Options;
        Field<ComboBox>("EdlSlotBox").SelectedIndex = 0;
        Field<ComboBox>("MtkProtocolBox").ItemsSource = _mtkProtocolOptions.Options;
        Field<ComboBox>("MtkProtocolBox").SelectedIndex = 0;
        Field<ComboBox>("MtkStageBox").ItemsSource = _mtkStageOptions.Options;
        Field<ComboBox>("MtkStageBox").SelectedIndex = 0;
        Field<ComboBox>("MtkStageBox").SelectionChanged += (_, _) => UpdateEdlButton();
        Field<ComboBox>("MtkProtocolBox").SelectionChanged += (_, _) => UpdateEdlButton();
        Field<ComboBox>("MtkStorageKindBox").ItemsSource = new[] { "eMMC", "UFS", "NOR", "NAND", "Auto" };
        Field<ComboBox>("MtkStorageKindBox").SelectedIndex = 0;
        Field<ComboBox>("MtkLegacyCompletionBox").ItemsSource = Enum.GetNames<MtkLegacyCompletionProfile>();
        Field<ComboBox>("MtkLegacyCompletionBox").SelectedIndex = 0;
        Field<ComboBox>("EdlLunBox").ItemsSource = Enumerable.Range(0, 8).ToArray();
        Field<ComboBox>("EdlLunBox").SelectedIndex = 0;
        Field<ComboBox>("RkStorageTypeBox").ItemsSource = RkStorageTypes;
        Field<ComboBox>("RkStorageTypeBox").SelectedIndex = -1;
        UpdateEdlProgrammerPickOptions();
        WarmRootCatalogs();
        PropertyChanged += OnModelPropertyChanged;
    }

    /// <summary>
    /// Starts observing the globally selected device while the page is attached.
    /// <para>页面附加期间开始观察全局选中的设备。</para>
    /// </summary>
    internal void OnAttached()
    {
        _deviceViewModel = DeviceViewModel.Instance;
        if (_deviceViewModel is not null)
        {
            _deviceViewModel.PropertyChanged += OnGlobalDeviceChanged;
        }

        _lastObservedDevice = SelectedDevice;
        ApplyDeviceProtocolExpansion(SelectedDevice?.Protocol);
        UpdateObservedDeviceInfo();
        UpdateEdlButton();
    }

    /// <summary>
    /// Stops observing the globally selected device and publishes the remaining log lines.
    /// <para>停止观察全局选中的设备，并发布剩余日志行。</para>
    /// </summary>
    internal void OnDetached()
    {
        if (_deviceViewModel is not null)
        {
            _deviceViewModel.PropertyChanged -= OnGlobalDeviceChanged;
        }

        _deviceViewModel = null;
        CancelRootOperations();
        UpdateObservedDeviceInfo();
        FlushPendingLog();
    }
}