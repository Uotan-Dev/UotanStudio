using System.Globalization;
using UotanStudio.Services;
using UotanStudio.ViewModels;

namespace UotanStudio.Workflow.Nodes;

/// <summary>Represents an input shared by every currently selected node.<para>表示当前所有选中节点共享的输入。</para></summary>
public sealed class BatchNodeInputViewModel : ViewModelBase
{
    private readonly Action<object?> _setValue;
    private object? _value;

    internal BatchNodeInputViewModel(string title, NodePortType portType, object? value,
        bool isMixed, Action<object?> setValue)
    {
        Title = title;
        PortType = portType;
        _value = value;
        IsMixed = isMixed;
        _setValue = setValue;
    }

    /// <summary>Input port title shared by the selected nodes.<para>所选节点共享的输入端口标题。</para></summary>
    /// <value>The shared port title.<para>共享的端口标题。</para></value>
    public string Title { get; }

    /// <summary>Input port type shared by the selected nodes.<para>所选节点共享的输入端口类型。</para></summary>
    /// <value>The shared port type.<para>共享的端口类型。</para></value>
    public NodePortType PortType { get; }

    /// <summary>Localized input port title.<para>本地化的输入端口标题。</para></summary>
    /// <value>The localized title string.<para>本地化的标题字符串。</para></value>
    public string LocalizedTitle => LanguageService.Instance.GetString("Node_Conn_" + Title.Replace(" ", string.Empty, StringComparison.Ordinal));

    /// <summary>Whether the selected nodes have different values for this input.<para>所选节点的该输入值是否不一致。</para></summary>
    /// <value>The mixed-value flag.<para>值不一致标记。</para></value>
    public bool IsMixed { get; private set; }

    /// <summary>Whether the input is text or a path.<para>该输入是否为文本或路径。</para></summary>
    /// <value>The text/path port flag.<para>文本/路径端口标记。</para></value>
    public bool IsTextOrPathPort => PortType is NodePortType.Text or NodePortType.Path;

    /// <summary>Whether the input is numeric.<para>该输入是否为数值。</para></summary>
    /// <value>The numeric port flag.<para>数值端口标记。</para></value>
    public bool IsNumberPort => PortType == NodePortType.Number;

    /// <summary>Whether the input is boolean.<para>该输入是否为布尔值。</para></summary>
    /// <value>The boolean port flag.<para>布尔端口标记。</para></value>
    public bool IsBooleanPort => PortType == NodePortType.Boolean;

    /// <summary>Raw shared value of the input.<para>该输入的原始共享值。</para></summary>
    /// <value>The current raw value.<para>当前的原始值。</para></value>
    public object? Value
    {
        get => _value;
        set
        {
            if (Equals(_value, value) && !IsMixed) return;
            _setValue(value);
            _value = value;
            IsMixed = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMixed));
            OnPropertyChanged(nameof(NumberValue));
            OnPropertyChanged(nameof(BooleanValue));
        }
    }

    /// <summary>Text/path value projected for a text box.<para>面向文本框投影的文本/路径值。</para></summary>
    /// <value>The value as display text.<para>以显示文本表示的值。</para></value>
    public string? TextValue
    {
        get => Value as string ?? Convert.ToString(Value, CultureInfo.InvariantCulture);
        set => Value = value;
    }

    /// <summary>Numeric value projected for a numeric editor.<para>由原始值投影出的数值。</para></summary>
    /// <value>The value converted to a number.<para>换算为数值后的值。</para></value>
    public double NumberValue
    {
        get => NodeDataUtil.ToDouble(Value, 0);
        set => Value = value;
    }

    /// <summary>Boolean value projected for a toggle editor.<para>由原始值投影出的布尔值。</para></summary>
    /// <value>The value converted to a boolean.<para>换算为布尔值后的值。</para></value>
    public bool BooleanValue
    {
        get => NodeDataUtil.ToBool(Value, false);
        set => Value = value;
    }
}