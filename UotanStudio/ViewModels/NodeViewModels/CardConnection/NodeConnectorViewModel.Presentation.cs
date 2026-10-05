namespace UotanStudio.ViewModels.NodeViewModels;

public partial class NodeConnectorViewModel
{
    /// <summary>Whether this parameter is hidden until advanced options are requested.</summary>
    public bool IsAdvanced { get; set; }

    /// <summary>Whether this input configures an operation rather than carrying a resource.</summary>
    public bool IsConfiguration { get; set; }

    /// <summary>Schema-owned relevance rule evaluated against the owning node.</summary>
    public Func<FlowNodeViewModel, bool>? IsVisibleWhen { get; set; }

    /// <summary>Connected upstream source shown instead of an unused default editor.</summary>
    public string SourceDescription
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HasConnectedSource));
                OnPropertyChanged(nameof(UsesTextEditor));
                OnPropertyChanged(nameof(CanEditChoice));
                OnPropertyChanged(nameof(CanEditNumber));
                OnPropertyChanged(nameof(CanEditBoolean));
                OnPropertyChanged(nameof(CanBrowsePath));
            }
        }
    } = string.Empty;

    public bool HasConnectedSource => SourceDescription.Length > 0;
    public bool CanEditChoice => HasChoices && !HasConnectedSource;
    public bool CanEditNumber => IsNumberPort && !HasConnectedSource;
    public bool CanEditBoolean => IsBooleanPort && !HasConnectedSource;
    public bool CanBrowsePath => IsPathPort && !HasConnectedSource;
}