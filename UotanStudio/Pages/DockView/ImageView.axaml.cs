using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System.Collections.Generic;

namespace UotanStudio.Pages.DockView;

public partial class ImageView : UserControl
{
    public ImageView()
    {
        InitializeComponent();
        ImagesList.ItemsSource = new List<string> { "boot.img", "system.img", "super.img", "dtb.img", "payload.bin", };
    }
}