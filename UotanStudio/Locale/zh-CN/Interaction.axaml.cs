using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UotanStudio.Locale.ZhCn;

/// <summary>
/// Simplified Chinese resource dictionary of interaction and validation messages, typed for
/// trimming safety.
/// <para>简体中文的交互与校验消息资源字典，以类型化实例方式保证裁剪安全。</para>
/// </summary>
public partial class Interaction : ResourceDictionary
{
    public Interaction() => AvaloniaXamlLoader.Load(this);
}