using Avalonia.Controls;

namespace UotanStudio.Pages.DockView;

public partial class PropertiesView : UserControl
{
    /// <summary>
    /// <para>构属性视图，XAML 中已绑定 <see cref="ViewModels.PropertiesViewModel.Properties"/> 集合。</para>
    /// Constructs the properties view; the XAML binds the <see cref="ViewModels.PropertiesViewModel.Properties"/> collection.
    /// </summary>
    public PropertiesView()
    {
        InitializeComponent();
    }
}