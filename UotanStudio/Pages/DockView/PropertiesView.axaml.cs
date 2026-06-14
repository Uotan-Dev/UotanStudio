using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System.Collections.Generic;

namespace UotanStudio.Pages.DockView;

public partial class PropertiesView : UserControl
{
    public record PropertyItem(string Name, string Value);
    public PropertiesView()
    {
        InitializeComponent();
        Properties.ItemsSource = new[]
        {
            new PropertyItem("镜像名称", "boot.img"),
            new PropertyItem("镜像大小", "106 MB"),
            new PropertyItem("镜像类型", "Kernel"),
            new PropertyItem("镜像格式", "（EXT4）")
        };
    }
}