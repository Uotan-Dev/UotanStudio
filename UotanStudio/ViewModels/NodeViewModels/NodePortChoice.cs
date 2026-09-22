using System.ComponentModel;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>A stable persisted option with a separately localized display label.</summary>
public sealed class NodePortChoice(string value) : INotifyPropertyChanged
{
    /// <summary>Nonlocalized value stored in workflow snapshots.</summary>
    public string Value { get; } = value;

    /// <summary>Localized option label.</summary>
    public string DisplayName => LanguageService.Instance.GetString("Node_Choice_" + Value.Replace('-', '_'));

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void RefreshLocalization() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
}