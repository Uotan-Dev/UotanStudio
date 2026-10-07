using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;

namespace UotanStudio.Views.Nodes;

/// <summary>Reviews automatically discovered interfaces before changing the graph.</summary>
internal sealed class CompositeInterfaceWindow : Window
{
    private readonly TextBox _name;
    private readonly List<PortRow> _inputs = [];
    private readonly List<PortRow> _outputs = [];

    public CompositeInterfaceWindow(CompositeBoundaryAnalysis analysis, string? name = null)
    {
        Title = Text("Node_Encapsulate");
        Width = 760;
        Height = 600;
        MinWidth = 500;
        MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _name = new TextBox { Text = name ?? Text("Node_DefaultCompositeName") };
        var content = new StackPanel { Spacing = 10, Margin = new Thickness(16) };
        content.Children.Add(_name);
        content.Children.Add(new TextBlock { Text = Text("Node_InterfaceHint"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        AddSection(content, "Node_InterfaceInputs", analysis.Inputs, analysis.InputCandidates, _inputs, true);
        AddSection(content, "Node_InterfaceOutputs", analysis.Outputs, analysis.OutputCandidates, _outputs, false);
        var apply = new Button { Content = Text("Node_Encapsulate"), HorizontalAlignment = HorizontalAlignment.Right };
        apply.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text) || _inputs.Concat(_outputs).Any(row => row.Included && string.IsNullOrWhiteSpace(row.Name.Text)))
            {
                return;
            }
            Close(true);
        };
        content.Children.Add(apply);
        Content = new ScrollViewer { Content = content };
    }

    public string CompositeName => _name.Text!.Trim();

    public CompositeBoundaryAnalysis GetBoundary()
    {
        return new CompositeBoundaryAnalysis(Collect(_inputs), Collect(_outputs), [], []);
    }

    private static IReadOnlyList<VisualPluginPortDefinition> Collect(List<PortRow> rows)
    {
        return [.. rows.Where(row => row.Included).OrderBy(row => row.Order.Value).Select(row =>
        {
            row.Definition.Name = row.Name.Text!.Trim();
            row.Definition.Required = row.Required.IsChecked == true;
            row.Definition.DefaultValue = row.Definition.IsSensitive || string.IsNullOrEmpty(row.Default.Text) ? null : row.Default.Text;
            return row.Definition;
        })];
    }

    private static void AddSection(StackPanel content, string title, IReadOnlyList<VisualPluginPortDefinition> discovered,
        IReadOnlyList<VisualPluginPortDefinition> candidates, List<PortRow> rows, bool input)
    {
        content.Children.Add(new TextBlock { Text = Text(title), FontWeight = Avalonia.Media.FontWeight.Bold });
        foreach (var definition in discovered.Concat(candidates))
        {
            var mandatory = discovered.Contains(definition);
            var include = new CheckBox { IsChecked = mandatory, IsEnabled = !mandatory };
            var name = new TextBox { Text = definition.Name, MinWidth = 120 };
            var order = new NumericUpDown { Value = rows.Count, Minimum = 0, Maximum = 4096, Width = 68 };
            var required = new CheckBox { Content = Text("Node_InterfaceRequired"), IsChecked = definition.Required, IsVisible = input };
            var value = new TextBox { Text = definition.IsSensitive ? null : definition.DefaultValue, PlaceholderText = Text("Node_InterfaceDefault"), IsVisible = input && !definition.IsSensitive, MinWidth = 100 };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,*"), ColumnSpacing = 8 };
            Control[] controls = [include, order, name, required, value];
            for (var i = 0; i < controls.Length; i++)
            {
                Grid.SetColumn(controls[i], i);
                row.Children.Add(controls[i]);
            }
            ToolTip.SetTip(name, definition.PortType + " · " + definition.Id);
            ToolTip.SetTip(order, Text("Node_InterfaceOrder"));
            rows.Add(new PortRow(definition, include, name, order, required, value));
            content.Children.Add(row);
        }
    }

    private static string Text(string key) => LanguageService.Instance.GetString(key);

    private sealed record PortRow(VisualPluginPortDefinition Definition, CheckBox Include, TextBox Name,
        NumericUpDown Order, CheckBox Required, TextBox Default)
    {
        public bool Included => Include.IsChecked == true;
    }
}