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
/// <para>界面视图的唯一目录（AOT 安全）：既登记 ViewModel→View 工厂与呈现元数据，
/// 供 <c>ViewLocator</c> 查找，也登记顶层页签（烧录/交付/节点/设置）的页面工厂，
/// 取代此前分散在 <c>ViewLocator</c>、<c>MainView</c> 中的并行枚举与手工 <c>new</c>。</para>
/// The single catalog of views (AOT-safe): it registers the ViewModel→View factories with
/// presentation metadata for <c>ViewLocator</c>, and the top-level tab (flash/delivery/node/
/// settings) page factories, replacing the parallel enumerations and hand-written <c>new</c>
/// calls previously spread across <c>ViewLocator</c> and <c>MainView</c>.
/// </summary>
/// <remarks>
/// <para>注册表在静态字段初始化时一次性登记全部条目，不含运行时反射；新增 ViewModel 或顶层
/// 页签须在此同步登记。<c>IsDocumentStyle</c> 标记需要编辑器外观（透明背景 + 点击上报）的文档型
/// 视图，<c>ViewLocator</c> 据此施加该处理，不再维护独立的 VM 类型列表。</para>
/// The catalog is populated once in static field initializers without runtime reflection; new
/// ViewModels or top-level tabs must be registered here as well. The <c>IsDocumentStyle</c> flag
/// marks document views that need editor chrome (transparent background + click reporting);
/// <c>ViewLocator</c> applies that treatment from the flag instead of keeping its own VM-type list.
/// </remarks>
internal static class ViewModelViewRegistry
{
    /// <summary>
    /// <para>一条 ViewModel→View 登记：视图工厂与呈现元数据。</para>
    /// A single ViewModel→View registration: the view factory plus presentation metadata.
    /// </summary>
    /// <param name="CreateView"><para>创建视图控件的工厂。</para>The factory that creates the view control.</param>
    /// <param name="IsDocumentStyle">
    /// <para>是否为需要编辑器外观（透明背景 + 点击上报）的文档型视图。</para>
    /// Whether the view is a document-style editor needing a transparent background and the
    /// click-to-activate handler.
    /// </param>
    internal readonly record struct Entry(Func<Control> CreateView, bool IsDocumentStyle = false);

    /// <summary>
    /// <para>VM 类型 → 视图登记条目 的映射表。</para>
    /// Map of VM type → view registration entry.
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
    /// <para>顶层页签枚举，标识主窗口底栏按需创建的四个页面。</para>
    /// Top-level tab identifiers for the four pages the main window creates on demand.
    /// </summary>
    internal enum TopLevelPage
    {
        /// <summary><para>烧录页。</para>The flash page.</summary>
        Flash,
        /// <summary><para>交付页。</para>The delivery page.</summary>
        Delivery,
        /// <summary><para>节点编辑页。</para>The node-editor page.</summary>
        NodeEditor,
        /// <summary><para>设置页。</para>The settings page.</summary>
        Settings,
    }

    /// <summary>
    /// <para>顶层页签 → 页面工厂 的映射表。</para>
    /// Map of top-level tab → page factory.
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
    /// <para>按 ViewModel 类型查找对应视图工厂；未注册时返回 <c>null</c>。</para>
    /// Looks up the view factory for the given ViewModel type; returns <c>null</c> when unregistered.
    /// </summary>
    /// <param name="viewModelType"><para>ViewModel 类型。</para>The ViewModel type.</param>
    /// <returns>
    /// <para>视图工厂委托；未注册时为 <c>null</c>。</para>
    /// The view-factory delegate; <c>null</c> when unregistered.
    /// </returns>
    public static Func<Control>? Resolve(Type viewModelType)
    {
        return _registry.TryGetValue(viewModelType, out var entry) ? entry.CreateView : null;
    }

    /// <summary>
    /// <para>判断该 ViewModel 类型对应的视图是否为文档型编辑器外观；未注册时返回 <c>false</c>。</para>
    /// Whether the view for the given ViewModel type uses the document-style editor chrome;
    /// returns <c>false</c> when unregistered.
    /// </summary>
    /// <param name="viewModelType"><para>ViewModel 类型。</para>The ViewModel type.</param>
    /// <returns>
    /// <para>条目存在且标记为 <c>IsDocumentStyle</c> 时返回 <c>true</c>。</para>
    /// <c>true</c> when a registered entry is flagged <c>IsDocumentStyle</c>.
    /// </returns>
    public static bool IsDocumentStyle(Type viewModelType)
    {
        return _registry.TryGetValue(viewModelType, out var entry) && entry.IsDocumentStyle;
    }

    /// <summary>
    /// <para>按顶层页签创建对应页面控件。</para>
    /// Creates the page control for the given top-level tab.
    /// </summary>
    /// <param name="page"><para>顶层页签标识。</para>The top-level tab identifier.</param>
    /// <returns><para>对应页面控件。</para>The matching page control.</returns>
    public static Control CreateTopLevelPage(TopLevelPage page)
    {
        return _topLevelPages[page]();
    }
}