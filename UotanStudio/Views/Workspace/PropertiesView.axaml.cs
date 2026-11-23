using Avalonia;
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
        UpdateMirrorServerButton.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                UpdateMirrorActionBarLayout(MirrorActionBar.Bounds.Width);
            }
        };
    }

    private void OnPropertiesGridSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && double.IsFinite(e.NewSize.Width) && e.NewSize.Width > 0 &&
            sender is DataGrid { Columns.Count: > 0 } grid)
        {

            grid.Columns[0].MaxWidth = Math.Max(1, e.NewSize.Width / 2);
        }
    }

    private void OnMirrorActionBarSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && double.IsFinite(e.NewSize.Width) && e.NewSize.Width > 0)
        {
            UpdateMirrorActionBarLayout(e.NewSize.Width);
        }
    }

    private void UpdateMirrorActionBarLayout(double availableWidth)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return;
        }

        var singleRow = false;
        if (UpdateMirrorServerButton.IsVisible)
        {
            var unconstrained = new Size(double.PositiveInfinity, double.PositiveInfinity);
            ApplyMirrorSettingsButton.Measure(unconstrained);
            UpdateMirrorServerButton.Measure(unconstrained);
            var minColumnWidth = Math.Max(
                ApplyMirrorSettingsButton.DesiredSize.Width,
                UpdateMirrorServerButton.DesiredSize.Width);
            singleRow = availableWidth >= minColumnWidth * 2 + MirrorActionBar.ColumnSpacing;
        }

        if (singleRow)
        {
            Grid.SetRow(ApplyMirrorSettingsButton, 0);
            Grid.SetColumn(ApplyMirrorSettingsButton, 0);
            Grid.SetColumnSpan(ApplyMirrorSettingsButton, 1);

            Grid.SetRow(UpdateMirrorServerButton, 0);
            Grid.SetColumn(UpdateMirrorServerButton, 1);
            Grid.SetColumnSpan(UpdateMirrorServerButton, 1);
        }
        else
        {
            Grid.SetRow(ApplyMirrorSettingsButton, 0);
            Grid.SetColumn(ApplyMirrorSettingsButton, 0);
            Grid.SetColumnSpan(ApplyMirrorSettingsButton, 2);

            Grid.SetRow(UpdateMirrorServerButton, 1);
            Grid.SetColumn(UpdateMirrorServerButton, 0);
            Grid.SetColumnSpan(UpdateMirrorServerButton, 2);
        }
    }
}