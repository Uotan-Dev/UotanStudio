using System.Globalization;
using UotanStudio.Projects.Interactions;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Interactions;
using UotanStudio.ViewModels.Nodes;

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

    /// <summary>
    /// Type keys of all <c>UI.*</c> elements, ordered like the interaction element registry.
    /// <para>全部 <c>UI.*</c> 元素的类型键，顺序与交互元素注册表一致。</para>
    /// </summary>
    public static IReadOnlyList<string> TypeKeys => InteractionElements.TypeKeys;

    private InteractionNodeViewModel(string typeKey) : base(typeKey, NodeCategory.Output)
    {
        TypeKey = typeKey;
    }

    /// <summary>Creates an interaction node by type key; the port layout comes from the element descriptor.
    /// <para>按类型键创建交互节点，端口布局来自元素描述符。</para></summary>
    /// <param name="typeKey">The interaction element type key.<para>交互元素类型键。</para></param>
    /// <returns>The interaction node with its ports configured.<para>配置好端口的交互节点。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the type key is not a registered interaction element.
    /// <para>类型键不是已注册的交互元素时抛出。</para></exception>
    public static InteractionNodeViewModel Create(string typeKey)
    {
        if (!InteractionElements.TryGetDescriptor(typeKey, out var descriptor))
        {
            throw new ArgumentOutOfRangeException(nameof(typeKey));
        }
        var node = new InteractionNodeViewModel(typeKey);
        descriptor.DeclarePorts(node);
        return node;
    }

    /// <inheritdoc />
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
        if (!InteractionElements.TryGetDescriptor(TypeKey, out var descriptor))
        {
            throw new InvalidDataException("Unknown interaction element.");
        }
        await descriptor.Execute(this, context).ConfigureAwait(false);
    }

    internal IReadOnlyDictionary<string, InteractionFieldSchema> FieldSchema => _fieldSchema;

    internal string Text(string title) => Convert.ToString(ReadInput(title), CultureInfo.InvariantCulture) ?? string.Empty;
    internal bool Bool(string title) => Convert.ToBoolean(ReadInput(title), CultureInfo.InvariantCulture);
    internal decimal? OptionalNumber(string title) => string.IsNullOrWhiteSpace(Text(title)) ? null : decimal.Parse(Text(title), CultureInfo.InvariantCulture);
    internal int ReadMaximumLength()
    {
        var value = Convert.ToDouble(ReadInput("Max Length"), CultureInfo.InvariantCulture);
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value is < 1 or > 1_048_576)
        {
            throw new InvalidDataException("Invalid field length limit.");
        }
        return (int)value;
    }
    internal T Require<T>(string title) where T : class => ReadInput(title) as T ?? throw new InvalidDataException($"Missing {title} input.");
    internal static InteractionValue Value(object? value, string type) => type switch
    {
        "Number" => string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)) ? new InteractionValue() : new InteractionValue { Number = Convert.ToDecimal(value, CultureInfo.InvariantCulture) },
        "Boolean" => new InteractionValue { Boolean = Convert.ToBoolean(value, CultureInfo.InvariantCulture) },
        "List" => new InteractionValue { Selection = (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) },
        "Text" => new InteractionValue { Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty },
        _ => throw new InvalidDataException("Unknown interaction value type.")
    };
    internal static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

    internal void In(NodeInputDeclaration declaration)
    {
        In(declaration.Title, declaration.PortType, declaration.InitialValue, declaration.Multiline, declaration.Choices?.ToArray(), declaration.Required);
        var port = Input[^1];
        port.IsAdvanced = declaration.Advanced;
        port.IsConfiguration = declaration.IsConfiguration;
        port.IsVisibleWhen = declaration.VisibleWhen
            ?? (declaration.Rule is { } rule ? rule.IsSatisfiedBy : null);
    }

    internal void Out(NodeOutputDeclaration declaration) => Out(declaration.Title, declaration.PortType);

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