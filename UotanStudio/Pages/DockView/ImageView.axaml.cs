using System.Collections.Generic;
using Avalonia.Controls;

namespace UotanStudio.Pages.DockView;

public partial class ImageView : UserControl
{
    public ImageView()
    {
        InitializeComponent();
        ImagesList.ItemsSource = new List<string> { "boot.img", "system.img", "super.img", "dtb.img", "payload.bin", };
    }
}