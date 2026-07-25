namespace UotanStudio.Abstractions.Plugin;

/// <summary>
/// <para>插件元数据快照，用于插件管理器 UI 展示，避免暴露插件实例本身。</para>
/// Plugin metadata snapshot, used by the plugin manager UI to avoid exposing the plugin instance itself.
/// </summary>
public sealed class PluginInfo
{
    /// <summary>
    /// <para>插件唯一标识符。</para>
    /// Unique plugin identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// <para>显示名称。</para>
    /// Display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// <para>版本号。</para>
    /// Version.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// <para>作者。</para>
    /// Author.
    /// </summary>
    public required string Author { get; init; }

    /// <summary>
    /// <para>描述。</para>
    /// Description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// <para>是否为内置插件。</para>
    /// Whether this is a built-in plugin.
    /// </summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>
    /// <para>是否已成功加载。</para>
    /// Whether the plugin has been successfully loaded.
    /// </summary>
    public bool IsLoaded { get; set; }

    /// <summary>
    /// <para>加载失败时的错误信息。</para>
    /// Error message when loading fails.
    /// </summary>
    public string? LoadError { get; set; }
}
