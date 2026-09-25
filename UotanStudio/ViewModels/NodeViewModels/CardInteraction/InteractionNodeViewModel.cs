using System.Globalization;
using UotanStudio.Projects.Interactions;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Interactions;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>Composable UI definitions and an explicit interaction effect boundary.</summary>
public sealed class InteractionNodeViewModel : FlowNodeViewModel
{
    private readonly Dictionary<string, InteractionFieldSchema> _fieldSchema = new(StringComparer.Ordinal);
    public string? SchemaError { get; private set; }

    public void ApplyFieldSchema(IReadOnlyList<InteractionFieldSchema> fields, bool complete, IReadOnlyList<NodeConnectionViewModel> connections)
    {
        SchemaError = null;
        if (fields.Select(field => field.Id).Distinct(StringComparer.Ordinal).Count() != fields.Count)
        {
            SchemaError = "The form contains duplicate field IDs.";
            return;
        }
        var incoming = fields.ToDictionary(field => field.Id, StringComparer.Ordinal);
        foreach (var port in Output.Where(port => port.PortId.StartsWith("field.", StringComparison.Ordinal)).ToArray())
        {
            var id = port.PortId[6..];
            if (!complete || !incoming.TryGetValue(id, out var field) || field.Type != port.PortType)
            {
                if (connections.Any(connection => ReferenceEquals(connection.Source, port)))
                {
                    SchemaError = "A connected form field has changed or its schema is no longer statically known.";
                }
                // Keep the endpoint so an interface change cannot silently delete a user's connection.
                continue;
            }
        }
        if (!complete)
        {
            return;
        }
        _fieldSchema.Clear();
        foreach (var field in fields)
        {
            _fieldSchema[field.Id] = field;
            var existing = Output.FirstOrDefault(port => port.PortId == "field." + field.Id);
            if (existing is null)
            {
                Output.Add(new NodeConnectorViewModel { PortId = "field." + field.Id, Title = "Field: " + field.Id, DisplayName = field.Label, PortType = field.Type, IsSensitive = field.Sensitive });
            }
            else if (!connections.Any(connection => ReferenceEquals(connection.Source, existing)))
            {
                existing.PortType = field.Type;
            }
            if (existing is not null)
            {
                existing.DisplayName = field.Label;
                existing.IsSensitive = field.Sensitive;
                existing.RefreshLocalization();
            }
        }
    }
    public static IReadOnlyList<string> TypeKeys { get; } =
    [
        "UI.Content.Text", "UI.Content.Divider", "UI.Content.Image",
        "UI.Field.Text", "UI.Field.Number", "UI.Field.Boolean", "UI.Field.Choice", "UI.Field.MultiChoice",
        "UI.Field.File", "UI.Field.Directory", "UI.Field.SavePath",
        "UI.Layout.Row", "UI.Layout.Column", "UI.Layout.Group", "UI.Layout.Pages",
        "UI.Condition.Equals", "UI.Form.Create", "UI.Dialog.Show", "UI.Result.Read",
        "UI.Dialog.Confirm", "UI.Notification.Show", "UI.Progress.Open", "UI.Progress.Update", "UI.Progress.Close"
    ];

    private InteractionNodeViewModel(string typeKey) : base(typeKey, NodeCategory.Output)
    {
        TypeKey = typeKey;
    }

    public static InteractionNodeViewModel Create(string typeKey)
    {
        if (!TypeKeys.Contains(typeKey, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(typeKey));
        }
        var node = new InteractionNodeViewModel(typeKey);
        if (typeKey.StartsWith("UI.Field.", StringComparison.Ordinal))
        {
            node.In("Field ID", NodePortType.Text, Guid.NewGuid().ToString("N"));
            node.In("Label", NodePortType.Text, string.Empty);
            node.In("Description", NodePortType.Text, string.Empty);
            node.In("Required", NodePortType.Boolean, false);
            node.In("Max Length", NodePortType.Number, 4096d);
            var kind = ParseFieldKind(typeKey);
            node.In("Default", kind == InteractionElementKind.NumberField ? NodePortType.Number : kind == InteractionElementKind.BooleanField ? NodePortType.Boolean : NodePortType.Text,
                kind == InteractionElementKind.BooleanField ? false : string.Empty);
            if (kind is InteractionElementKind.ChoiceField or InteractionElementKind.MultiChoiceField)
            {
                node.In("Options", NodePortType.Text, string.Empty, multiline: true);
            }
            if (kind == InteractionElementKind.NumberField)
            {
                node.In("Minimum", NodePortType.Text, string.Empty);
                node.In("Maximum", NodePortType.Text, string.Empty);
            }
            node.In("Sensitive", NodePortType.Boolean, false);
            node.Input.Single(port => port.Title == "Sensitive").PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(NodeConnectorViewModel.InitialValue))
                {
                    node.Input.Single(port => port.Title == "Default").IsSensitive = node.Input.Single(port => port.Title == "Sensitive").InitialValue is true;
                }
            };
            node.In("Visible When", NodePortType.Record);
            node.In("Enabled When", NodePortType.Record);
            node.In("Required When", NodePortType.Record);
            node.Out("Component", NodePortType.UiComponent);
        }
        else if (typeKey.StartsWith("UI.Layout.", StringComparison.Ordinal))
        {
            node.In("Label", NodePortType.Text, string.Empty);
            node.In("First", NodePortType.UiComponent);
            node.In("Second", NodePortType.UiComponent);
            node.Out("Component", NodePortType.UiComponent);
        }
        else
        {
            switch (typeKey)
            {
                case "UI.Notification.Show":
                case "UI.Progress.Open":
                    node.In("Flow", NodePortType.Flow);
                    node.In("Title", NodePortType.Text, string.Empty);
                    node.In("Message", NodePortType.Text, string.Empty);
                    node.Out("Completed", NodePortType.Flow);
                    if (typeKey == "UI.Progress.Open")
                    {
                        node.Out("Handle", NodePortType.Record);
                    }
                    break;
                case "UI.Progress.Update":
                case "UI.Progress.Close":
                    node.In("Flow", NodePortType.Flow);
                    node.In("Handle", NodePortType.Record, required: true);
                    if (typeKey == "UI.Progress.Update")
                    {
                        node.In("Message", NodePortType.Text, string.Empty);
                        node.In("Progress", NodePortType.Number, 0d);
                    }
                    node.Out("Completed", NodePortType.Flow);
                    break;
                case "UI.Content.Text":
                    node.In("Text", NodePortType.Text, string.Empty, multiline: true);
                    node.Out("Component", NodePortType.UiComponent);
                    break;
                case "UI.Content.Divider":
                    node.Out("Component", NodePortType.UiComponent);
                    break;
                case "UI.Content.Image":
                    node.In("Path", NodePortType.Path, string.Empty);
                    node.Out("Component", NodePortType.UiComponent);
                    break;
                case "UI.Condition.Equals":
                    node.In("Field ID", NodePortType.Text, string.Empty);
                    node.In("Expected", NodePortType.Text, string.Empty);
                    node.In("Value Type", NodePortType.Text, "Text", choices: ["Text", "Number", "Boolean"]);
                    node.Out("Condition", NodePortType.Record);
                    break;
                case "UI.Form.Create":
                    node.In("Title", NodePortType.Text, string.Empty);
                    node.In("Content", NodePortType.UiComponent, required: true);
                    node.In("Submit Label", NodePortType.Text, string.Empty);
                    node.In("Cancel Label", NodePortType.Text, string.Empty);
                    node.Out("Form", NodePortType.UiForm);
                    break;
                case "UI.Dialog.Show":
                case "UI.Dialog.Confirm":
                    node.In("Flow", NodePortType.Flow);
                    if (typeKey == "UI.Dialog.Show")
                    {
                        node.In("Form", NodePortType.UiForm, required: true);
                    }
                    else
                    {
                        node.In("Title", NodePortType.Text, string.Empty);
                        node.In("Message", NodePortType.Text, string.Empty, multiline: true);
                    }
                    node.In("Timeout Seconds", NodePortType.Number, 0d);
                    node.Out("Submitted", NodePortType.Flow);
                    node.Out("Cancelled", NodePortType.Flow);
                    node.Out("TimedOut", NodePortType.Flow);
                    node.Out("Values", NodePortType.Record);
                    break;
                case "UI.Result.Read":
                    node.In("Values", NodePortType.Record, required: true);
                    node.In("Field ID", NodePortType.Text, string.Empty);
                    node.In("Value Type", NodePortType.Text, "Text", choices: ["Text", "Number", "Boolean", "List"]);
                    node.Out("Text", NodePortType.Text);
                    node.Out("Number", NodePortType.Number);
                    node.Out("Boolean", NodePortType.Boolean);
                    node.Out("Selection", NodePortType.List);
                    break;
            }
        }
        return node;
    }

    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (SchemaError is not null)
        {
            throw new InvalidDataException(SchemaError);
        }
        foreach (var output in Output)
        {
            output.Data = output.PortType == NodePortType.Flow ? false : null;
        }
        if (TypeKey.StartsWith("UI.Field.", StringComparison.Ordinal))
        {
            var kind = ParseFieldKind(TypeKey);
            WriteOutput("Component", new InteractionElement
            {
                Id = Text("Field ID"),
                Kind = kind,
                Label = Text("Label"),
                Description = Text("Description"),
                Required = Bool("Required"),
                Sensitive = Bool("Sensitive"),
                MaxLength = ReadMaximumLength(),
                DefaultValue = Value(ReadInput("Default"), kind == InteractionElementKind.NumberField ? "Number" : kind == InteractionElementKind.BooleanField ? "Boolean" : kind == InteractionElementKind.MultiChoiceField ? "List" : "Text"),
                Options = Text("Options").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                Minimum = OptionalNumber("Minimum"),
                Maximum = OptionalNumber("Maximum"),
                VisibleWhen = ReadInput("Visible When") as InteractionCondition,
                EnabledWhen = ReadInput("Enabled When") as InteractionCondition,
                RequiredWhen = ReadInput("Required When") as InteractionCondition
            });
            return;
        }
        if (TypeKey.StartsWith("UI.Layout.", StringComparison.Ordinal))
        {
            var children = new[] { ReadInput("First"), ReadInput("Second") }.OfType<InteractionElement>().ToArray();
            WriteOutput("Component", new InteractionElement { Id = InstanceId, Kind = Enum.Parse<InteractionElementKind>(TypeKey[10..]), Label = Text("Label"), Children = children });
            return;
        }
        switch (TypeKey)
        {
            case "UI.Notification.Show":
            case "UI.Progress.Open":
                var feedback = context.Interactions as IWorkflowFeedbackService ?? throw new NotSupportedException("The host does not support feedback.");
                if (TypeKey == "UI.Notification.Show")
                {
                    await feedback.NotifyAsync(Text("Title"), Text("Message"), context.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var handle = await feedback.OpenProgressAsync(Text("Title"), Text("Message"), context.CancellationToken).ConfigureAwait(false);
                    try
                    {
                        context.RegisterResource(handle);
                    }
                    catch
                    {
                        await handle.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                    WriteOutput("Handle", handle);
                }
                WriteOutput("Completed", true);
                break;
            case "UI.Progress.Update":
                await Require<IInteractionProgress>("Handle").UpdateAsync(Text("Message"), Convert.ToDouble(ReadInput("Progress"), CultureInfo.InvariantCulture), context.CancellationToken).ConfigureAwait(false);
                WriteOutput("Completed", true);
                break;
            case "UI.Progress.Close":
                await Require<IInteractionProgress>("Handle").DisposeAsync().ConfigureAwait(false);
                WriteOutput("Completed", true);
                break;
            case "UI.Content.Text":
            case "UI.Content.Divider":
                WriteOutput("Component", new InteractionElement { Id = InstanceId, Kind = TypeKey.EndsWith("Text", StringComparison.Ordinal) ? InteractionElementKind.Text : InteractionElementKind.Divider, Label = Text("Text") });
                break;
            case "UI.Content.Image":
                var path = Text("Path");
                await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    if (stream.Length is <= 0 or > 8 * 1024 * 1024)
                    {
                        throw new InvalidDataException("An interaction image must be between 1 byte and 8 MiB.");
                    }
                    var data = new byte[(int)stream.Length];
                    await stream.ReadExactlyAsync(data, context.CancellationToken).ConfigureAwait(false);
                    WriteOutput("Component", new InteractionElement { Id = InstanceId, Kind = InteractionElementKind.Image, ImageData = data });
                }
                break;
            case "UI.Condition.Equals":
                WriteOutput("Condition", new InteractionCondition { FieldId = Text("Field ID"), EqualsValue = Value(ReadInput("Expected"), Text("Value Type")) });
                break;
            case "UI.Form.Create":
                var form = new InteractionForm
                {
                    Title = Text("Title"),
                    Content = Require<InteractionElement>("Content"),
                    SubmitLabel = NullIfEmpty(Text("Submit Label")),
                    CancelLabel = NullIfEmpty(Text("Cancel Label"))
                };
                _ = new InteractionFormState(form);
                WriteOutput("Form", form);
                break;
            case "UI.Dialog.Show":
            case "UI.Dialog.Confirm":
                var dialogForm = TypeKey == "UI.Dialog.Show" ? Require<InteractionForm>("Form") : new InteractionForm
                {
                    Title = Text("Title"),
                    Content = new InteractionElement { Id = "message", Kind = InteractionElementKind.Text, Label = Text("Message") }
                };
                var seconds = Convert.ToDouble(ReadInput("Timeout Seconds"), CultureInfo.InvariantCulture);
                if (!double.IsFinite(seconds) || seconds < 0 || seconds > 86_400)
                {
                    throw new InvalidDataException("Invalid interaction timeout.");
                }
                using (context.BeginUserInteractionWait())
                {
                    var result = await context.Interactions.ShowAsync(new InteractionRequest
                    {
                        Form = dialogForm,
                        Source = LocalizedTitle,
                        Timeout = seconds == 0 ? null : TimeSpan.FromSeconds(seconds)
                    }, context.CancellationToken).ConfigureAwait(false);
                    if (result.Outcome == InteractionOutcome.Submitted)
                    {
                        WriteOutput("Values", new InteractionRecord(result.Values, dialogForm));
                        var runtimeFields = new InteractionFormState(dialogForm).Fields.ToDictionary(field => field.Id, StringComparer.Ordinal);
                        foreach (var field in _fieldSchema.Values)
                        {
                            if (result.Values.TryGetValue(field.Id, out var fieldValue))
                            {
                                var data = field.Type switch
                                {
                                    NodePortType.Boolean => (object?)fieldValue.Boolean,
                                    NodePortType.Number => fieldValue.Number,
                                    NodePortType.List => fieldValue.Selection,
                                    _ => fieldValue.Text
                                };
                                var output = Output.Single(port => port.PortId == "field." + field.Id);
                                output.IsSensitive = runtimeFields.TryGetValue(field.Id, out var runtimeField) && runtimeField.Sensitive;
                                output.Data = data;
                            }
                        }
                    }
                    WriteOutput(result.Outcome.ToString(), true);
                }
                break;
            case "UI.Result.Read":
                var record = Require<InteractionRecord>("Values");
                if (!record.Values.TryGetValue(Text("Field ID"), out var value))
                {
                    throw new InvalidDataException("The submitted result does not contain that field.");
                }
                var type = Text("Value Type");
                var sensitive = new InteractionFormState(record.Schema).Fields.Any(field => field.Id == Text("Field ID") && field.Sensitive);
                foreach (var output in Output)
                {
                    output.IsSensitive = sensitive;
                }
                switch (type)
                {
                    case "Text" when value.Text is not null: WriteOutput("Text", value.Text); break;
                    case "Number" when value.Number.HasValue: WriteOutput("Number", value.Number.Value); break;
                    case "Boolean" when value.Boolean.HasValue: WriteOutput("Boolean", value.Boolean.Value); break;
                    case "List" when value.Selection is not null: WriteOutput("Selection", value.Selection); break;
                    default: throw new InvalidDataException("The submitted field does not have the requested type.");
                }
                break;
        }
    }

    private static InteractionElementKind ParseFieldKind(string typeKey) => typeKey[9..] switch
    {
        "Text" => InteractionElementKind.TextField,
        "Number" => InteractionElementKind.NumberField,
        "Boolean" => InteractionElementKind.BooleanField,
        "Choice" => InteractionElementKind.ChoiceField,
        "MultiChoice" => InteractionElementKind.MultiChoiceField,
        "File" => InteractionElementKind.FileField,
        "Directory" => InteractionElementKind.DirectoryField,
        "SavePath" => InteractionElementKind.SavePathField,
        _ => throw new InvalidDataException("Unknown field kind.")
    };

    private static InteractionValue Value(object? value, string type) => type switch
    {
        "Number" => string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)) ? new InteractionValue() : new InteractionValue { Number = Convert.ToDecimal(value, CultureInfo.InvariantCulture) },
        "Boolean" => new InteractionValue { Boolean = Convert.ToBoolean(value, CultureInfo.InvariantCulture) },
        "List" => new InteractionValue { Selection = (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) },
        "Text" => new InteractionValue { Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty },
        _ => throw new InvalidDataException("Unknown interaction value type.")
    };

    private T Require<T>(string title) where T : class => ReadInput(title) as T ?? throw new InvalidDataException($"Missing {title} input.");
    private string Text(string title) => Convert.ToString(ReadInput(title), CultureInfo.InvariantCulture) ?? string.Empty;
    private bool Bool(string title) => Convert.ToBoolean(ReadInput(title), CultureInfo.InvariantCulture);
    private decimal? OptionalNumber(string title) => string.IsNullOrWhiteSpace(Text(title)) ? null : decimal.Parse(Text(title), CultureInfo.InvariantCulture);
    private int ReadMaximumLength()
    {
        var value = Convert.ToDouble(ReadInput("Max Length"), CultureInfo.InvariantCulture);
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value is < 1 or > 1_048_576)
        {
            throw new InvalidDataException("Invalid field length limit.");
        }
        return (int)value;
    }
    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text;
    private void In(string title, NodePortType type, object? initial = null, bool multiline = false, string[]? choices = null, bool required = false)
    {
        Input.Add(new NodeConnectorViewModel
        {
            Title = title,
            PortId = "input." + title.Replace(' ', '-').ToLowerInvariant(),
            PortType = type,
            IsInput = true,
            IsRequired = required,
            InitialValue = initial,
            MaxConnections = 1,
            IsMultiline = multiline,
            Choices = choices?.Select(item => new NodePortChoice(item)).ToArray() ?? []
        });
    }
    private void Out(string title, NodePortType type)
    {
        Output.Add(new NodeConnectorViewModel { Title = title, PortId = "output." + title.ToLowerInvariant(), PortType = type });
    }
}

/// <summary>Invocation-scoped typed values; a summary never reveals potentially sensitive field contents.</summary>
public sealed class InteractionRecord(IReadOnlyDictionary<string, InteractionValue> values, InteractionForm schema)
{
    public IReadOnlyDictionary<string, InteractionValue> Values { get; } = values;
    public InteractionForm Schema { get; } = schema;
    public override string ToString() => $"{Values.Count} fields";
}