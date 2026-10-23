using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Projects.Schemes;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Operator-side view of one declared scheme input: the value bound to the form control plus the
/// visibility flags and required/number validation used by the read-only operator window.
/// <para>Scheme 操作员侧的声明输入视图：表单控件绑定的取值，以及只读操作员窗口使用的可见性标志与必填/数值校验。</para>
/// </summary>
public sealed partial class SchemeInputViewModel : ObservableObject
{
    /// <summary>
    /// Creates the operator input view and seeds the value from the declared default.
    /// <para>创建操作员输入视图，并用声明的默认值初始化取值。</para>
    /// </summary>
    /// <param name="definition">The declared input; must not be <see langword="null"/>.
    /// <para>声明的输入；不能为 <see langword="null"/>。</para></param>
    public SchemeInputViewModel(SchemeInputDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
        Value = definition.Default ?? string.Empty;
    }

    /// <summary>
    /// The declared input this view presents.
    /// <para>本视图呈现的声明输入。</para>
    /// </summary>
    public SchemeInputDefinition Definition { get; }

    /// <summary>
    /// Stable input identifier used as the run option key.
    /// <para>作为运行选项键使用的稳定输入标识符。</para>
    /// </summary>
    public string Id => Definition.Id;

    /// <summary>
    /// Display caption, falling back to <see cref="Id"/> when the declaration has no name.
    /// <para>显示标题；声明没有名称时回退到 <see cref="Id"/>。</para>
    /// </summary>
    public string Name => string.IsNullOrWhiteSpace(Definition.Name) ? Definition.Id : Definition.Name;

    /// <summary>
    /// Declared value type.
    /// <para>声明的取值类型。</para>
    /// </summary>
    public SchemeInputType Type => Definition.Type;

    /// <summary>
    /// Whether the input must be supplied before a run.
    /// <para>输入是否必须在运行前提供。</para>
    /// </summary>
    public bool Required => Definition.Required;

    /// <summary>
    /// Whether the value must be masked in the form.
    /// <para>取值是否必须在表单中遮蔽。</para>
    /// </summary>
    public bool Sensitive => Definition.Sensitive;

    /// <summary>
    /// Allowed values for a choice input; empty for every other type.
    /// <para>可选输入的允许取值；其他类型为空列表。</para>
    /// </summary>
    public IReadOnlyList<string> Choices => Definition.Choices;

    /// <summary>
    /// Declared default text; empty when the declaration has no default.
    /// <para>声明的默认文本；没有默认值时为空白。</para>
    /// </summary>
    public string DefaultValue => Definition.Default ?? string.Empty;

    /// <summary>
    /// Current form value.
    /// <para>当前表单取值。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveValue))]
    [NotifyPropertyChangedFor(nameof(BooleanValue))]
    [NotifyPropertyChangedFor(nameof(IsMissingRequiredValue))]
    [NotifyPropertyChangedFor(nameof(HasInvalidNumber))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    private string _value = string.Empty;

    /// <summary>
    /// Whether the input renders as a plain text box.
    /// <para>输入是否呈现为普通文本框。</para>
    /// </summary>
    public bool IsPlainTextInput => (IsText || IsNumber) && !Sensitive;

    /// <summary>
    /// Whether the input renders as a masked text box.
    /// <para>输入是否呈现为遮蔽文本框。</para>
    /// </summary>
    public bool IsMaskedTextInput => (IsText || IsNumber) && Sensitive;

    /// <summary>
    /// Whether the input renders as a browsable plain path box.
    /// <para>输入是否呈现为可浏览的普通路径框。</para>
    /// </summary>
    public bool IsPlainBrowseInput => IsBrowseable && !Sensitive;

    /// <summary>
    /// Whether the input renders as a browsable masked path box.
    /// <para>输入是否呈现为可浏览的遮蔽路径框。</para>
    /// </summary>
    public bool IsMaskedBrowseInput => IsBrowseable && Sensitive;

    /// <summary>
    /// Whether the input renders as a check box.
    /// <para>输入是否呈现为复选框。</para>
    /// </summary>
    public bool IsBooleanInput => Type == SchemeInputType.Boolean;

    /// <summary>
    /// Whether the input renders as a choice combo box.
    /// <para>输入是否呈现为选项下拉框。</para>
    /// </summary>
    public bool IsChoiceInput => Type == SchemeInputType.Choice;

    /// <summary>
    /// Whether the input is a free-form text value.
    /// <para>输入是否为自由文本。</para>
    /// </summary>
    public bool IsText => Type == SchemeInputType.Text;

    /// <summary>
    /// Whether the input is a numeric value.
    /// <para>输入是否为数值。</para>
    /// </summary>
    public bool IsNumber => Type == SchemeInputType.Number;

    /// <summary>
    /// Whether the input selects a file path.
    /// <para>输入是否选择文件路径。</para>
    /// </summary>
    public bool IsPath => Type == SchemeInputType.Path;

    /// <summary>
    /// Whether the input selects a directory path.
    /// <para>输入是否选择目录路径。</para>
    /// </summary>
    public bool IsDirectory => Type == SchemeInputType.Directory;

    /// <summary>
    /// Whether the input carries a file or directory path.
    /// <para>输入是否承载文件或目录路径。</para>
    /// </summary>
    public bool IsBrowseable => IsPath || IsDirectory;

    /// <summary>
    /// Check-box projection of the value; assigning writes <c>"true"</c> or <c>"false"</c>.
    /// <para>取值的复选框投影；赋值时写入 <c>"true"</c> 或 <c>"false"</c>。</para>
    /// </summary>
    public bool BooleanValue
    {
        get => IsTruthy(Value);
        set => Value = value ? "true" : "false";
    }

    /// <summary>
    /// Value supplied to the run: the current text, or the declared default when the form is blank.
    /// <para>提交给运行的取值：当前文本，表单为空时使用声明的默认值。</para>
    /// </summary>
    public string EffectiveValue => string.IsNullOrWhiteSpace(Value) ? DefaultValue : Value;

    /// <summary>
    /// Whether a required input has neither a current nor a default value.
    /// <para>必填输入既无当前值也无默认值。</para>
    /// </summary>
    public bool IsMissingRequiredValue => Required && string.IsNullOrWhiteSpace(EffectiveValue);

    /// <summary>
    /// Whether a number input carries text that is not a valid number.
    /// <para>数值输入是否携带非有效数字的文本。</para>
    /// </summary>
    public bool HasInvalidNumber => IsNumber && !string.IsNullOrWhiteSpace(Value)
        && !double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// Whether the input currently fails required or numeric validation.
    /// <para>输入当前是否未通过必填或数值校验。</para>
    /// </summary>
    public bool HasError => IsMissingRequiredValue || HasInvalidNumber;

    /// <summary>
    /// Localized explanation of a missing required value; empty when the input is acceptable.
    /// <para>缺少必填值的本地化说明；输入可接受时为空。</para>
    /// </summary>
    public string ValidationMessage => IsMissingRequiredValue
        ? string.Format(CultureInfo.CurrentCulture, LanguageService.Instance.GetString("Node_Graph_Required"), Id)
        : string.Empty;

    /// <summary>
    /// Stores a picked file or directory path as the value.
    /// <para>把选择的文件或目录路径写入取值。</para>
    /// </summary>
    /// <param name="path">The picked path, or <see langword="null"/> to ignore the pick.
    /// <para>选择的路径；为 <see langword="null"/> 时忽略本次选择。</para></param>
    public void ApplyPath(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            Value = path;
        }
    }

    /// <summary>
    /// Resolves the value supplied to <c>SchemeRunner</c>.
    /// <para>解析提交给 <c>SchemeRunner</c> 的取值。</para>
    /// </summary>
    /// <returns>The current text, or the declared default when the form is blank.
    /// <para>当前文本；表单为空时为声明的默认值。</para></returns>
    public string GetRunValue() => EffectiveValue;

    private static bool IsTruthy(string text) => text.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        _ => false
    };
}