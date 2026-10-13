using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UotanStudio.Locale.ZhCn;

/// <summary>
/// Simplified-Chinese common-module resource dictionary (code-behind of
/// <c>Locale/zh-CN/Common.axaml</c>) with AOT-safe typed instantiation.
/// <para>简体中文通用模块资源字典（<c>Locale/zh-CN/Common.axaml</c> 的代码后置），
/// AOT 安全的类型实例化。</para>
/// </summary>
public partial class Common : ResourceDictionary
{
    /// <summary>
    /// Creates the locale module dictionary, loading its content from the precompiled XAML.
    /// <para>创建该语言模块字典并从预编译 XAML 填充内容。</para>
    /// </summary>
    public Common() => AvaloniaXamlLoader.Load(this);
}

/// <summary>
/// Simplified-Chinese menu-module resource dictionary (code-behind of <c>Locale/zh-CN/Menu.axaml</c>).
/// <para>简体中文菜单模块资源字典（<c>Locale/zh-CN/Menu.axaml</c> 的代码后置）。</para>
/// </summary>
public partial class Menu : ResourceDictionary
{
    /// <inheritdoc cref="Common()"/>
    public Menu() => AvaloniaXamlLoader.Load(this);
}

/// <summary>
/// Simplified-Chinese dock-module resource dictionary (code-behind of <c>Locale/zh-CN/Dock.axaml</c>).
/// <para>简体中文停靠模块资源字典（<c>Locale/zh-CN/Dock.axaml</c> 的代码后置）。</para>
/// </summary>
public partial class Dock : ResourceDictionary
{
    /// <inheritdoc cref="Common()"/>
    public Dock() => AvaloniaXamlLoader.Load(this);
}

/// <summary>
/// Simplified-Chinese node-module resource dictionary (code-behind of <c>Locale/zh-CN/Node.axaml</c>).
/// <para>简体中文节点模块资源字典（<c>Locale/zh-CN/Node.axaml</c> 的代码后置）。</para>
/// </summary>
public partial class Node : ResourceDictionary
{
    /// <inheritdoc cref="Common()"/>
    public Node() => AvaloniaXamlLoader.Load(this);
}

/// <summary>
/// Simplified-Chinese settings-module resource dictionary (code-behind of <c>Locale/zh-CN/Settings.axaml</c>).
/// <para>简体中文设置模块资源字典（<c>Locale/zh-CN/Settings.axaml</c> 的代码后置）。</para>
/// </summary>
public partial class Settings : ResourceDictionary
{
    /// <inheritdoc cref="Common()"/>
    public Settings() => AvaloniaXamlLoader.Load(this);
}

/// <summary>
/// Simplified-Chinese device-module resource dictionary (code-behind of <c>Locale/zh-CN/Device.axaml</c>).
/// <para>简体中文设备模块资源字典（<c>Locale/zh-CN/Device.axaml</c> 的代码后置）。</para>
/// </summary>
public partial class Device : ResourceDictionary
{
    /// <inheritdoc cref="Common()"/>
    public Device() => AvaloniaXamlLoader.Load(this);
}