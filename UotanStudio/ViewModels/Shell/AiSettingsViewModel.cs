using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UotanStudio.ViewModels.Shell;

/// <summary>AI connection editor state, held only in memory.<para>AI 连接编辑状态，仅保存在内存中。</para></summary>
public partial class AiSettingsViewModel : ViewModelBase
{
    [ObservableProperty] private string _apiEndpoint = string.Empty;
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string? _selectedModel;
    [ObservableProperty] private bool _isFetchingModels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApiKeyPasswordChar))]
    private bool _isApiKeyVisible;

    public char ApiKeyPasswordChar => IsApiKeyVisible ? '\0' : '*';
    public ObservableCollection<string> Models { get; } = [];

    public IRelayCommand FetchModelsCommand { get; } = new RelayCommand(() => { }, () => false);

    internal void Reset()
    {
        ApiEndpoint = string.Empty;
        ApiKey = string.Empty;
        IsApiKeyVisible = false;
        SelectedModel = null;
        Models.Clear();
        IsFetchingModels = false;
    }
}