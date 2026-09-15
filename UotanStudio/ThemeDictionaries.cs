using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UotanStudio.Themes;

/// <summary>
/// <para>浅色主题资源字典（<c>Themes/LightColors.axaml</c> 的代码后置）。
/// 通过 <c>x:Class</c> 让 Avalonia 编译器生成预编译 XAML，运行时以类型实例化
/// （<c>new LightColors()</c>）替代 <see cref="ResourceInclude"/>，保证 AOT/裁剪安全。</para>
/// Light theme resource dictionary (code-behind of <c>Themes/LightColors.axaml</c>).
/// The <c>x:Class</c> makes the Avalonia compiler emit precompiled XAML so the runtime can
/// instantiate it by type (<c>new LightColors()</c>) instead of <see cref="ResourceInclude"/>,
/// keeping the app AOT/trimming-safe.
/// </summary>
public partial class LightColors : ResourceDictionary
{
    /// <summary>
    /// <para>创建浅色主题字典并从预编译 XAML 填充内容。</para>
    /// Creates the light theme dictionary, loading its content from the precompiled XAML.
    /// </summary>
    public LightColors()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

/// <summary>
/// <para>深色主题资源字典（<c>Themes/DarkColors.axaml</c> 的代码后置），AOT 安全的类型实例化。</para>
/// Dark theme resource dictionary (code-behind of <c>Themes/DarkColors.axaml</c>) with
/// AOT-safe typed instantiation.
/// </summary>
public partial class DarkColors : ResourceDictionary
{
    /// <summary>
    /// <para>创建深色主题字典并从预编译 XAML 填充内容。</para>
    /// Creates the dark theme dictionary, loading its content from the precompiled XAML.
    /// </summary>
    public DarkColors()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

/// <summary>
/// <para>全局主题色资源字典（<c>Themes/AccentColors.axaml</c> 的代码后置）：强调色及其派生色只在这里定义一次，
/// 整档在 <c>App.axaml</c> 中排在 <see cref="LightColors"/> / <see cref="DarkColors"/> 之后合并，
/// 因此同名的 Semi 调色板键会被这里的值覆盖，换色只需改这一个文件。</para>
/// Global accent dictionary (code-behind of <c>Themes/AccentColors.axaml</c>): the accent and its derivatives
/// are defined once here and the dictionary is merged after <see cref="LightColors"/> / <see cref="DarkColors"/>
/// in <c>App.axaml</c>, so its keys override the Semi palette and re-skinning touches a single file.
/// </summary>
public partial class AccentColors : ResourceDictionary
{
    /// <summary>
    /// <para>创建主题色字典并从预编译 XAML 填充内容。</para>
    /// Creates the accent dictionary, loading its content from the precompiled XAML.
    /// </summary>
    public AccentColors()
    {
        AvaloniaXamlLoader.Load(this);
    }
}