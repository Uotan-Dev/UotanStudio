using UotanStudio.Projects.Interactions;

namespace UotanStudio.Services.Interactions;

/// <summary>Validates bounded form schemas and owns one invocation's mutable values.</summary>
public sealed class InteractionFormState
{
    private readonly Dictionary<string, InteractionValue> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InteractionElement> _elements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _parents = new(StringComparer.Ordinal);
    private long _imageBytes;

    public InteractionFormState(InteractionForm form)
    {
        Form = form;
        if (form.Title.Length > 16_384 || form.SubmitLabel?.Length > 1024 || form.CancelLabel?.Length > 1024)
        {
            throw new InvalidDataException("Form labels exceed their size limits.");
        }
        Visit(form.Content, null, 0);
        foreach (var element in _elements.Values)
        {
            foreach (var condition in new[] { element.VisibleWhen, element.EnabledWhen, element.RequiredWhen })
            {
                if (condition is not null && (!_elements.TryGetValue(condition.FieldId, out var target) || !IsField(target)))
                {
                    throw new InvalidDataException("A form condition references a missing field.");
                }
            }
        }
    }

    public InteractionForm Form { get; }
    public IEnumerable<InteractionElement> Fields => _elements.Values.Where(IsField);
    public event EventHandler? Changed;
    public InteractionValue GetValue(string id) => _values.GetValueOrDefault(id) ?? new InteractionValue();

    public void SetValue(string id, InteractionValue value)
    {
        if (!_elements.TryGetValue(id, out var element) || !IsField(element))
        {
            throw new InvalidDataException("Unknown form field.");
        }
        _values[id] = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsVisible(InteractionElement element) => Matches(element.VisibleWhen) && ParentAllows(element, false);
    public bool IsEnabled(InteractionElement element) => IsVisible(element) && Matches(element.EnabledWhen) && ParentAllows(element, true);

    public IReadOnlyDictionary<string, string> Validate()
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in Fields.Where(IsEnabled))
        {
            var value = GetValue(field.Id);
            var required = field.Required || field.RequiredWhen is not null && Matches(field.RequiredWhen);
            var error = ValidateValue(field, value, required);
            if (error is not null)
            {
                errors[field.Id] = error;
            }
        }
        return errors;
    }

    public InteractionResult Submit()
    {
        if (Validate().Count != 0)
        {
            throw new InvalidOperationException("The form has validation errors.");
        }
        return new InteractionResult
        {
            Outcome = InteractionOutcome.Submitted,
            Values = Fields.Where(IsEnabled).ToDictionary(field => field.Id, field => GetValue(field.Id), StringComparer.Ordinal)
        };
    }

    public static bool IsField(InteractionElement element) => element.Kind is >= InteractionElementKind.TextField and <= InteractionElementKind.SavePathField;

    private void Visit(InteractionElement element, string? parent, int depth)
    {
        if (depth > 16 || _elements.Count >= 256 || string.IsNullOrWhiteSpace(element.Id) || element.Id.Length > 128 || !_elements.TryAdd(element.Id, element))
        {
            throw new InvalidDataException("The form exceeds its limits or contains duplicate element IDs.");
        }
        _imageBytes += element.ImageData?.Length ?? 0;
        var container = element.Kind is InteractionElementKind.Row or InteractionElementKind.Column or InteractionElementKind.Group or InteractionElementKind.Pages;
        if (!Enum.IsDefined(element.Kind) || element.MaxLength is < 1 or > 1_048_576 || element.Options.Length > 1000 || element.Options.Any(option => option.Length > 4096)
            || element.Options.Distinct(StringComparer.Ordinal).Count() != element.Options.Length
            || element.Label.Length > 16_384 || element.Description.Length > 65_536 || element.Minimum > element.Maximum
            || element.ImageData?.Length > 8 * 1024 * 1024 || _imageBytes > 16 * 1024 * 1024
            || !container && element.Children.Length != 0 || element.Kind == InteractionElementKind.Image && element.ImageData is not { Length: > 0 })
        {
            throw new InvalidDataException("Invalid form element constraints.");
        }
        _parents[element.Id] = parent;
        if (IsField(element))
        {
            _values[element.Id] = element.DefaultValue ?? element.Kind switch
            {
                InteractionElementKind.BooleanField => new InteractionValue { Boolean = false },
                InteractionElementKind.MultiChoiceField => new InteractionValue { Selection = [] },
                InteractionElementKind.NumberField => new InteractionValue(),
                _ => new InteractionValue { Text = string.Empty }
            };
        }
        foreach (var child in element.Children)
        {
            Visit(child, element.Id, depth + 1);
        }
    }

    private bool ParentAllows(InteractionElement element, bool enabled)
    {
        var parent = _parents[element.Id];
        return parent is null || (enabled ? IsEnabled(_elements[parent]) : IsVisible(_elements[parent]));
    }

    private bool Matches(InteractionCondition? condition)
    {
        if (condition is null)
        {
            return true;
        }
        var actual = GetValue(condition.FieldId);
        var expected = condition.EqualsValue;
        return actual.Text == expected.Text && actual.Number == expected.Number && actual.Boolean == expected.Boolean
            && (actual.Selection ?? []).SequenceEqual(expected.Selection ?? [], StringComparer.Ordinal);
    }

    private static string? ValidateValue(InteractionElement field, InteractionValue value, bool required)
    {
        if (value.Selection is not null && field.Kind != InteractionElementKind.MultiChoiceField
            || value.Boolean is not null && field.Kind != InteractionElementKind.BooleanField
            || value.Number is not null && field.Kind != InteractionElementKind.NumberField
            || value.Text is not null && field.Kind is InteractionElementKind.BooleanField or InteractionElementKind.MultiChoiceField)
        {
            return "Type";
        }
        switch (field.Kind)
        {
            case InteractionElementKind.BooleanField:
                return required && value.Boolean != true ? "Required" : null;
            case InteractionElementKind.NumberField:
                if (value.Number is not { } number)
                {
                    return required || !string.IsNullOrEmpty(value.Text) ? "Number" : null;
                }
                return number < field.Minimum || number > field.Maximum ? "Range" : null;
            case InteractionElementKind.MultiChoiceField:
                var selections = value.Selection ?? [];
                return selections.Length == 0 && required ? "Required"
                    : selections.Length > field.Options.Length || selections.Distinct(StringComparer.Ordinal).Count() != selections.Length
                    || selections.Any(item => !field.Options.Contains(item, StringComparer.Ordinal)) ? "Choice" : null;
            default:
                var text = value.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    return required ? "Required" : null;
                }
                if (text.Length > field.MaxLength)
                {
                    return "Length";
                }
                if (field.Kind == InteractionElementKind.ChoiceField && !field.Options.Contains(text, StringComparer.Ordinal))
                {
                    return "Choice";
                }
                if (field.Kind == InteractionElementKind.FileField && !File.Exists(text))
                {
                    return "File";
                }
                if (field.Kind == InteractionElementKind.DirectoryField && !Directory.Exists(text))
                {
                    return "Directory";
                }
                if (field.Kind == InteractionElementKind.SavePathField && (text.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || string.IsNullOrEmpty(Path.GetFileName(text))))
                {
                    return "Path";
                }
                return null;
        }
    }
}