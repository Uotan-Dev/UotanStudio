using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UotanStudio.Locale.EnUs;

/// <summary>Trim-safe interaction strings for English.</summary>
public partial class Interaction : ResourceDictionary
{
    public Interaction() => AvaloniaXamlLoader.Load(this);
}