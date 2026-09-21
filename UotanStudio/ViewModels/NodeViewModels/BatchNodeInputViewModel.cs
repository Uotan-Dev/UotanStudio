using System.Globalization;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Represents an input shared by every currently selected node.
/// </summary>
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

    /// <summary>Input port title shared by the selected nodes.</summary>
    public string Title { get; }

    /// <summary>Input port type shared by the selected nodes.</summary>
    public NodePortType PortType { get; }

    /// <summary>Localized input port title.</summary>
    public string LocalizedTitle => LanguageService.Instance.GetString("Node_Conn_" + Title.Replace(" ", string.Empty, StringComparison.Ordinal));

    /// <summary>Whether the selected nodes have different values for this input.</summary>
    public bool IsMixed { get; private set; }

    /// <summary>Whether the input is text or a path.</summary>
    public bool IsTextOrPathPort => PortType is NodePortType.Text or NodePortType.Path;

    /// <summary>Whether the input is numeric.</summary>
    public bool IsNumberPort => PortType == NodePortType.Number;

    /// <summary>Whether the input is boolean.</summary>
    public bool IsBooleanPort => PortType == NodePortType.Boolean;

    /// <summary>Raw value used by the batch editor.</summary>
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

    /// <summary>Text/path value projected for a text box.</summary>
    public string? TextValue
    {
        get => Value as string ?? Convert.ToString(Value, CultureInfo.InvariantCulture);
        set => Value = value;
    }

    /// <summary>Numeric value projected for a numeric editor.</summary>
    public double NumberValue
    {
        get => NodeDataUtil.ToDouble(Value, 0);
        set => Value = value;
    }

    /// <summary>Boolean value projected for a toggle editor.</summary>
    public bool BooleanValue
    {
        get => NodeDataUtil.ToBool(Value, false);
        set => Value = value;
    }
}