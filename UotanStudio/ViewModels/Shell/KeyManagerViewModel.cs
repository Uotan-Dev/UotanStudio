using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services.Keys;

namespace UotanStudio.ViewModels.Shell;

/// <summary>Key library selection, background operations and user-visible operation results.</summary>
public partial class KeyManagerViewModel : ViewModelBase
{
    private readonly KeyLibraryService _library;
    public ObservableCollection<ManagedKey> Keys { get; } = [];
    public string[] Kinds { get; } = ["ADB", "HDC", "Payload"];
    public int[] PayloadSizes => KindIndex switch { 0 => [2048], 1 => [3072], _ => [2048, 4096] };
    public string LibraryPath => _library.Root;
    [ObservableProperty] private string _name = "My key";
    [ObservableProperty] private int _kindIndex;
    [ObservableProperty] private int _payloadBits = 2048;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private ManagedKey? _selectedKey;
    [ObservableProperty] private string _publicKey = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    [ObservableProperty] private bool _confirmRemoval;
    [ObservableProperty] private bool _confirmDeletion;
    public bool IsIdle => !IsBusy;
    public bool IsPayload => KindIndex == 2;
    public bool HasSelection => SelectedKey is not null;
    public string SelectedPath => SelectedKey is { } key ? _library.PrivatePath(key) : "";
    private IReadOnlyList<ManagedKey> _all = [];

    public KeyManagerViewModel() : this(new KeyLibraryService()) { }
    public KeyManagerViewModel(KeyLibraryService library) => _library = library;

    partial void OnKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPayload));
        OnPropertyChanged(nameof(PayloadSizes));
        PayloadBits = value == 1 ? 3072 : 2048;
    }
    partial void OnSearchChanged(string value) => Filter();
    partial void OnSelectedKeyChanged(ManagedKey? value)
    {
        ConfirmRemoval = false;
        ConfirmDeletion = false;
        RenameText = value?.Name ?? "";
        PublicKey = "";
        try { if (value is not null) PublicKey = _library.PublicText(value); }
        catch (Exception ex) { Status = ex.Message; }
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedPath));
    }

    private void Filter()
    {
        var id = SelectedKey?.Id;
        Keys.Clear();
        foreach (var key in _all.Where(k => string.IsNullOrWhiteSpace(Search) ||
                     (k.Name + " " + k.Summary + " " + k.Fingerprint).Contains(Search, StringComparison.OrdinalIgnoreCase))) Keys.Add(key);
        SelectedKey = Keys.FirstOrDefault(k => k.Id == id) ?? Keys.FirstOrDefault();
    }

    /// <summary>Starts a new key without modifying the selected entry.</summary>
    [RelayCommand]
    private void NewKey()
    {
        if (IsBusy) return;
        SelectedKey = null;
        Name = "";
        Password = "";
        Status = "";
    }

    [RelayCommand]
    private Task DeleteAsync() => ConfirmDeletion && SelectedKey is { } key ? DeleteKeyAsync(key) : Task.CompletedTask;

    // The caller confirms this specific row before deletion, independently of the current selection.
    // 调用方先确认具体条目，删除目标不随当前选中项变化。
    internal Task DeleteKeyAsync(ManagedKey key) => RunAsync(async () =>
    {
        await Task.Run(() => _library.Delete(key));
        _all = await Task.Run(_library.List);
        Filter();
    });

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        _all = await Task.Run(_library.List);
        Filter();
    });

    [RelayCommand]
    private Task GenerateAsync()
    {
        var name = Name;
        var kind = (ManagedKeyKind)KindIndex;
        var bits = kind switch { ManagedKeyKind.Adb => 2048, ManagedKeyKind.Hdc => 3072, _ => PayloadBits };
        return AddAsync(() => _library.Generate(name, kind, bits));
    }

    public Task ImportAsync(string path)
    {
        var name = Name;
        var kind = (ManagedKeyKind)KindIndex;
        var password = Password;
        Password = "";
        return AddAsync(() => _library.Import(name, kind, path, password));
    }

    private Task AddAsync(Func<ManagedKey> create) => RunAsync(async () =>
    {
        var key = await Task.Run(create);
        _all = await Task.Run(_library.List);
        Search = "";
        Filter();
        SelectedKey = Keys.First(k => k.Id == key.Id);
    });

    [RelayCommand]
    private Task RenameAsync() => RunAsync(async () =>
    {
        if (SelectedKey is not { } key) return;
        var name = RenameText;
        await Task.Run(() => _library.Rename(key, name));
        _all = await Task.Run(_library.List);
        Filter();
    });

    [RelayCommand]
    private Task RemoveAsync() => RunAsync(async () =>
    {
        if (!ConfirmRemoval || SelectedKey is not { } key) return;
        await Task.Run(() => _library.Remove(key));
        _all = await Task.Run(_library.List);
        Filter();
    });

    public Task ExportAsync(string destination, bool includePrivate) => RunAsync(async () =>
    {
        if (SelectedKey is not { } key) return;
        var path = await Task.Run(() => _library.Export(key, destination, includePrivate));
        Status = path;
    });

    public async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "";
        try { await action(); }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }
}
