using Avalonia.Controls;

namespace UotanStudio.Views.Workspace;

public partial class PropertiesView : UserControl
{
    /// <summary>
    /// Constructs the properties view; the XAML binds the <see cref="ViewModels.PropertiesViewModel.Properties"/> collection.
    /// <para>构属性视图，XAML 中已绑定 <see cref="ViewModels.PropertiesViewModel.Properties"/> 集合。</para>
    /// </summary>
    public PropertiesView()
    {
        InitializeComponent();
    }

    private void OnPropertiesGridSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && double.IsFinite(e.NewSize.Width) && e.NewSize.Width > 0 &&
            sender is DataGrid { Columns.Count: > 0 } grid)
        {

            grid.Columns[0].MaxWidth = Math.Max(1, e.NewSize.Width / 2);
        }
    }
}