using Avalonia.Controls;
using UotanStudio.Services;

namespace UotanStudio.Pages.DockView;

public partial class PropertiesView : UserControl
{
    public record PropertyItem(string Name, string Value);
    public PropertiesView()
    {
        InitializeComponent();
        Properties.ItemsSource = new[]
        {
            new PropertyItem(LanguageService.Instance.GetString("Dock_Properties_ImageName"), "boot.img"),
            new PropertyItem(LanguageService.Instance.GetString("Dock_Properties_ImageSize"), "106 MB"),
            new PropertyItem(LanguageService.Instance.GetString("Dock_Properties_ImageType"), "Kernel"),
            new PropertyItem(LanguageService.Instance.GetString("Dock_Properties_ImageFormat"), "（EXT4）")
        };
    }
}