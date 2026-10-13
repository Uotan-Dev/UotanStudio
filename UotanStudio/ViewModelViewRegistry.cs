using System.Collections.Frozen;
using Avalonia.Controls;
using UotanStudio.ViewModels.Compare;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Compare;
using UotanStudio.Views.Devices;
using UotanStudio.Views.Editors;
using UotanStudio.Views.Nodes;
using UotanStudio.Views.Shell;
using UotanStudio.Views.Workspace;

namespace UotanStudio;

/// <summary>
/// Single AOT-safe catalog of view-model-to-control factories with presentation metadata for
/// <c>ViewLocator</c>, plus factories for the four on-demand top-level tabs.
/// <para>唯一的 AOT 安全控件工厂目录：按数据模型类型登记工厂与呈现元数据以便 <c>ViewLocator</c> 查找，
/// 并登记四个顶层页签的按需工厂。</para>
/// </summary>
internal static class ViewModelViewRegistry
{
    /// <summary>
    /// A single registration entry: the control factory plus presentation metadata.
    /// <para>单条登记条目：控件工厂与呈现元数据。</para>
    /// </summary>
    /// <param name="CreateView">The factory producing the control. <para>产出控件的工厂。</para></param>
    /// <param name="IsDocumentStyle">
    /// Whether this entry needs document-style chrome: a transparent background and a click-to-activate handler.
    /// <para>该条目是否需要文档型外观：透明背景与点击激活上报。</para>
    /// </param>
    internal readonly record struct Entry(Func<Control> CreateView, bool IsDocumentStyle = false);

    /// <summary>
    /// Map of VM type → view registration entry.
    /// <para>VM 类型 → 视图登记条目 的映射表。</para>
    /// </summary>
    private static readonly FrozenDictionary<Type, Entry> _registry = new Dictionary<Type, Entry>
    {
        [typeof(CompareViewModel)] = new(() => new CompareView()),
        [typeof(FileViewModel)] = new(() => new FileView(), IsDocumentStyle: true),
        [typeof(DtbEditorViewModel)] = new(() => new DtbEditorView(), IsDocumentStyle: true),
        [typeof(DexEditorViewModel)] = new(() => new DexEditorView(), IsDocumentStyle: true),
        [typeof(ElfInfoViewModel)] = new(() => new ElfInfoView(), IsDocumentStyle: true),
        [typeof(QualcommImageViewModel)] = new(() => new QualcommImageView(), IsDocumentStyle: true),
        [typeof(QcnEditorViewModel)] = new(() => new QcnEditorView()),
        [typeof(ManifestEditorViewModel)] = new(() => new ManifestEditorView(), IsDocumentStyle: true),
        [typeof(PartitionEditorViewModel)] = new(() => new PartitionEditorView(), IsDocumentStyle: true),
        [typeof(ArscEditorViewModel)] = new(() => new ArscEditorView(), IsDocumentStyle: true),
        [typeof(HexEditorViewModel)] = new(() => new HexEditorView(), IsDocumentStyle: true),
        [typeof(LogEditorViewModel)] = new(() => new LogEditorView(), IsDocumentStyle: true),
        [typeof(ScreenMirrorViewModel)] = new(() => new ScreenMirrorView()),
        [typeof(ApkEditorViewModel)] = new(() => new ApkEditorView(), IsDocumentStyle: true),
        [typeof(FirmwareInfoViewModel)] = new(() => new FirmwareInfoView(), IsDocumentStyle: true),
        [typeof(FileManagerEditorViewModel)] = new(() => new FileManagerEditorView(), IsDocumentStyle: true),
        [typeof(ImageViewModel)] = new(() => new ImageView()),
        [typeof(AiAssistantViewModel)] = new(() => new AiAssistantView()),
        [typeof(AiSettingsViewModel)] = new(() => new AiSettingsView()),
        [typeof(ProjectExplorerViewModel)] = new(() => new ProjectExplorerView()),
        [typeof(ProjectSettingViewModel)] = new(() => new ProjectSettingView()),
        [typeof(PluginToolViewModel)] = new(() => new PatchToolView()),
        [typeof(PropertiesViewModel)] = new(() => new PropertiesView()),
        [typeof(DeviceViewModel)] = new(() => new DeviceView()),
        [typeof(WorkspaceLogViewModel)] = new(() => new WorkspaceLogView()),
        [typeof(AdbLogViewModel)] = new(() => new AdbLogView()),
        [typeof(TerminalViewModel)] = new(() => new TerminalView()),
    }.ToFrozenDictionary();

    /// <summary>
    /// Top-level tab identifiers for the four pages the main window creates on demand.
    /// <para>顶层页签枚举，标识主窗口底栏按需创建的四个页面。</para>
    /// </summary>
    internal enum TopLevelPage
    {
        /// <summary>The flash tab.<para>烧录标签页。</para></summary>
        Flash,
        /// <summary>The delivery tab.<para>交付标签页。</para></summary>
        Delivery,
        /// <summary>The node editor tab.<para>节点编辑标签页。</para></summary>
        NodeEditor,
        /// <summary>The settings tab.<para>设置标签页。</para></summary>
        Settings,
    }

    /// <summary>
    /// Map of top-level tab → page factory.
    /// <para>顶层页签 → 页面工厂 的映射表。</para>
    /// </summary>
    private static readonly FrozenDictionary<TopLevelPage, Func<Control>> _topLevelPages =
        new Dictionary<TopLevelPage, Func<Control>>
        {
            [TopLevelPage.Flash] = () => new FlashView(),
            [TopLevelPage.Delivery] = () => new DeliveryView(),
            [TopLevelPage.NodeEditor] = () => new NodeEditorView(),
            [TopLevelPage.Settings] = () => new SettingsView(),
        }.ToFrozenDictionary();

    /// <summary>
    /// Looks up a registered control factory by data-model type; returns <c>null</c> when unregistered.
    /// <para>按数据模型类型查找已登记的控件工厂；未登记时返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="viewModelType">The data-model type to look up. <para>待查找的数据模型类型。</para></param>
    /// <returns>
    /// The control-factory delegate; <c>null</c> when unregistered.
    /// <para>控件工厂委托；未登记时为 <c>null</c>。</para>
    /// </returns>
    public static Func<Control>? Resolve(Type viewModelType)
    {
        return _registry.TryGetValue(viewModelType, out var entry) ? entry.CreateView : null;
    }

    /// <summary>
    /// Whether the entry for a data-model type uses document-style chrome; false when unregistered.
    /// <para>某数据模型类型的条目是否使用文档型外观；未登记时为 false。</para>
    /// </summary>
    /// <param name="viewModelType">The data-model type to check. <para>待检查的数据模型类型。</para></param>
    /// <returns>
    /// <c>true</c> when a registered entry is flagged <c>IsDocumentStyle</c>.
    /// <para>条目存在且标记为 <c>IsDocumentStyle</c> 时返回 <c>true</c>。</para>
    /// </returns>
    public static bool IsDocumentStyle(Type viewModelType)
    {
        return _registry.TryGetValue(viewModelType, out var entry) && entry.IsDocumentStyle;
    }

    /// <summary>
    /// Creates a control for the given top-level tab.
    /// <para>按顶层页签创建对应控件。</para>
    /// </summary>
    /// <param name="page">The top-level tab identifier. <para>顶层页签标识。</para></param>
    /// <returns>The control for that tab. <para>该页签对应的控件。</para></returns>
    public static Control CreateTopLevelPage(TopLevelPage page)
    {
        return _topLevelPages[page]();
    }
}