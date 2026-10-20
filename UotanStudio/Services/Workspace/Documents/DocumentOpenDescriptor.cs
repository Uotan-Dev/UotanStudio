using UotanStudio.ViewModels;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;

namespace UotanStudio.Services.Workspace.Documents;

/// <summary>
/// Registration for one document kind: the session Kind string, the explicit open mode, menu
/// metadata, the routing probe, the open factory, the dedup key and the cleanup hook.
/// <para>单一文档种类的登记项：承载会话 Kind、显式打开模式、菜单元数据、路由谓词、打开工厂、去重键与清理钩子。</para>
/// </summary>
internal sealed record DocumentOpenDescriptor
{
    /// <summary>The kind string persisted in session snapshots (verbatim match with the DTO). <para>会话快照使用的种类字符串（与持久化 DTO 的 Kind 逐字一致）。</para></summary>
    /// <value>The kind this descriptor is registered under. <para>本描述符登记所用的种类字符串。</para></value>
    required public string Kind { get; init; }

    /// <summary>The explicit open mode owned by this descriptor; null for compare/mirror-style documents. <para>该描述符独占的显式打开模式；对比/投屏类无对应模式时为 null。</para></summary>
    /// <value>The explicit mode to open with, or null. <para>显式打开方式，或为 null。</para></value>
    public EditorOpenMode? Mode { get; init; }

    /// <summary>The document type used for type-keyed lookup and session capture mapping. <para>文档类型，用于按类型查找与会话捕获映射。</para></summary>
    /// <value>The type registered for this document kind. <para>本文档种类登记的类型。</para></value>
    required public Type ViewModelType { get; init; }

    /// <summary>The locale resource key for the "Open As" menu entry; empty when not menu-listed. <para>"打开方式"菜单项的资源键；不进入菜单时为空。</para></summary>
    /// <value>The locale key for the menu entry, or an empty string. <para>菜单项的资源键，或为空字符串。</para></value>
    public string LocaleKey { get; init; } = string.Empty;

    /// <summary>The menu order (0 is reserved for the Auto pseudo entry); negative means not menu-listed. <para>菜单顺序（0 保留给 Auto 伪项）；负值表示不进入菜单。</para></summary>
    /// <value>The ascending menu position, or a negative value. <para>升序菜单位置，或为负值。</para></value>
    public int MenuOrder { get; init; } = -1;

    /// <summary>The semantic Iconica icon for the menu entry; null when the entry is iconless. <para>菜单项的 Iconica 语义图标；无图标时为 null。</para></summary>
    /// <value>The icon identifier, or null when iconless. <para>图标标识，无图标时为 null。</para></value>
    public AppIconKind? IconKind { get; init; }

    /// <summary>The automatic-mode routing probe (extension/magic), populated during format consolidation. <para>自动模式路由谓词（扩展名/魔数），在格式收敛阶段填充。</para></summary>
    /// <value>The predicate consulted in automatic mode, or null. <para>自动模式下询问的谓词，或为 null。</para></value>
    public Func<DocumentOpenRequest, bool>? Probe { get; init; }

    /// <summary>The format-handler names associated with automatic routing. <para>自动路由关联的格式处理器名称集合。</para></summary>
    /// <value>The associated handler names, or null. <para>关联的处理器名称集合，或为 null。</para></value>
    public IReadOnlyList<string>? RequiredFormatNames { get; init; }

    /// <summary>The open entry point: creates and loads the document from the unified request against a workspace instance. <para>打开入口：在工作区实例上按统一请求创建并载入文档。</para></summary>
    /// <value>The factory taking a workspace and a request and returning the load task. <para>接收工作区与请求、返回载入任务的工厂。</para></value>
    public Func<WorkspaceViewModel, DocumentOpenRequest, Task>? Open { get; init; }

    /// <summary>The dedup key (file path) for an opened document. <para>已打开文档的去重键（文件路径）。</para></summary>
    /// <value>The function returning a document's dedup key, or null. <para>返回文档去重键的函数，或为 null。</para></value>
    public Func<object, string?>? GetPath { get; init; }

    /// <summary>The cleanup hook run when the document leaves the layout. <para>文档离开布局时运行的清理钩子。</para></summary>
    /// <value>The action called with the document instance, or null. <para>以文档实例调用的操作，或为 null。</para></value>
    public Action<object>? Cleanup { get; init; }

    /// <summary>Whether the document participates in session capture. <para>是否计入会话文档捕获。</para></summary>
    /// <value>True when an instance is captured as a session document. <para>实例作为会话文档被捕获时为真。</para></value>
    public bool IsDocument { get; init; } = true;

    /// <summary>Whether at most one instance of this document kind exists. <para>同类文档是否全局单实例。</para></summary>
    /// <value>True when a single instance is enforced. <para>强制单实例时为真。</para></value>
    public bool Singleton { get; init; }
}