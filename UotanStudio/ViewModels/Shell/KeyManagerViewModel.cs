using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.Services.Keys;

namespace UotanStudio.ViewModels.Shell;

/// <summary>Key library selection, background operations and user-visible operation results.</summary>
public partial class KeyManagerViewModel : ViewModelBase
{
    private readonly KeyLibraryService _library;
    // Settings access seams; tests inject isolated delegates to avoid touching real user settings.
    // 设置访问接缝；测试注入隔离委托以避免触碰真实用户设置。
    private readonly Func<AppSettings> _loadSettings = () => SettingsPersistenceService.Instance.LoadSettings();
    private readonly Func<string, string?, bool> _updateDefaultKeyId = SettingsPersistenceService.Instance.UpdateDefaultKeyId;
    // Runtime-identity fingerprint seam; tests inject isolated keys to avoid touching real runtime files.
    // 运行时身份指纹接缝；测试注入隔离密钥以避免触碰真实运行时文件。
    private readonly Func<ManagedKeyKind, string?> _runtimeFingerprint = KeyLibraryService.GetRuntimeIdentityFingerprint;
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
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsSelectedDefault), nameof(CanSetCurrentDefault))] private string _defaultSignatureIds = "";
    public bool IsIdle => !IsBusy;
    public bool IsPayload => KindIndex == 2;
    /// <summary>Whether the UI is showing the APK key purpose.</summary>
    /// <summary>当前是否选择了 APK 密钥用途。</summary>
    public bool IsApkKey => KindIndex == 3;
    /// <summary>Whether the UI is showing the APK certificate purpose.</summary>
    /// <summary>当前是否选择了 APK 证书用途。</summary>
    public bool IsApkCertificate => KindIndex == 4;
    /// <summary>Whether the selected purpose is backed by the managed key library.</summary>
    /// <summary>当前用途是否由密钥库后端支持。</summary>
    public bool IsLibraryPurpose => KindIndex is >= 0 and < 3;
    /// <summary>Whether the key-size selector can be changed for the selected purpose.</summary>
    /// <summary>当前用途是否允许调整密钥位数。</summary>
    public bool IsBitsEditable => IsPayload || IsApkKey;
    public bool HasSelection => SelectedKey is not null;
    public string SelectedPath => SelectedKey is { } key ? _library.PrivatePath(key) : "";
    public bool HasCertificate => SelectedKey is { Kind: ManagedKeyKind.Payload };
    public bool IsSelectedDefault => SelectedKey is { } k && IsDefault(k);
    public bool CanSetCurrentDefault => SelectedKey is { Kind: not ManagedKeyKind.Payload };
    private IReadOnlyList<ManagedKey> _all = [];

    public KeyManagerViewModel() : this(new KeyLibraryService()) { }
    public KeyManagerViewModel(KeyLibraryService library)
    {
        _library = library;
        RebuildDefaultIds();
    }

    /// <summary>Test-only constructor with isolated settings delegates.</summary>
    /// <summary>仅供测试的构造函数，注入隔离的设置委托。</summary>
    internal KeyManagerViewModel(KeyLibraryService library, Func<AppSettings> loadSettings,
        Func<string, string?, bool> updateDefaultKeyId, Func<ManagedKeyKind, string?>? runtimeFingerprint = null)
    {
        _library = library;
        _loadSettings = loadSettings;
        _updateDefaultKeyId = updateDefaultKeyId;
        if (runtimeFingerprint is not null) _runtimeFingerprint = runtimeFingerprint;
        RebuildDefaultIds();
    }

    /// <summary>Re-reads the persisted default key ids and refreshes the badge binding source. A persisted id that is empty or no longer exists in the library falls back to the runtime identity matched by fingerprint, so the auto-imported entry is shown as in use.</summary>
    /// <summary>重新读取已持久化的默认密钥标识并刷新徽标绑定源。持久化 Id 为空或已不存在于库中时回退按指纹匹配运行时身份，使自动导入的条目显示为当前使用。</summary>
    private void RebuildDefaultIds()
    {
        var settings = _loadSettings();
        string? Resolve(string kind, string? savedId)
        {
            var managedKind = kind == "Adb" ? ManagedKeyKind.Adb : ManagedKeyKind.Hdc;
            if (!string.IsNullOrEmpty(savedId) && _all.Any(k => k.Kind == managedKind && k.Id == savedId))
                return kind + ":" + savedId;
            var fingerprint = _runtimeFingerprint(managedKind);
            if (fingerprint is null || _library.FindByFingerprint(managedKind, fingerprint) is not { } key) return null;
            return kind + ":" + key.Id;
        }
        DefaultSignatureIds = string.Join(" ",
            [.. new[] { Resolve("Adb", settings.AdbDefaultKeyId), Resolve("Hdc", settings.HdcDefaultKeyId) }.Where(part => part is not null)]);
    }

    /// <summary>True when the key is the current default signature for its ADB/HDC purpose.</summary>
    /// <summary>当密钥是其 ADB/HDC 用途的当前默认签名时返回 true。</summary>
    private bool IsDefault(ManagedKey key) =>
        key.Kind is ManagedKeyKind.Adb or ManagedKeyKind.Hdc &&
        DefaultSignatureIds.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(key.Kind + ":" + key.Id, StringComparer.Ordinal);

    partial void OnDefaultSignatureIdsChanged(string value) => OnPropertyChanged(nameof(IsSelectedDefault));

    partial void OnKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPayload));
        OnPropertyChanged(nameof(IsApkKey));
        OnPropertyChanged(nameof(IsApkCertificate));
        OnPropertyChanged(nameof(IsLibraryPurpose));
        OnPropertyChanged(nameof(IsBitsEditable));
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
        OnPropertyChanged(nameof(HasCertificate));
        OnPropertyChanged(nameof(SelectedPath));
        OnPropertyChanged(nameof(IsSelectedDefault));
        OnPropertyChanged(nameof(CanSetCurrentDefault));
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
        if (IsDefault(key))
        {
            var cleared = await Task.Run(() => _updateDefaultKeyId(
                key.Kind == ManagedKeyKind.Adb ? "Adb" : "Hdc", null));
            if (!cleared) Status = GetStringOrFallback("Keys_DefaultSaveFailed", "Failed to save the default key setting.");
        }
        await Task.Run(() => _library.Delete(key));
        _all = await Task.Run(_library.List);
        Filter();
        RebuildDefaultIds();
    });

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        _all = await Task.Run(_library.List);
        Filter();
        RebuildDefaultIds();
    });

    [RelayCommand]
    private Task GenerateAsync()
    {
        // APK key and certificate entries are UI-only until their dedicated backend is added.
        // APK 密钥和证书条目在专用后端接入前仅用于界面展示。
        if (!IsLibraryPurpose) return Task.CompletedTask;
        var name = Name;
        var kind = (ManagedKeyKind)KindIndex;
        var bits = kind switch { ManagedKeyKind.Adb => 2048, ManagedKeyKind.Hdc => 3072, _ => PayloadBits };
        return AddAsync(() => _library.Generate(name, kind, bits));
    }

    public Task ImportAsync(string path)
    {
        if (!IsLibraryPurpose) return Task.CompletedTask;
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
        RebuildDefaultIds();
    });

    /// <summary>Publishes the selected ADB/HDC key as the current runtime default signature.</summary>
    /// <summary>将选中的 ADB/HDC 密钥发布为当前运行时默认签名。</summary>
    [RelayCommand]
    private Task SetCurrentDefaultAsync()
    {
        if (IsBusy || SelectedKey is not { Kind: not ManagedKeyKind.Payload } key) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            await Task.Run(() => _library.PublishDefault(key));
            var saved = await Task.Run(() => _updateDefaultKeyId(
                key.Kind == ManagedKeyKind.Adb ? "Adb" : "Hdc", key.Id));
            if (!saved) Status = GetStringOrFallback("Keys_DefaultSaveFailed", "Failed to save the default key setting.");
            else RebuildDefaultIds();
        });
    }

    /// <summary>Localized string with an English fallback when the resource key is missing.</summary>
    /// <summary>本地化字符串；资源缺失时回退英文。</summary>
    private static string GetStringOrFallback(string key, string fallback)
    {
        var value = LanguageService.Instance.GetString(key);
        return value == key ? fallback : value;
    }

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

    public Task ExportCertificateAsync(string destination) => RunAsync(async () =>
    {
        if (SelectedKey is not { } key) return;
        var path = await Task.Run(() =>
        {
            var certificate = _library.EnsureCertificate(key);
            var target = Path.Combine(Path.GetFullPath(destination), key.Kind.ToString().ToLowerInvariant() + "-" + key.Id + ".crt");
            File.Copy(certificate, target, false);
            return target;
        });
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
