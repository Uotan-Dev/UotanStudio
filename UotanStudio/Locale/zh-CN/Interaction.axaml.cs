using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UotanStudio.Locale.ZhCn;

/// <summary>Trim-safe interaction strings for Simplified Chinese.</summary>
public partial class Interaction : ResourceDictionary
{
    public Interaction() => AvaloniaXamlLoader.Load(this);
}