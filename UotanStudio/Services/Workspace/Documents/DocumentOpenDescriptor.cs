using UotanStudio.Views.Controls;

namespace UotanStudio.Services.Workspace.Documents;

/// <summary>
/// <para>文档编辑器描述符：一个编辑器种类的开发现场唯一登记项，
/// 承载会话 Kind、显式打开模式、菜单元数据、路由谓词、工厂、去重键与清理钩子。</para>
/// Document-editor descriptor: the single registration per editor kind, carrying the session
/// Kind string, the explicit open mode, context-menu metadata, the routing probe, document
/// factories, the dedup key and the cleanup hook.
/// </summary>
internal sealed record DocumentOpenDescriptor
{
    /// <summary><para>会话快照使用的种类字符串（与持久化 DTO 的 Kind 逐字一致）。</para>The kind string persisted in session snapshots (verbatim match with the DTO).</summary>
    required public string Kind { get; init; }

    /// <summary><para>该描述符独占的显式打开模式；对比/投屏类无对应模式时为 null。</para>The explicit open mode owned by this descriptor; null for compare/mirror-style documents.</summary>
    public EditorOpenMode? Mode { get; init; }

    /// <summary><para>文档 ViewModel 类型，用于按类型查找与捕获映射。</para>The document ViewModel type used for type-keyed lookup and session capture mapping.</summary>
    required public Type ViewModelType { get; init; }

    /// <summary><para>"打开方式"菜单项的资源键；不进入菜单时为空。</para>The locale resource key for the "Open As" menu entry; empty when not menu-listed.</summary>
    public string LocaleKey { get; init; } = string.Empty;

    /// <summary><para>菜单顺序（0 保留给 Auto 伪项）；负值表示不进入菜单。</para>The menu order (0 is reserved for the Auto pseudo entry); negative means not menu-listed.</summary>
    public int MenuOrder { get; init; } = -1;

    /// <summary><para>菜单图标的 MaterialIconKind 名称；无图标时为 null。</para>The MaterialIconKind name for the menu icon; null when iconless.</summary>
    public string? IconKind { get; init; }

    /// <summary><para>自动模式路由谓词（扩展名/魔数），在格式收敛阶段填充。</para>The automatic-mode routing probe (extension/magic), populated during format consolidation.</summary>
    public Func<DocumentOpenRequest, bool>? Probe { get; init; }

    /// <summary><para>自动路由关联的格式处理器名称集合。</para>The format-handler names associated with automatic routing.</summary>
    public IReadOnlyList<string>? RequiredFormatNames { get; init; }

    /// <summary><para>文档工厂：按统一请求创建并加载 ViewModel，在派发收敛阶段填充。</para>The document factory: creates and loads the ViewModel from the unified request, populated during dispatch consolidation.</summary>
    public Func<DocumentOpenRequest, Task<object?>>? Factory { get; init; }

    /// <summary><para>已打开文档的去重键（文件路径）。</para>The dedup key (file path) for an opened document.</summary>
    public Func<object, string?>? GetPath { get; init; }

    /// <summary><para>文档从布局移除时的清理钩子，语义与原 SafeRemoveDockable 链一致。</para>The cleanup hook run when the document leaves the layout, matching the original SafeRemoveDockable chain semantics.</summary>
    public Action<object>? Cleanup { get; init; }

    /// <summary><para>是否计入会话文档快照（与现 WorkspaceSessionCapture 口径一致）。</para>Whether the document participates in session capture (aligned with the current WorkspaceSessionCapture scope).</summary>
    public bool IsDocument { get; init; } = true;

    /// <summary><para>同类文档是否全局单实例。</para>Whether at most one instance of this document kind exists.</summary>
    public bool Singleton { get; init; }
}
