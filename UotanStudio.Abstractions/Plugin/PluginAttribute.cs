using System;

namespace UotanStudio.Abstractions.Plugin;

/// <summary>
/// <para>标记一个类为插件实现。源生成器会扫描此特性并生成编译期注册代码，AOT 安全。</para>
/// Marks a class as a plugin implementation. A source generator scans this attribute and emits compile-time registration code, AOT-safe.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PluginAttribute : Attribute
{
    /// <summary>
    /// <para>插件唯一标识符。</para>
    /// Unique plugin identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// <para>显示名称（可选，缺省时取类名）。</para>
    /// Display name (optional; defaults to the class name).
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// <para>版本号（可选，缺省为 <c>0.0.0</c>）。</para>
    /// Version (optional; defaults to <c>0.0.0</c>).
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// <para>作者（可选）。</para>
    /// Author (optional).
    /// </summary>
    public string? Author { get; set; }

    /// <summary>
    /// <para>描述（可选）。</para>
    /// Description (optional).
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// <para>创建插件特性。</para>
    /// Creates the plugin attribute.
    /// </summary>
    /// <param name="id">
    /// <para>插件唯一标识符（反向域名建议）。</para>
    /// Unique plugin identifier (reverse-DNS recommended).
    /// </param>
    public PluginAttribute(string id)
    {
        Id = id;
    }
}
