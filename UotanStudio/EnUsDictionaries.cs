using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UotanStudio.Locale.EnUs;

/// <summary>
/// <para>英文（美国）通用模块资源字典（<c>Locale/en-US/Common.axaml</c> 的代码后置），
/// AOT 安全的类型实例化。</para>
/// English (US) common-module resource dictionary (code-behind of
/// <c>Locale/en-US/Common.axaml</c>) with AOT-safe typed instantiation.
/// </summary>
public partial class Common : ResourceDictionary
{
    /// <summary>
    /// <para>创建该语言模块字典并从预编译 XAML 填充内容。</para>
    /// Creates the locale module dictionary, loading its content from the precompiled XAML.
    /// </summary>
    public Common() => AvaloniaXamlLoader.Load(this);
}

/// <summary>
/// <para>英文（美国）菜单模块资源字典（<c>Locale/en-US/Menu.axaml</c> 的代码后置）。</para>
/// English (US) menu-module resource dictionary (code-behind of <c>Locale/en-US/Menu.axaml</c>).
/// </summary>
public partial class Menu : ResourceDictionary
{
    /// <inheritdoc cref="Common()"/>
    public Menu() => AvaloniaXamlLoader.Load(this);
}

/// <summary>
/// <para>英文（美国）停靠模块资源字典（<c>Locale/en-US/Dock.axaml</c> 的代码后置）。</para>
/// English (US) dock-module resource dictionary (code-behind of <c>Locale/en-US/Dock.axaml</c>).
/// </summary>
public partial class Dock : ResourceDictionary
{
    /// <inheritdoc cref="Common()"/>
    public Dock() => AvaloniaXamlLoader.Load(this);
}

/// <summary>
/// <para>英文（美国）节点模块资源字典（<c>Locale/en-US/Node.axaml</c> 的代码后置）。</para>
/// English (US) node-module resource dictionary (code-behind of <c>Locale/en-US/Node.axaml</c>).
/// </summary>
public partial class Node : ResourceDictionary
{
    /// <inheritdoc cref="Common()"/>
    public Node() => AvaloniaXamlLoader.Load(this);
}

/// <summary>
/// <para>英文（美国）设置模块资源字典（<c>Locale/en-US/Settings.axaml</c> 的代码后置）。</para>
/// English (US) settings-module resource dictionary (code-behind of <c>Locale/en-US/Settings.axaml</c>).
/// </summary>
public partial class Settings : ResourceDictionary
{
    /// <inheritdoc cref="Common()"/>
    public Settings() => AvaloniaXamlLoader.Load(this);
}

/// <summary>
/// <para>英文（美国）设备模块资源字典（<c>Locale/en-US/Device.axaml</c> 的代码后置）。</para>
/// English (US) device-module resource dictionary (code-behind of <c>Locale/en-US/Device.axaml</c>).
/// </summary>
public partial class Device : ResourceDictionary
{
    /// <inheritdoc cref="Common()"/>
    public Device() => AvaloniaXamlLoader.Load(this);
}
