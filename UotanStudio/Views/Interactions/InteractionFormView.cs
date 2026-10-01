using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;
using UotanStudio.Projects.Interactions;
using UotanStudio.Services;
using UotanStudio.Services.Interactions;

namespace UotanStudio.Views.Interactions;

/// <summary>Renders a bounded declarative form in either a dialog or an embedded host.</summary>
public sealed class InteractionFormView : UserControl
{
    private readonly InteractionFormState _state;
    private readonly Dictionary<string, Control> _controls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _errors = new(StringComparer.Ordinal);
    private readonly Button _submit;
    private long _decodedImagePixels;

    public InteractionFormView(InteractionFormState state)
    {
        _state = state;
        var body = Build(state.Form.Content);
        _submit = new Button { Content = state.Form.SubmitLabel ?? Text("Submit") };
        var cancel = new Button { Content = state.Form.CancelLabel ?? Text("Cancel") };
        _submit.Click += (_, _) => Submit();
        cancel.Click += (_, _) => Completed?.Invoke(this, new InteractionResult { Outcome = InteractionOutcome.Cancelled });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(cancel);
        actions.Children.Add(_submit);
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(16), RowSpacing = 12 };
        layout.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid.SetRow(actions, 1);
        layout.Children.Add(actions);
        Content = layout;
        _state.Changed += OnChanged;
        Refresh();
    }

    public event EventHandler<InteractionResult>? Completed;

    private void Submit()
    {
        InteractionResult result;
        try
        {

            result = _state.Submit();
        }
        catch (InvalidOperationException)
        {
            Refresh();
            return;
        }
        Completed?.Invoke(this, result);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _state.Changed -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _state.Changed -= OnChanged;
        _state.Changed += OnChanged;
        base.OnAttachedToVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var errors = _state.Validate();
        foreach (var element in Flatten(_state.Form.Content))
        {
            _controls[element.Id].IsVisible = _state.IsVisible(element);
            _controls[element.Id].IsEnabled = _state.IsEnabled(element);
            if (_errors.TryGetValue(element.Id, out var error))
            {
                error.Text = errors.TryGetValue(element.Id, out var key) ? Text("Error_" + key) : string.Empty;
                error.IsVisible = !string.IsNullOrEmpty(error.Text);
            }
        }
        _submit.IsEnabled = errors.Count == 0;
    }

    private Control Build(InteractionElement element)
    {
        Control control;
        if (InteractionFormState.IsField(element))
        {
            var container = new StackPanel { Spacing = 4 };
            container.Children.Add(new TextBlock { Text = element.Label, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(element.Description))
            {
                container.Children.Add(new TextBlock { Text = element.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            }
            container.Children.Add(BuildField(element));
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            error.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemControlErrorTextForegroundBrush"));
            _errors[element.Id] = error;
            container.Children.Add(error);
            control = container;
        }
        else if (element.Kind == InteractionElementKind.Divider)
        {
            control = new Separator();
        }
        else if (element.Kind == InteractionElementKind.Text)
        {
            control = new TextBlock { Text = element.Label, TextWrapping = TextWrapping.Wrap };
        }
        else if (element.Kind == InteractionElementKind.Image)
        {
            var data = element.ImageData ?? throw new InvalidDataException("Missing image data.");
            using var header = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(header) ?? throw new InvalidDataException("Unsupported image data.");
            var pixels = (long)codec.Info.Width * codec.Info.Height;
            _decodedImagePixels += pixels;
            if (codec.Info.Width is <= 0 or > 4096 || codec.Info.Height is <= 0 or > 4096 || pixels > 4_194_304 || _decodedImagePixels > 16_777_216)
            {
                throw new InvalidDataException("Interaction images exceed the decoded size limit.");
            }
            using var encoded = new MemoryStream(data, false);
            var bitmap = Bitmap.DecodeToWidth(encoded, 1024);
            var image = new Image { Source = bitmap, MaxWidth = 1024, MaxHeight = 512, Stretch = Stretch.Uniform };
            image.DetachedFromVisualTree += (_, _) => bitmap.Dispose();
            control = image;
        }
        else if (element.Kind == InteractionElementKind.Pages)
        {
            control = new TabControl
            {
                ItemsSource = element.Children.Select(child => new TabItem { Header = child.Label, Content = Build(child) }).ToArray()
            };
        }
        else
        {
            var panel = new StackPanel { Spacing = 12, Orientation = element.Kind == InteractionElementKind.Row ? Orientation.Horizontal : Orientation.Vertical };
            foreach (var child in element.Children)
            {
                panel.Children.Add(Build(child));
            }
            control = element.Kind == InteractionElementKind.Group ? new Expander { Header = element.Label, Content = panel, IsExpanded = true } : panel;
        }
        _controls[element.Id] = control;
        return control;
    }

    private Control BuildField(InteractionElement field)
    {
        var initial = _state.GetValue(field.Id);
        if (field.Kind == InteractionElementKind.BooleanField)
        {
            var check = new CheckBox { IsChecked = initial.Boolean ?? false };
            check.IsCheckedChanged += (_, _) => _state.SetValue(field.Id, new InteractionValue { Boolean = check.IsChecked == true });
            return check;
        }
        if (field.Kind == InteractionElementKind.ChoiceField)
        {
            var combo = new ComboBox { ItemsSource = field.Options, SelectedItem = initial.Text, HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.SelectionChanged += (_, _) => _state.SetValue(field.Id, new InteractionValue { Text = combo.SelectedItem as string });
            return combo;
        }
        if (field.Kind == InteractionElementKind.MultiChoiceField)
        {
            var panel = new StackPanel { Spacing = 4 };
            var checks = field.Options.Select(option => new CheckBox { Content = option, IsChecked = initial.Selection?.Contains(option, StringComparer.Ordinal) == true }).ToArray();
            foreach (var check in checks)
            {
                check.IsCheckedChanged += (_, _) => _state.SetValue(field.Id, new InteractionValue { Selection = checks.Where(item => item.IsChecked == true).Select(item => (string)item.Content!).ToArray() });
                panel.Children.Add(check);
            }
            return panel;
        }
        var box = new TextBox
        {
            Text = initial.Number?.ToString(CultureInfo.InvariantCulture) ?? initial.Text,
            MaxLength = field.MaxLength,
            PasswordChar = field.Sensitive ? '●' : '\0'
        };
        box.TextChanged += (_, _) => _state.SetValue(field.Id, field.Kind == InteractionElementKind.NumberField
            ? decimal.TryParse(box.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? new InteractionValue { Number = number } : new InteractionValue { Text = box.Text }
            : new InteractionValue { Text = box.Text });
        if (field.Kind is not (InteractionElementKind.FileField or InteractionElementKind.DirectoryField or InteractionElementKind.SavePathField))
        {
            return box;
        }
        var browse = new Button { Content = Text("Browse") };
        browse.Click += async (_, _) =>
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                return;
            }
            IStorageItem? selected = field.Kind switch
            {
                InteractionElementKind.DirectoryField => (await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = field.Label, AllowMultiple = false })).FirstOrDefault(),
                InteractionElementKind.SavePathField => await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = field.Label }),
                _ => (await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = field.Label, AllowMultiple = false })).FirstOrDefault()
            };
            using (selected)
            {
                if (selected?.TryGetLocalPath() is { } path)
                {
                    box.Text = path;
                }
            }
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(box);
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        return row;
    }

    private static IEnumerable<InteractionElement> Flatten(InteractionElement root)
    {
        yield return root;
        foreach (var child in root.Children.SelectMany(Flatten))
        {
            yield return child;
        }
    }

    private static string Text(string key) => LanguageService.Instance.GetString("Interaction_" + key);
}