namespace UotanStudio.Projects.Interactions;

/// <summary>Serializable UI vocabulary; no host controls or executable scripts cross this boundary.</summary>
public enum InteractionElementKind
{
    Text, Divider, Image, TextField, NumberField, BooleanField, ChoiceField, MultiChoiceField,
    FileField, DirectoryField, SavePathField, Row, Column, Group, Pages
}

/// <summary>A scalar or list value whose representation is independent of the UI host.</summary>
public sealed record InteractionValue
{
    public string? Text { get; init; }
    public decimal? Number { get; init; }
    public bool? Boolean { get; init; }
    public string[]? Selection { get; init; }
    public override string ToString() => "[interaction value]";
}

/// <summary>A field dependency evaluated without running a workflow or arbitrary expression.</summary>
public sealed record InteractionCondition
{
    public required string FieldId { get; init; }
    public required InteractionValue EqualsValue { get; init; }
}

/// <summary>A stable field, content element, or layout container.</summary>
public sealed record InteractionElement
{
    public required string Id { get; init; }
    public InteractionElementKind Kind { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public byte[]? ImageData { get; init; }
    public InteractionValue? DefaultValue { get; init; }
    public bool Required { get; init; }
    public bool Sensitive { get; init; }
    public int MaxLength { get; init; } = 4096;
    public decimal? Minimum { get; init; }
    public decimal? Maximum { get; init; }
    public string[] Options { get; init; } = [];
    public InteractionCondition? VisibleWhen { get; init; }
    public InteractionCondition? EnabledWhen { get; init; }
    public InteractionCondition? RequiredWhen { get; init; }
    public InteractionElement[] Children { get; init; } = [];
}

/// <summary>A reusable form schema. Field IDs remain stable when labels change.</summary>
public sealed record InteractionForm
{
    public string Title { get; init; } = string.Empty;
    public required InteractionElement Content { get; init; }
    public string? SubmitLabel { get; init; }
    public string? CancelLabel { get; init; }
}

/// <summary>An individual request owned by one workflow invocation.</summary>
public sealed record InteractionRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required InteractionForm Form { get; init; }
    public string Source { get; init; } = string.Empty;
    public TimeSpan? Timeout { get; init; }
}

public enum InteractionOutcome { Submitted, Cancelled, TimedOut }

/// <summary>Only submitted results carry data. String rendering never reveals form values.</summary>
public sealed record InteractionResult
{
    public InteractionOutcome Outcome { get; init; }
    public IReadOnlyDictionary<string, InteractionValue> Values { get; init; } = new Dictionary<string, InteractionValue>();
    public override string ToString() => Outcome.ToString();
}