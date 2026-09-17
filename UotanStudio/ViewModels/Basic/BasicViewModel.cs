using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UotanStudio.ViewModels.Basic;

/// <summary>
/// <para>基础页面「CPU 频率」区域里的单个核心条目：核心序号与当前频率文本。</para>
/// A single core row in the basic page's CPU-frequency section: the core index and its current-frequency text.
/// </summary>
public sealed partial class BasicCpuCoreEntry : ObservableObject
{
    /// <summary><para>核心序号（0 起）。</para>The zero-based core index.</summary>
    public int Index { get; init; }

    /// <summary>
    /// <para>当前频率文本；设备数据接入前保持占位符，界面只展示布局。</para>
    /// Current-frequency text; kept as a placeholder until device data is wired, so the page only shows layout.
    /// </summary>
    [ObservableProperty]
    private string _frequency = BasicViewModel.Placeholder;
}

/// <summary>
/// <para>基本页面「可用频率表」区域里的单个核心条目：核心序号与可用频率列表文本。</para>
/// A single core row in the basic page's available-frequency table: the core index and its frequency list.
/// </summary>
public sealed partial class BasicFrequencyEntry : ObservableObject
{
    /// <summary><para>核心序号（0 起）。</para>The zero-based core index.</summary>
    public int Index { get; init; }

    /// <summary>
    /// <para>可用频率列表文本；设备数据接入前保持占位符。</para>
    /// Available-frequency list text; kept as a placeholder until device data is wired.
    /// </summary>
    [ObservableProperty]
    private string _frequencies = BasicViewModel.Placeholder;

    /// <summary>
    /// <para>该核心的可用频率是否被选中；频率表整行都是可点选的复选框，选中状态保存在这里，
    /// 设备数据接入前保持未选中。</para>
    /// Whether this core's available frequencies are selected; the table rows are selectable and hold their
    /// state here. It stays unselected until the backend supplies data.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// <para>基础页面（底部导航「基础」）的界面状态：左侧菜单选中项、四项设备状态、
/// 右侧固定面板的标签页、CPU 频率区域以及日志占位文本。</para>
/// Basic page (the bottom "Basic" entry) state: the selected left-hand menu section, the four device-status
/// values, the fixed right-hand tab index, the CPU-frequency section and the log placeholder.
/// </summary>
/// <remarks>
/// <para>本页当前只迁移界面：除菜单切换与纯本地交互外没有任何后端调用，设备状态、CPU 频率、
/// 文件列表与日志都由后端接入后填充，未接入时统一显示占位符。</para>
/// The page currently migrates the interface only: apart from menu switching and purely local interaction
/// there are no backend calls. Device status, CPU frequencies, file lists and logs are filled once the
/// backend is wired; until then they show placeholders.
/// </remarks>
public sealed partial class BasicViewModel : ViewModelBase
{
    /// <summary><para>尚未取得数据时显示的占位符。</para>The placeholder shown while no data is available.</summary>
    public const string Placeholder = "--";

    /// <summary>
    /// <para>绑定用的占位文本：视图里所有尚未接入数据的字段都指向这里，占位符只需维护一处。</para>
    /// Bindable placeholder text: every field that has no data behind it yet points here, so the placeholder
    /// is maintained in a single place.
    /// </summary>
    public string PlaceholderText => Placeholder;

    /// <summary>
    /// <para>「CPU 频率」区域展示的核心数量；只决定占位行数，不代表真实核心数。</para>
    /// Number of cores shown in the CPU-frequency section; it only sizes the placeholder rows, not the real core count.
    /// </summary>
    private const int PlaceholderCoreCount = 8;

    /// <summary>
    /// <para>左侧菜单当前选中的分区序号（0 起）。中栏把六个分段纵向合并成一页，所以这个序号不再切换可见性，
    /// 只表示「该滚动到哪一个分段」，滚动位置变化时也会同步回来。</para>
    /// The selected left-hand menu section (zero-based). The middle column stacks all six sections in one page, so
    /// this index no longer toggles visibility: it names the section to scroll to and follows the scroll position.
    /// </summary>
    [ObservableProperty]
    private int _selectedSection;

    /// <summary>
    /// <para>右侧固定面板当前选中的标签页；窗口宽度较小时该面板的标签头由视图隐藏。</para>
    /// The selected tab of the fixed right-hand panel; the view hides its tab headers on narrow windows.
    /// </summary>
    [ObservableProperty]
    private int _panelTabIndex;

    /// <summary><para>设备连接状态。</para>The device connection status.</summary>
    [ObservableProperty]
    private string _status = Placeholder;

    /// <summary><para>设备机型代号。</para>The device model code.</summary>
    [ObservableProperty]
    private string _codeName = Placeholder;

    /// <summary><para>Bootloader 锁状态。</para>The bootloader lock status.</summary>
    [ObservableProperty]
    private string _blStatus = Placeholder;

    /// <summary><para>VAB 状态。</para>The VAB status.</summary>
    [ObservableProperty]
    private string _vabStatus = Placeholder;

    /// <summary>
    /// <para>电池电量百分比（0–100）；设备数据接入前保持 0，圆形水波进度显示为无水位的空圆。</para>
    /// Battery level percentage (0-100); it stays at 0 until device data is wired, leaving the wave gauge with an
    /// empty water level.
    /// </summary>
    [ObservableProperty]
    private double _batteryLevel;

    /// <summary><para>电池容量与充电状态文本；设备数据接入前保持占位符。</para>
    /// Battery capacity and charging-state text; kept as a placeholder until device data is wired.</summary>
    [ObservableProperty]
    private string _batteryInfo = Placeholder;

    /// <summary>
    /// <para>存储占用百分比（0–100）；设备数据接入前保持 0，环形进度显示为空环。</para>
    /// Storage usage percentage (0-100); it stays at 0 until device data is wired, leaving the ring empty.
    /// </summary>
    [ObservableProperty]
    private double _storageLevel;

    /// <summary><para>存储容量文本（已用/总量）；设备数据接入前保持占位符。</para>
    /// Storage capacity text (used/total); kept as a placeholder until device data is wired.</summary>
    [ObservableProperty]
    private string _storageInfo = Placeholder;

    /// <summary>
    /// <para>内存占用百分比（0–100）；设备数据接入前保持 0，环形进度显示为空环。</para>
    /// Memory usage percentage (0-100); it stays at 0 until device data is wired, leaving the ring empty.
    /// </summary>
    [ObservableProperty]
    private double _memoryLevel;

    /// <summary><para>内存容量文本（已用/总量）；设备数据接入前保持占位符。</para>
    /// Memory capacity text (used/total); kept as a placeholder until device data is wired.</summary>
    [ObservableProperty]
    private string _memoryInfo = Placeholder;

    /// <summary><para>右侧日志面板的文本；后端接入前保持为空。</para>Log text of the right-hand panel; empty until the backend is wired.</summary>
    [ObservableProperty]
    private string _logText = string.Empty;

    /// <summary>
    /// <para>文件列表是否为空：为真时显示空状态提示。数据接入前恒为真。</para>
    /// Whether the file list is empty; the empty-state hint shows while it is true. It stays true until data is wired.
    /// </summary>
    [ObservableProperty]
    private bool _isFileListEmpty = true;

    /// <summary><para>按核心序号排列的当前频率占位条目。</para>Current-frequency placeholder rows ordered by core index.</summary>
    public ObservableCollection<BasicCpuCoreEntry> CpuCores { get; } =
        [.. Enumerable.Range(0, PlaceholderCoreCount).Select(index => new BasicCpuCoreEntry { Index = index })];

    /// <summary><para>按核心序号排列的可用频率占位条目。</para>Available-frequency placeholder rows ordered by core index.</summary>
    public ObservableCollection<BasicFrequencyEntry> AvailableFrequencies { get; } =
        [.. Enumerable.Range(0, PlaceholderCoreCount).Select(index => new BasicFrequencyEntry { Index = index })];

    /// <summary><para>清空右侧日志面板的本地文本。</para>Clears the local text of the right-hand log panel.</summary>
    [RelayCommand]
    private void ClearLog() => LogText = string.Empty;
}