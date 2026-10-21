using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;
using UotanStudio.Services.Projects.Schemes;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// One selectable scheme input type with a localized caption for the editor combo box.
/// <para>编辑器下拉框使用的可选 Scheme 输入类型及其本地化标题。</para>
/// </summary>
/// <param name="Type">The declared input type. <para>声明的输入类型。</para></param>
/// <param name="DisplayName">Localized caption. <para>本地化标题。</para></param>
public sealed record SchemeInputTypeOption(SchemeInputType Type, string DisplayName)
{
    /// <summary>
    /// All scheme input types in declaration order.
    /// <para>按声明顺序排列的全部 Scheme 输入类型。</para>
    /// </summary>
    /// <returns>One option per <see cref="SchemeInputType"/> value.
    /// <para>每个 <see cref="SchemeInputType"/> 值一个选项。</para></returns>
    public static IReadOnlyList<SchemeInputTypeOption> CreateAll()
    {
        var language = LanguageService.Instance;
        return
        [
            new(SchemeInputType.Text, language.GetString("Node_PortType_Text")),
            new(SchemeInputType.Path, language.GetString("Node_PortType_Path")),
            new(SchemeInputType.Directory, language.GetString("Node_PortType_PatchWorkspace")),
            new(SchemeInputType.Boolean, language.GetString("Node_PortType_Boolean")),
            new(SchemeInputType.Number, language.GetString("Node_PortType_Number")),
            new(SchemeInputType.Choice, language.GetString("Node_PortType_List"))
        ];
    }
}

/// <summary>
/// Editable view of one declared scheme input definition.
/// <para>单个声明 Scheme 输入定义的可编辑视图。</para>
/// </summary>
public sealed partial class SchemeInputEditorViewModel : ObservableObject
{
    /// <summary>
    /// Creates an editor row from a declared input.
    /// <para>按声明输入创建编辑行。</para>
    /// </summary>
    /// <param name="definition">The declaration to edit; must not be <see langword="null"/>.
    /// <para>要编辑的声明；不能为 <see langword="null"/>。</para></param>
    public SchemeInputEditorViewModel(SchemeInputDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _id = definition.Id;
        _name = definition.Name;
        _type = definition.Type;
        _required = definition.Required;
        _sensitive = definition.Sensitive;
        _defaultValue = definition.Default ?? string.Empty;
        _choicesText = string.Join(Environment.NewLine, definition.Choices);
    }

    /// <summary>
    /// Stable input identifier.
    /// <para>稳定的输入标识符。</para>
    /// </summary>
    [ObservableProperty]
    private string _id;

    /// <summary>
    /// Display name of the input.
    /// <para>输入的显示名称。</para>
    /// </summary>
    [ObservableProperty]
    private string _name;

    /// <summary>
    /// Declared value type.
    /// <para>声明的取值类型。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTypeOption))]
    private SchemeInputType _type;

    /// <summary>
    /// Whether the input must be supplied before a run.
    /// <para>输入是否必须在运行前提供。</para>
    /// </summary>
    [ObservableProperty]
    private bool _required;

    /// <summary>
    /// Whether the value must be masked.
    /// <para>取值是否必须遮蔽。</para>
    /// </summary>
    [ObservableProperty]
    private bool _sensitive;

    /// <summary>
    /// Optional default value.
    /// <para>可选的默认值。</para>
    /// </summary>
    [ObservableProperty]
    private string _defaultValue;

    /// <summary>
    /// Allowed values for a choice input, one per line.
    /// <para>可选输入的允许取值，每行一个。</para>
    /// </summary>
    [ObservableProperty]
    private string _choicesText;

    /// <summary>
    /// Selectable input types, rebuilt with the current language.
    /// <para>可选输入类型，按当前语言重新生成。</para>
    /// </summary>
    public IReadOnlyList<SchemeInputTypeOption> TypeOptions => SchemeInputTypeOption.CreateAll();

    /// <summary>
    /// Selected input type option, projected onto <see cref="Type"/>.
    /// <para>选中的输入类型选项，投影到 <see cref="Type"/>。</para>
    /// </summary>
    public SchemeInputTypeOption? SelectedTypeOption
    {
        get => TypeOptions.FirstOrDefault(option => option.Type == Type);
        set
        {
            if (value is not null && value.Type != Type)
            {
                Type = value.Type;
            }
        }
    }

    /// <summary>
    /// Whether the row shows the free-form choices editor.
    /// <para>该行是否显示自由格式的可选值编辑器。</para>
    /// </summary>
    public bool IsChoiceInput => Type == SchemeInputType.Choice;

    /// <summary>
    /// Projects the row back to a persistence definition.
    /// <para>把编辑行投影回持久化定义。</para>
    /// </summary>
    /// <returns>The edited definition. <para>编辑后的定义。</para></returns>
    public SchemeInputDefinition ToDefinition() => new()
    {
        Id = Id.Trim(),
        Name = Name.Trim(),
        Type = Type,
        Required = Required,
        Sensitive = Sensitive,
        Default = string.IsNullOrWhiteSpace(DefaultValue) ? null : DefaultValue,
        Choices = ParseChoices(ChoicesText)
    };

    private static List<string> ParseChoices(string text)
    {
        List<string> choices = [];
        foreach (var line in text.Split(['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var part in line.Split([',', ';'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!choices.Contains(part, StringComparer.Ordinal))
                {
                    choices.Add(part);
                }
            }
        }
        return choices;
    }
}