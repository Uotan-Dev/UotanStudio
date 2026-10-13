using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UotanStudio.Themes;

/// <summary>
/// Light theme resource dictionary code-behind. Its x:Class makes the XAML precompile so the
/// runtime instantiates it by type instead of <see cref="ResourceInclude"/>, keeping the app
/// AOT/trimming-safe.
/// <para>浅色主题资源字典的代码后置。x:Class 使 XAML 预编译，运行时按类型实例化而非
/// <see cref="ResourceInclude"/>，保持 AOT/裁剪安全。</para>
/// </summary>
public partial class LightColors : ResourceDictionary
{
    /// <summary>
    /// Creates the light theme dictionary, loading its content from the precompiled XAML.
    /// <para>创建浅色主题字典并从预编译 XAML 填充内容。</para>
    /// </summary>
    public LightColors()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

/// <summary>
/// Dark theme resource dictionary code-behind with AOT-safe typed instantiation.
/// <para>深色主题资源字典的代码后置，AOT 安全的类型实例化。</para>
/// </summary>
public partial class DarkColors : ResourceDictionary
{
    /// <summary>
    /// Creates the dark theme dictionary, loading its content from the precompiled XAML.
    /// <para>创建深色主题字典并从预编译 XAML 填充内容。</para>
    /// </summary>
    public DarkColors()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

/// <summary>
/// Global accent dictionary code-behind: the accent and its derivatives are defined once,
/// merged after <see cref="LightColors"/> and <see cref="DarkColors"/> so the keys override
/// the Semi palette from one place.
/// <para>全局主题色字典的代码后置：强调色与派生色在此定义一次，并在浅色与深色字典之后合并，
/// 同名 Semi 调色板键由这里的值覆盖，换色只改一处。</para>
/// </summary>
public partial class AccentColors : ResourceDictionary
{
    /// <summary>
    /// Creates the accent dictionary, loading its content from the precompiled XAML.
    /// <para>创建主题色字典并从预编译 XAML 填充内容。</para>
    /// </summary>
    public AccentColors()
    {
        AvaloniaXamlLoader.Load(this);
    }
}