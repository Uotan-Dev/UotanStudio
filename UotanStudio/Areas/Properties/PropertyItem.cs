namespace UotanStudio.Areas.Properties;

/// <summary>
/// <para>属性面板中的单个键值对条目。</para>
/// A single key/value entry shown in the properties panel.
/// </summary>
/// <param name="key"><para>属性名。</para>The property name.</param>
/// <param name="value"><para>属性值。</para>The property value.</param>
public sealed record PropertyItem(string Key, string Value);