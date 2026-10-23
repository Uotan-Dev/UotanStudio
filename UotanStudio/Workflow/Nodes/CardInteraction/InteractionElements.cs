using System.Globalization;
using UotanStudio.Projects.Interactions;
using UotanStudio.Services.Interactions;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Registry;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Descriptor for one <c>UI.*</c> interaction element: port declaration and execution delegate are paired here,
/// forming the single source for both the construction and execution dispatch paths.
/// <para>单个 <c>UI.*</c> 交互元素的描述符：端口声明与执行委托在此配对，是构造与执行两条分发路径的唯一来源。</para>
/// </summary>
public sealed class InteractionElementDescriptor
{
    /// <summary>
    /// Workflow type key of the element (such as <c>UI.Field.Create</c>).
    /// <para>元素的工作流类型键（如 <c>UI.Field.Create</c>）。</para>
    /// </summary>
    public required string TypeKey { get; init; }

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
/// Registry of all <c>UI.*</c> element descriptors. Element families that differ only in one discriminator value
/// are served by a single node carrying that value on a <c>Kind</c> or <c>Mode</c> input port, so the toolbox exposes
/// one builder per element family rather than one per variant.
/// <para>全部 <c>UI.*</c> 元素描述符的注册表。仅靠单一判别取值区分的元素家族由一个节点承载，判别值写在 <c>Kind</c> 或 <c>Mode</c>
/// 输入端口上，因此工具箱按元素家族给出一个构造器，而不是按变体各给一个。</para>
/// </summary>
internal static class InteractionElements
{
    internal static readonly NodeInputDeclaration Kind = new("Kind", NodePortType.Text, "TextField")
    {
        Choices = ["TextField", "NumberField", "BooleanField", "ChoiceField", "MultiChoiceField",
            "FileField", "DirectoryField", "SavePathField"],
        IsConfiguration = true
    };

    internal static readonly NodeInputDeclaration ContentKind = new("Kind", NodePortType.Text, "Text")
    {
        Choices = ["Text", "Divider", "Image"],
        IsConfiguration = true
    };

    internal static readonly NodeInputDeclaration LayoutKind = new("Kind", NodePortType.Text, "Row")
    {
        Choices = ["Row", "Column", "Group", "Pages"],
        IsConfiguration = true
    };

    internal static readonly NodeInputDeclaration ProgressMode = new("Mode", NodePortType.Text, "Update")
    {
        Choices = ["Open", "Update", "Close"],
        IsConfiguration = true
    };

    internal static readonly NodeInputDeclaration DialogMode = new("Mode", NodePortType.Text, "Show")
    {
        Choices = ["Show", "Confirm"],
        IsConfiguration = true
    };

    private static readonly IReadOnlyDictionary<string, InteractionElementDescriptor> Descriptors =
        new Dictionary<string, InteractionElementDescriptor>(StringComparer.Ordinal)
        {
            ["UI.Content.Create"] = new()
            {
                TypeKey = "UI.Content.Create",
                DeclarePorts = node =>
                {
                    node.In(ContentKind);
                    node.In(new NodeInputDeclaration("Text", NodePortType.Text, string.Empty)
                    {
                        Multiline = true,
                        VisibleWhen = NodePortVisibility.MayEqualAny(ContentKind, "Text")
                    });
                    node.In(new NodeInputDeclaration("Path", NodePortType.Path, string.Empty)
                    {
                        VisibleWhen = NodePortVisibility.MayEqualAny(ContentKind, "Image")
                    });
                    node.Out(new NodeOutputDeclaration("Component", NodePortType.UiComponent));
                },
                Execute = ExecuteContentAsync
            },
            ["UI.Field.Create"] = Field(),
            ["UI.Layout.Create"] = Layout(),
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
            ["UI.Dialog"] = Dialog(),
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
            ["UI.Notification.Show"] = Feedback(),
            ["UI.Progress"] = new()
            {
                TypeKey = "UI.Progress",
                DeclarePorts = node =>
                {
                    node.In(new NodeInputDeclaration("Flow", NodePortType.Flow));
                    node.In(ProgressMode);
                    node.In(new NodeInputDeclaration("Title", NodePortType.Text, string.Empty)
                    {
                        VisibleWhen = NodePortVisibility.MayEqualAny(ProgressMode, "Open")
                    });
                    node.In(new NodeInputDeclaration("Message", NodePortType.Text, string.Empty)
                    {
                        VisibleWhen = NodePortVisibility.MayEqualAny(ProgressMode, "Open", "Update")
                    });
                    node.In(new NodeInputDeclaration("Handle", NodePortType.Record)
                    {
                        VisibleWhen = NodePortVisibility.MayEqualAny(ProgressMode, "Update", "Close")
                    });
                    node.In(new NodeInputDeclaration("Progress", NodePortType.Number, 0d)
                    {
                        VisibleWhen = NodePortVisibility.MayEqualAny(ProgressMode, "Update")
                    });
                    node.Out(new NodeOutputDeclaration("Completed", NodePortType.Flow));
                    node.Out(new NodeOutputDeclaration("Handle", NodePortType.Record));
                },
                Execute = ExecuteProgressAsync
            }
        };

    internal static IReadOnlyList<string> TypeKeys { get; } =
    [
        "UI.Content.Create", "UI.Field.Create", "UI.Layout.Create",
        "UI.Condition.Equals", "UI.Form.Create", "UI.Dialog", "UI.Result.Read",
        "UI.Notification.Show", "UI.Progress"
    ];

    internal static bool TryGetDescriptor(string typeKey, out InteractionElementDescriptor descriptor)
        => Descriptors.TryGetValue(typeKey, out descriptor!);

    /// <summary>Maps a field element kind to the port type its value is carried on.
    /// <para>把字段元素种类映射到其取值所用的端口类型。</para></summary>
    /// <param name="kind">The declared field kind. <para>声明的字段种类。</para></param>
    /// <returns>The port type for that kind's value. <para>该种类取值对应的端口类型。</para></returns>
    internal static NodePortType FieldType(InteractionElementKind kind) => kind switch
    {
        InteractionElementKind.NumberField => NodePortType.Number,
        InteractionElementKind.BooleanField => NodePortType.Boolean,
        InteractionElementKind.MultiChoiceField => NodePortType.List,
        InteractionElementKind.FileField or InteractionElementKind.DirectoryField or InteractionElementKind.SavePathField => NodePortType.Path,
        _ => NodePortType.Text
    };

    /// <summary>Parses the discriminator value of a field, layout or content builder node.
    /// <para>解析字段、布局或内容构造器节点的判别取值。</para></summary>
    /// <param name="discriminator">The raw discriminator text. <para>判别端口的原始文本。</para></param>
    /// <returns>The parsed kind. <para>解析出的元素种类。</para></returns>
    /// <exception cref="InvalidDataException">Thrown when the discriminator names no interaction element kind.
    /// <para>判别值不对应任何交互元素种类时抛出。</para></exception>
    internal static InteractionElementKind ParseKind(string discriminator) =>
        Enum.TryParse<InteractionElementKind>(discriminator, ignoreCase: false, out var kind)
            ? kind
            : throw new InvalidDataException($"Unknown interaction element kind '{discriminator}'.");

    private static InteractionElementDescriptor Field() => new()
    {
        TypeKey = "UI.Field.Create",
        DeclarePorts = node =>
        {
            node.In(Kind);
            node.In(new NodeInputDeclaration("Field ID", NodePortType.Text, Guid.NewGuid().ToString("N")) { IsConfiguration = true, Advanced = true });
            node.In(new NodeInputDeclaration("Label", NodePortType.Text, string.Empty));
            node.In(new NodeInputDeclaration("Description", NodePortType.Text, string.Empty) { Advanced = true });
            node.In(new NodeInputDeclaration("Required", NodePortType.Boolean, false));
            node.In(new NodeInputDeclaration("Max Length", NodePortType.Number, 4096d) { Advanced = true });
            node.In(new NodeInputDeclaration("Default", NodePortType.Text, string.Empty));
            node.In(new NodeInputDeclaration("Options", NodePortType.Text, string.Empty)
            {
                Multiline = true,
                VisibleWhen = NodePortVisibility.MayEqualAny(Kind, "ChoiceField", "MultiChoiceField")
            });
            node.In(new NodeInputDeclaration("Minimum", NodePortType.Text, string.Empty)
            {
                Advanced = true,
                VisibleWhen = NodePortVisibility.MayEqualAny(Kind, "NumberField")
            });
            node.In(new NodeInputDeclaration("Maximum", NodePortType.Text, string.Empty)
            {
                Advanced = true,
                VisibleWhen = NodePortVisibility.MayEqualAny(Kind, "NumberField")
            });
            node.In(new NodeInputDeclaration("Sensitive", NodePortType.Boolean, false) { Advanced = true });
            node.In(new NodeInputDeclaration("Visible When", NodePortType.Record) { Advanced = true });
            node.In(new NodeInputDeclaration("Enabled When", NodePortType.Record) { Advanced = true });
            node.In(new NodeInputDeclaration("Required When", NodePortType.Record) { Advanced = true });
            node.Out(new NodeOutputDeclaration("Component", NodePortType.UiComponent));

            // The Default port carries the field value, so its type follows the declared kind exactly as the
            // per-kind nodes used to require, and a sensitive field keeps masking its default in the panel.
            var kindPort = node.Input.Single(port => port.Title == "Kind");
            var defaultPort = node.Input.Single(port => port.Title == "Default");
            var sensitivePort = node.Input.Single(port => port.Title == "Sensitive");
            defaultPort.PortType = FieldType(ParseKind(KindText(kindPort)));
            kindPort.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(NodeConnectorViewModel.InitialValue)
                    && kindPort.InitialValue is string value
                    && Enum.TryParse<InteractionElementKind>(value, out var kind))
                {
                    defaultPort.PortType = FieldType(kind);
                }
            };
            sensitivePort.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(NodeConnectorViewModel.InitialValue))
                {
                    defaultPort.IsSensitive = sensitivePort.InitialValue is true;
                }
            };
        },
        Execute = ExecuteFieldAsync
    };

    private static InteractionElementDescriptor Layout() => new()
    {
        TypeKey = "UI.Layout.Create",
        DeclarePorts = node =>
        {
            node.In(LayoutKind);
            node.In(new NodeInputDeclaration("Label", NodePortType.Text, string.Empty));
            node.In(new NodeInputDeclaration("First", NodePortType.UiComponent));
            node.In(new NodeInputDeclaration("Second", NodePortType.UiComponent));
            node.Out(new NodeOutputDeclaration("Component", NodePortType.UiComponent));
        },
        Execute = ExecuteLayoutAsync
    };

    private static string KindText(NodeConnectorViewModel port) =>
        Convert.ToString(port.InitialValue, CultureInfo.InvariantCulture) ?? string.Empty;

    private static InteractionElementDescriptor Dialog() => new()
    {
        TypeKey = "UI.Dialog",
        DeclarePorts = node =>
        {
            node.In(new NodeInputDeclaration("Flow", NodePortType.Flow));
            node.In(DialogMode);
            node.In(new NodeInputDeclaration("Form", NodePortType.UiForm)
            {
                VisibleWhen = NodePortVisibility.MayEqualAny(DialogMode, "Show")
            });
            node.In(new NodeInputDeclaration("Title", NodePortType.Text, string.Empty)
            {
                VisibleWhen = NodePortVisibility.MayEqualAny(DialogMode, "Confirm")
            });
            node.In(new NodeInputDeclaration("Message", NodePortType.Text, string.Empty)
            {
                Multiline = true,
                VisibleWhen = NodePortVisibility.MayEqualAny(DialogMode, "Confirm")
            });
            node.In(new NodeInputDeclaration("Timeout Seconds", NodePortType.Number, 0d) { Advanced = true });
            node.Out(new NodeOutputDeclaration("Submitted", NodePortType.Flow));
            node.Out(new NodeOutputDeclaration("Cancelled", NodePortType.Flow));
            node.Out(new NodeOutputDeclaration("TimedOut", NodePortType.Flow));
            node.Out(new NodeOutputDeclaration("Values", NodePortType.Record));
        },
        Execute = ExecuteDialogAsync
    };

    private static InteractionElementDescriptor Feedback() => new()
    {
        TypeKey = "UI.Notification.Show",
        DeclarePorts = node =>
        {
            node.In(new NodeInputDeclaration("Flow", NodePortType.Flow));
            node.In(new NodeInputDeclaration("Title", NodePortType.Text, string.Empty));
            node.In(new NodeInputDeclaration("Message", NodePortType.Text, string.Empty));
            node.Out(new NodeOutputDeclaration("Completed", NodePortType.Flow));
        },
        Execute = ExecuteFeedbackAsync
    };

    private static Task ExecuteFieldAsync(InteractionNodeViewModel node, INodeExecutionContext context)
    {
        _ = context;
        var kind = ParseKind(node.Text("Kind"));
        node.WriteOutput("Component", new InteractionElement
        {
            Id = node.Text("Field ID"),
            Kind = kind,
            Label = node.Text("Label"),
            Description = node.Text("Description"),
            Required = node.Bool("Required"),
            Sensitive = node.Bool("Sensitive"),
            MaxLength = node.ReadMaximumLength(),
            DefaultValue = InteractionNodeViewModel.Value(node.ReadInput("Default"), kind switch
            {
                InteractionElementKind.NumberField => "Number",
                InteractionElementKind.BooleanField => "Boolean",
                InteractionElementKind.MultiChoiceField => "List",
                _ => "Text"
            }),
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
            Kind = ParseKind(node.Text("Kind")),
            Label = node.Text("Label"),
            Children = children
        });
        return Task.CompletedTask;
    }

    private static async Task ExecuteFeedbackAsync(InteractionNodeViewModel node, INodeExecutionContext context)
    {
        var feedback = context.Interactions as IWorkflowFeedbackService ?? throw new NotSupportedException("The host does not support feedback.");
        await feedback.NotifyAsync(node.Text("Title"), node.Text("Message"), context.CancellationToken).ConfigureAwait(false);
        node.WriteOutput("Completed", true);
    }

    private static async Task ExecuteProgressAsync(InteractionNodeViewModel node, INodeExecutionContext context)
    {
        switch (node.Text("Mode"))
        {
            case "Update":
                await node.Require<IInteractionProgress>("Handle").UpdateAsync(node.Text("Message"),
                    Convert.ToDouble(node.ReadInput("Progress"), CultureInfo.InvariantCulture), context.CancellationToken).ConfigureAwait(false);
                break;
            case "Close":
                await node.Require<IInteractionProgress>("Handle").DisposeAsync().ConfigureAwait(false);
                break;
            default:
                var feedback = context.Interactions as IWorkflowFeedbackService
                    ?? throw new NotSupportedException("The host does not support feedback.");
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
                break;
        }
        node.WriteOutput("Completed", true);
    }

    private static async Task ExecuteContentAsync(InteractionNodeViewModel node, INodeExecutionContext context)
    {
        var kind = ParseKind(node.Text("Kind"));
        if (kind == InteractionElementKind.Image)
        {
            await ExecuteContentImageAsync(node, context).ConfigureAwait(false);
            return;
        }
        node.WriteOutput("Component", new InteractionElement
        {
            Id = node.InstanceId,
            Kind = kind,
            Label = node.Text("Text")
        });
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
        var dialogForm = node.Text("Mode") != "Confirm" ? node.Require<InteractionForm>("Form") : new InteractionForm
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
}