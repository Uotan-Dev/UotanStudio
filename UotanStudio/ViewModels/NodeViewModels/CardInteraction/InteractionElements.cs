using System.Globalization;
using UotanStudio.Projects.Interactions;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Interactions;
using UotanStudio.ViewModels.Nodes;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Descriptor for one <c>UI.*</c> interaction element: port declaration and execution delegate are paired here,
/// forming the single source for both the construction and execution dispatch paths.
/// <para>单个 <c>UI.*</c> 交互元素的描述符：端口声明与执行委托在此配对，是构造与执行两条分发路径的唯一来源。</para>
/// </summary>
public sealed class InteractionElementDescriptor
{
    /// <summary>
    /// Workflow type key of the element (such as <c>UI.Field.Text</c>).
    /// <para>元素的工作流类型键（如 <c>UI.Field.Text</c>）。</para>
    /// </summary>
    public required string TypeKey { get; init; }

    /// <summary>
    /// Interaction field kind for field elements; null for non-field elements.
    /// <para>字段类元素对应的交互字段种类；非字段元素为 null。</para>
    /// </summary>
    public InteractionElementKind? FieldKind { get; init; }

    /// <summary>
    /// Declares the port set on a freshly created node.
    /// <para>在新建节点上声明端口集合。</para>
    /// </summary>
    public required Action<InteractionNodeViewModel> DeclarePorts { get; init; }

    /// <summary>
    /// Execution delegate receiving the host node and the execution context.
    /// <para>执行该元素的委托，接收宿主节点与执行上下文。</para>
    /// </summary>
    public required Func<InteractionNodeViewModel, INodeExecutionContext, Task> Execute { get; init; }
}

/// <summary>
/// Registry of all <c>UI.*</c> element descriptors; adding an element only requires one descriptor entry.
/// <para>全部 <c>UI.*</c> 元素描述符的注册表；新增元素只需补一个描述符条目。</para>
/// </summary>
internal static class InteractionElements
{
    private static readonly IReadOnlyDictionary<string, InteractionElementDescriptor> Descriptors =
        new Dictionary<string, InteractionElementDescriptor>(StringComparer.Ordinal)
        {
            ["UI.Content.Text"] = new()
            {
                TypeKey = "UI.Content.Text",
                DeclarePorts = node =>
                {
                    node.In(new NodeInputDeclaration("Text", NodePortType.Text, string.Empty) { Multiline = true });
                    node.Out(new NodeOutputDeclaration("Component", NodePortType.UiComponent));
                },
                Execute = ExecuteContentAsync
            },
            ["UI.Content.Divider"] = new()
            {
                TypeKey = "UI.Content.Divider",
                DeclarePorts = node => node.Out(new NodeOutputDeclaration("Component", NodePortType.UiComponent)),
                Execute = ExecuteContentAsync
            },
            ["UI.Content.Image"] = new()
            {
                TypeKey = "UI.Content.Image",
                DeclarePorts = node =>
                {
                    node.In(new NodeInputDeclaration("Path", NodePortType.Path, string.Empty));
                    node.Out(new NodeOutputDeclaration("Component", NodePortType.UiComponent));
                },
                Execute = ExecuteContentImageAsync
            },
            ["UI.Field.Text"] = Field("Text", InteractionElementKind.TextField),
            ["UI.Field.Number"] = Field("Number", InteractionElementKind.NumberField),
            ["UI.Field.Boolean"] = Field("Boolean", InteractionElementKind.BooleanField),
            ["UI.Field.Choice"] = Field("Choice", InteractionElementKind.ChoiceField),
            ["UI.Field.MultiChoice"] = Field("MultiChoice", InteractionElementKind.MultiChoiceField),
            ["UI.Field.File"] = Field("File", InteractionElementKind.FileField),
            ["UI.Field.Directory"] = Field("Directory", InteractionElementKind.DirectoryField),
            ["UI.Field.SavePath"] = Field("SavePath", InteractionElementKind.SavePathField),
            ["UI.Layout.Row"] = Layout("Row"),
            ["UI.Layout.Column"] = Layout("Column"),
            ["UI.Layout.Group"] = Layout("Group"),
            ["UI.Layout.Pages"] = Layout("Pages"),
            ["UI.Condition.Equals"] = new()
            {
                TypeKey = "UI.Condition.Equals",
                DeclarePorts = node =>
                {
                    node.In(new NodeInputDeclaration("Field ID", NodePortType.Text, string.Empty));
                    node.In(new NodeInputDeclaration("Expected", NodePortType.Text, string.Empty));
                    node.In(new NodeInputDeclaration("Value Type", NodePortType.Text, "Text") { Choices = ["Text", "Number", "Boolean"] });
                    node.Out(new NodeOutputDeclaration("Condition", NodePortType.Record));
                },
                Execute = ExecuteConditionAsync
            },
            ["UI.Form.Create"] = new()
            {
                TypeKey = "UI.Form.Create",
                DeclarePorts = node =>
                {
                    node.In(new NodeInputDeclaration("Title", NodePortType.Text, string.Empty));
                    node.In(new NodeInputDeclaration("Content", NodePortType.UiComponent) { Required = true });
                    node.In(new NodeInputDeclaration("Submit Label", NodePortType.Text, string.Empty));
                    node.In(new NodeInputDeclaration("Cancel Label", NodePortType.Text, string.Empty));
                    node.Out(new NodeOutputDeclaration("Form", NodePortType.UiForm));
                },
                Execute = ExecuteFormAsync
            },
            ["UI.Dialog.Show"] = Dialog(isConfirmation: false),
            ["UI.Result.Read"] = new()
            {
                TypeKey = "UI.Result.Read",
                DeclarePorts = node =>
                {
                    node.In(new NodeInputDeclaration("Values", NodePortType.Record) { Required = true });
                    node.In(new NodeInputDeclaration("Field ID", NodePortType.Text, string.Empty));
                    node.In(new NodeInputDeclaration("Value Type", NodePortType.Text, "Text") { Choices = ["Text", "Number", "Boolean", "List"] });
                    node.Out(new NodeOutputDeclaration("Text", NodePortType.Text));
                    node.Out(new NodeOutputDeclaration("Number", NodePortType.Number));
                    node.Out(new NodeOutputDeclaration("Boolean", NodePortType.Boolean));
                    node.Out(new NodeOutputDeclaration("Selection", NodePortType.List));
                },
                Execute = ExecuteResultAsync
            },
            ["UI.Dialog.Confirm"] = Dialog(isConfirmation: true),
            ["UI.Notification.Show"] = Feedback(),
            ["UI.Progress.Open"] = Feedback(withHandleOutput: true),
            ["UI.Progress.Update"] = ProgressHandle(withProgressInputs: true),
            ["UI.Progress.Close"] = ProgressHandle(withProgressInputs: false)
        };

    internal static IReadOnlyList<string> TypeKeys { get; } =
    [
        "UI.Content.Text", "UI.Content.Divider", "UI.Content.Image",
        "UI.Field.Text", "UI.Field.Number", "UI.Field.Boolean", "UI.Field.Choice", "UI.Field.MultiChoice",
        "UI.Field.File", "UI.Field.Directory", "UI.Field.SavePath",
        "UI.Layout.Row", "UI.Layout.Column", "UI.Layout.Group", "UI.Layout.Pages",
        "UI.Condition.Equals", "UI.Form.Create", "UI.Dialog.Show", "UI.Result.Read",
        "UI.Dialog.Confirm", "UI.Notification.Show", "UI.Progress.Open", "UI.Progress.Update", "UI.Progress.Close"
    ];

    internal static bool TryGetDescriptor(string typeKey, out InteractionElementDescriptor descriptor)
        => Descriptors.TryGetValue(typeKey, out descriptor!);

    private static InteractionElementDescriptor Field(string suffix, InteractionElementKind kind) => new()
    {
        TypeKey = "UI.Field." + suffix,
        FieldKind = kind,
        DeclarePorts = node =>
        {
            node.In(new NodeInputDeclaration("Field ID", NodePortType.Text, Guid.NewGuid().ToString("N")) { IsConfiguration = true, Advanced = true });
            node.In(new NodeInputDeclaration("Label", NodePortType.Text, string.Empty));
            node.In(new NodeInputDeclaration("Description", NodePortType.Text, string.Empty) { Advanced = true });
            node.In(new NodeInputDeclaration("Required", NodePortType.Boolean, false));
            node.In(new NodeInputDeclaration("Max Length", NodePortType.Number, 4096d) { Advanced = true });
            node.In(new NodeInputDeclaration("Default",
                kind == InteractionElementKind.NumberField ? NodePortType.Number : kind == InteractionElementKind.BooleanField ? NodePortType.Boolean : NodePortType.Text,
                kind == InteractionElementKind.BooleanField ? false : string.Empty));
            if (kind is InteractionElementKind.ChoiceField or InteractionElementKind.MultiChoiceField)
            {
                node.In(new NodeInputDeclaration("Options", NodePortType.Text, string.Empty) { Multiline = true });
            }
            if (kind == InteractionElementKind.NumberField)
            {
                node.In(new NodeInputDeclaration("Minimum", NodePortType.Text, string.Empty) { Advanced = true });
                node.In(new NodeInputDeclaration("Maximum", NodePortType.Text, string.Empty) { Advanced = true });
            }
            node.In(new NodeInputDeclaration("Sensitive", NodePortType.Boolean, false) { Advanced = true });
            node.Input.Single(port => port.Title == "Sensitive").PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(NodeConnectorViewModel.InitialValue))
                {
                    node.Input.Single(port => port.Title == "Default").IsSensitive =
                        node.Input.Single(port => port.Title == "Sensitive").InitialValue is true;
                }
            };
            node.In(new NodeInputDeclaration("Visible When", NodePortType.Record) { Advanced = true });
            node.In(new NodeInputDeclaration("Enabled When", NodePortType.Record) { Advanced = true });
            node.In(new NodeInputDeclaration("Required When", NodePortType.Record) { Advanced = true });
            node.Out(new NodeOutputDeclaration("Component", NodePortType.UiComponent));
        },
        Execute = ExecuteFieldAsync
    };

    private static InteractionElementDescriptor Layout(string suffix) => new()
    {
        TypeKey = "UI.Layout." + suffix,
        DeclarePorts = node =>
        {
            node.In(new NodeInputDeclaration("Label", NodePortType.Text, string.Empty));
            node.In(new NodeInputDeclaration("First", NodePortType.UiComponent));
            node.In(new NodeInputDeclaration("Second", NodePortType.UiComponent));
            node.Out(new NodeOutputDeclaration("Component", NodePortType.UiComponent));
        },
        Execute = ExecuteLayoutAsync
    };

    private static InteractionElementDescriptor Dialog(bool isConfirmation) => new()
    {
        TypeKey = isConfirmation ? "UI.Dialog.Confirm" : "UI.Dialog.Show",
        DeclarePorts = node =>
        {
            node.In(new NodeInputDeclaration("Flow", NodePortType.Flow));
            if (node.TypeKey == "UI.Dialog.Show")
            {
                node.In(new NodeInputDeclaration("Form", NodePortType.UiForm) { Required = true });
            }
            else
            {
                node.In(new NodeInputDeclaration("Title", NodePortType.Text, string.Empty));
                node.In(new NodeInputDeclaration("Message", NodePortType.Text, string.Empty) { Multiline = true });
            }
            node.In(new NodeInputDeclaration("Timeout Seconds", NodePortType.Number, 0d) { Advanced = true });
            node.Out(new NodeOutputDeclaration("Submitted", NodePortType.Flow));
            node.Out(new NodeOutputDeclaration("Cancelled", NodePortType.Flow));
            node.Out(new NodeOutputDeclaration("TimedOut", NodePortType.Flow));
            node.Out(new NodeOutputDeclaration("Values", NodePortType.Record));
        },
        Execute = ExecuteDialogAsync
    };

    private static InteractionElementDescriptor Feedback(bool withHandleOutput = false) => new()
    {
        TypeKey = withHandleOutput ? "UI.Progress.Open" : "UI.Notification.Show",
        DeclarePorts = node =>
        {
            node.In(new NodeInputDeclaration("Flow", NodePortType.Flow));
            node.In(new NodeInputDeclaration("Title", NodePortType.Text, string.Empty));
            node.In(new NodeInputDeclaration("Message", NodePortType.Text, string.Empty));
            node.Out(new NodeOutputDeclaration("Completed", NodePortType.Flow));
            if (withHandleOutput)
            {
                node.Out(new NodeOutputDeclaration("Handle", NodePortType.Record));
            }
        },
        Execute = ExecuteFeedbackAsync
    };

    private static InteractionElementDescriptor ProgressHandle(bool withProgressInputs) => new()
    {
        TypeKey = withProgressInputs ? "UI.Progress.Update" : "UI.Progress.Close",
        DeclarePorts = node =>
        {
            node.In(new NodeInputDeclaration("Flow", NodePortType.Flow));
            node.In(new NodeInputDeclaration("Handle", NodePortType.Record) { Required = true });
            if (withProgressInputs)
            {
                node.In(new NodeInputDeclaration("Message", NodePortType.Text, string.Empty));
                node.In(new NodeInputDeclaration("Progress", NodePortType.Number, 0d));
            }
            node.Out(new NodeOutputDeclaration("Completed", NodePortType.Flow));
        },
        Execute = ExecuteProgressHandleAsync
    };

    private static Task ExecuteFieldAsync(InteractionNodeViewModel node, INodeExecutionContext context)
    {
        _ = context;
        var kind = ParseFieldKind(node.TypeKey);
        node.WriteOutput("Component", new InteractionElement
        {
            Id = node.Text("Field ID"),
            Kind = kind,
            Label = node.Text("Label"),
            Description = node.Text("Description"),
            Required = node.Bool("Required"),
            Sensitive = node.Bool("Sensitive"),
            MaxLength = node.ReadMaximumLength(),
            DefaultValue = InteractionNodeViewModel.Value(node.ReadInput("Default"),
                kind == InteractionElementKind.NumberField ? "Number" : kind == InteractionElementKind.BooleanField ? "Boolean" : kind == InteractionElementKind.MultiChoiceField ? "List" : "Text"),
            Options = node.Text("Options").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            Minimum = node.OptionalNumber("Minimum"),
            Maximum = node.OptionalNumber("Maximum"),
            VisibleWhen = node.ReadInput("Visible When") as InteractionCondition,
            EnabledWhen = node.ReadInput("Enabled When") as InteractionCondition,
            RequiredWhen = node.ReadInput("Required When") as InteractionCondition
        });
        return Task.CompletedTask;
    }

    private static Task ExecuteLayoutAsync(InteractionNodeViewModel node, INodeExecutionContext context)
    {
        _ = context;
        var children = new[] { node.ReadInput("First"), node.ReadInput("Second") }.OfType<InteractionElement>().ToArray();
        node.WriteOutput("Component", new InteractionElement
        {
            Id = node.InstanceId,
            Kind = Enum.Parse<InteractionElementKind>(node.TypeKey[10..]),
            Label = node.Text("Label"),
            Children = children
        });
        return Task.CompletedTask;
    }

    private static async Task ExecuteFeedbackAsync(InteractionNodeViewModel node, INodeExecutionContext context)
    {
        var feedback = context.Interactions as IWorkflowFeedbackService ?? throw new NotSupportedException("The host does not support feedback.");
        if (node.TypeKey == "UI.Notification.Show")
        {
            await feedback.NotifyAsync(node.Text("Title"), node.Text("Message"), context.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            var handle = await feedback.OpenProgressAsync(node.Text("Title"), node.Text("Message"), context.CancellationToken).ConfigureAwait(false);
            try
            {
                context.RegisterResource(handle);
            }
            catch
            {
                await handle.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            node.WriteOutput("Handle", handle);
        }
        node.WriteOutput("Completed", true);
    }

    private static async Task ExecuteProgressHandleAsync(InteractionNodeViewModel node, INodeExecutionContext context)
    {
        if (node.TypeKey == "UI.Progress.Update")
        {
            await node.Require<IInteractionProgress>("Handle").UpdateAsync(node.Text("Message"),
                Convert.ToDouble(node.ReadInput("Progress"), CultureInfo.InvariantCulture), context.CancellationToken).ConfigureAwait(false);
            node.WriteOutput("Completed", true);
            return;
        }
        await node.Require<IInteractionProgress>("Handle").DisposeAsync().ConfigureAwait(false);
        node.WriteOutput("Completed", true);
    }

    private static Task ExecuteContentAsync(InteractionNodeViewModel node, INodeExecutionContext context)
    {
        _ = context;
        node.WriteOutput("Component", new InteractionElement
        {
            Id = node.InstanceId,
            Kind = node.TypeKey.EndsWith("Text", StringComparison.Ordinal) ? InteractionElementKind.Text : InteractionElementKind.Divider,
            Label = node.Text("Text")
        });
        return Task.CompletedTask;
    }

    private static async Task ExecuteContentImageAsync(InteractionNodeViewModel node, INodeExecutionContext context)
    {
        var path = node.Text("Path");
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length is <= 0 or > 8 * 1024 * 1024)
            {
                throw new InvalidDataException("An interaction image must be between 1 byte and 8 MiB.");
            }
            var data = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(data, context.CancellationToken).ConfigureAwait(false);
            node.WriteOutput("Component", new InteractionElement { Id = node.InstanceId, Kind = InteractionElementKind.Image, ImageData = data });
        }
    }

    private static Task ExecuteConditionAsync(InteractionNodeViewModel node, INodeExecutionContext context)
    {
        _ = context;
        node.WriteOutput("Condition", new InteractionCondition
        {
            FieldId = node.Text("Field ID"),
            EqualsValue = InteractionNodeViewModel.Value(node.ReadInput("Expected"), node.Text("Value Type"))
        });
        return Task.CompletedTask;
    }

    private static Task ExecuteFormAsync(InteractionNodeViewModel node, INodeExecutionContext context)
    {
        _ = context;
        var form = new InteractionForm
        {
            Title = node.Text("Title"),
            Content = node.Require<InteractionElement>("Content"),
            SubmitLabel = InteractionNodeViewModel.NullIfEmpty(node.Text("Submit Label")),
            CancelLabel = InteractionNodeViewModel.NullIfEmpty(node.Text("Cancel Label"))
        };
        _ = new InteractionFormState(form);
        node.WriteOutput("Form", form);
        return Task.CompletedTask;
    }

    private static async Task ExecuteDialogAsync(InteractionNodeViewModel node, INodeExecutionContext context)
    {
        var dialogForm = node.TypeKey == "UI.Dialog.Show" ? node.Require<InteractionForm>("Form") : new InteractionForm
        {
            Title = node.Text("Title"),
            Content = new InteractionElement { Id = "message", Kind = InteractionElementKind.Text, Label = node.Text("Message") }
        };
        var seconds = Convert.ToDouble(node.ReadInput("Timeout Seconds"), CultureInfo.InvariantCulture);
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 86_400)
        {
            throw new InvalidDataException("Invalid interaction timeout.");
        }
        using (context.BeginUserInteractionWait())
        {
            var result = await context.Interactions.ShowAsync(new InteractionRequest
            {
                Form = dialogForm,
                Source = node.LocalizedTitle,
                Timeout = seconds == 0 ? null : TimeSpan.FromSeconds(seconds)
            }, context.CancellationToken).ConfigureAwait(false);
            if (result.Outcome == InteractionOutcome.Submitted)
            {
                node.WriteOutput("Values", new InteractionRecord(result.Values, dialogForm));
                var runtimeFields = new InteractionFormState(dialogForm).Fields.ToDictionary(field => field.Id, StringComparer.Ordinal);
                foreach (var field in node.FieldSchema.Values)
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
                        var output = node.Output.Single(port => port.PortId == "field." + field.Id);
                        output.IsSensitive = runtimeFields.TryGetValue(field.Id, out var runtimeField) && runtimeField.Sensitive;
                        output.Data = data;
                    }
                }
            }
            node.WriteOutput(result.Outcome.ToString(), true);
        }
    }

    private static Task ExecuteResultAsync(InteractionNodeViewModel node, INodeExecutionContext context)
    {
        _ = context;
        var record = node.Require<InteractionRecord>("Values");
        if (!record.Values.TryGetValue(node.Text("Field ID"), out var value))
        {
            throw new InvalidDataException("The submitted result does not contain that field.");
        }
        var type = node.Text("Value Type");
        var sensitive = new InteractionFormState(record.Schema).Fields.Any(field => field.Id == node.Text("Field ID") && field.Sensitive);
        foreach (var output in node.Output)
        {
            output.IsSensitive = sensitive;
        }
        switch (type)
        {
            case "Text" when value.Text is not null: node.WriteOutput("Text", value.Text); break;
            case "Number" when value.Number.HasValue: node.WriteOutput("Number", value.Number.Value); break;
            case "Boolean" when value.Boolean.HasValue: node.WriteOutput("Boolean", value.Boolean.Value); break;
            case "List" when value.Selection is not null: node.WriteOutput("Selection", value.Selection); break;
            default: throw new InvalidDataException("The submitted field does not have the requested type.");
        }
        return Task.CompletedTask;
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
}