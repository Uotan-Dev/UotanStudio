using System.Collections.ObjectModel;
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
    public string[] Kinds { get; } = ["ADB", "HDC", "Payload", "JAR", "APK", "ZIP"];
    public int[] PayloadSizes => KindIndex switch
    {
        0 => [2048],
        1 => [3072],
        3 => [2048, 4096],
        _ => [2048, 4096],
    };
    public string LibraryPath => _library.Root;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private string _name = "My key";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private int _kindIndex;
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
    public bool IsPayload => KindIndex == (int)ManagedKeyKind.Payload;
    /// <summary>当前是否选择了 APK 密钥用途。</summary>
    /// <summary>Whether the UI is showing the APK certificate purpose.</summary>
    /// <summary>当前是否选择了 APK 证书用途。</summary>
    public bool IsApkCertificate => KindIndex == (int)ManagedKeyKind.Apk;
    /// <summary>Whether the UI is showing the JAR purpose (library-backed, 2048/4096 bits).</summary>
    /// <summary>当前是否选择了 JAR 用途（库后端支持，2048/4096 位）。</summary>
    public bool IsJar => KindIndex == (int)ManagedKeyKind.Jar;
    /// <summary>当前是否选择了 ZIP（OTA 包签名）用途。</summary>
    /// <summary>Whether the UI is showing the ZIP (OTA package signing) purpose.</summary>
    public bool IsZip => KindIndex == (int)ManagedKeyKind.Zip;
    /// <summary>Whether the selected purpose is backed by the managed key library.</summary>
    /// <summary>当前用途是否由密钥库后端支持。</summary>
    public bool IsLibraryPurpose => KindIndex is (int)ManagedKeyKind.Zip or < (int)ManagedKeyKind.Apk;
    /// <summary>Whether the key-size selector can be changed for the selected purpose.</summary>
    /// <summary>当前用途是否允许调整密钥位数。</summary>
    public bool IsBitsEditable => IsPayload || IsJar || IsZip;
    // ---- APK certificate form (purpose index 4) ----
    // ---- APK 证书表单（用途下标 4）----
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private string _certificateAlias = "my-release-key";
    [ObservableProperty] private string _keystorePassword = "";
    [ObservableProperty] private int _validityYears = 10;
    [ObservableProperty] private string _commonName = "Memory";
    [ObservableProperty] private string _organizationalUnit = "Android";
    [ObservableProperty] private string _organization = "Example";
    [ObservableProperty] private string _locality = "Shanghai";
    [ObservableProperty] private string _state = "Shanghai";
    [ObservableProperty] private string _countryCode = "CN";
    [ObservableProperty] private int _algorithmIndex; // 0 = RSA, 1 = EC
    [ObservableProperty] private int _keySizeIndex; // index into ApkKeySizes
    [ObservableProperty] private int _formatIndex; // ApkCertificateExportFormat
    [ObservableProperty] private string _outputDirectory = "";
    /// <summary>Size/curve options for the selected algorithm; EC curves are dynamically expanded.</summary>
    /// <summary>当前算法可选的位数/曲线；EC 曲线动态扩展。</summary>
    public int[] ApkKeySizes => AlgorithmIndex == 1 ? [256, 384, 521] : [2048, 3072, 4096];
    /// <summary>Currently selected key size / curve for the APK certificate form.</summary>
    /// <summary>APK 证书表单当前选中的位数/曲线。</summary>
    public int SelectedApkKeySize
    {
        get => KeySizeIndex >= 0 && KeySizeIndex < ApkKeySizes.Length ? ApkKeySizes[KeySizeIndex] : ApkKeySizes[0];
        set
        {
            var index = Array.IndexOf(ApkKeySizes, value);
            if (index >= 0) KeySizeIndex = index;
        }
    }
    /// <summary>Two-way bindings for the RSA/EC radio pair.</summary>
    /// <summary>RSA/EC 单选按钮的双向绑定属性。</summary>
    public bool IsRsaAlgorithm { get => AlgorithmIndex == 0; set { if (value) AlgorithmIndex = 0; } }
    public bool IsEcAlgorithm { get => AlgorithmIndex == 1; set { if (value) AlgorithmIndex = 1; } }
    /// <summary>Two-way bindings for the size/curve radio options.</summary>
    /// <summary>位数/曲线单选项的双向绑定属性。</summary>
    public bool IsKeySize2048 { get => SelectedApkKeySize == 2048; set { if (value) SelectedApkKeySize = 2048; } }
    public bool IsKeySize3072 { get => SelectedApkKeySize == 3072; set { if (value) SelectedApkKeySize = 3072; } }
    public bool IsKeySize4096 { get => SelectedApkKeySize == 4096; set { if (value) SelectedApkKeySize = 4096; } }
    public bool IsKeySizeP256 { get => SelectedApkKeySize == 256; set { if (value) SelectedApkKeySize = 256; } }
    public bool IsKeySizeP384 { get => SelectedApkKeySize == 384; set { if (value) SelectedApkKeySize = 384; } }
    public bool IsKeySizeP521 { get => SelectedApkKeySize == 521; set { if (value) SelectedApkKeySize = 521; } }
    /// <summary>Two-way bindings for the four output-format radio buttons.</summary>
    /// <summary>四个输出格式单选按钮的双向绑定属性。</summary>
    public bool IsFormatAll { get => FormatIndex == 0; set { if (value) FormatIndex = 0; } }
    public bool IsFormatJks { get => FormatIndex == 1; set { if (value) FormatIndex = 1; } }
    public bool IsFormatPkcs12 { get => FormatIndex == 2; set { if (value) FormatIndex = 2; } }
    public bool IsFormatPem { get => FormatIndex == 3; set { if (value) FormatIndex = 3; } }
    public bool IsOutputDirectoryRequired => IsApkCertificate;
    public bool HasSelection => SelectedKey is not null;
    /// <summary>
    /// <para>「生成密钥对」的必要条件：普通用途需要名称，APK 证书需要别名（或名称）；未填写完整时按钮禁用。</para>
    /// Required inputs for Generate: a name for library purposes, the alias (or name) for APK certificates.
    /// The button stays disabled until those are present.
    /// </summary>
    public bool CanGenerate => IsApkCertificate
        ? !string.IsNullOrWhiteSpace(CertificateAlias) || !string.IsNullOrWhiteSpace(Name)
        : !string.IsNullOrWhiteSpace(Name);
    /// <summary>
    /// <para>「导入」的必要条件：用途由密钥库支持且已填写名称；未填写完整时按钮禁用。</para>
    /// Required inputs for Import: a library-backed purpose and a non-empty name.
    /// </summary>
    public bool CanImport => IsLibraryPurpose && !string.IsNullOrWhiteSpace(Name);
    public string SelectedPath => SelectedKey is { } key ? _library.PrivatePath(key) : "";
    public bool HasCertificate => SelectedKey is { Kind: ManagedKeyKind.Payload or ManagedKeyKind.Jar or ManagedKeyKind.Zip };
    public bool IsSelectedDefault => SelectedKey is { } k && IsDefault(k);
    public bool CanSetCurrentDefault => SelectedKey is { Kind: ManagedKeyKind.Adb or ManagedKeyKind.Hdc };
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

    /// <summary>Re-reads the persisted default key ids and refreshes the badge binding source. A persisted id that is empty or absent from the library falls back to the runtime identity matched by fingerprint, so the auto-imported entry is shown as in use.</summary>
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
        OnPropertyChanged(nameof(IsApkCertificate));
        OnPropertyChanged(nameof(IsJar));
        OnPropertyChanged(nameof(IsZip));
        OnPropertyChanged(nameof(IsLibraryPurpose));
        OnPropertyChanged(nameof(IsBitsEditable));
        OnPropertyChanged(nameof(IsOutputDirectoryRequired));
        OnPropertyChanged(nameof(PayloadSizes));
        PayloadBits = value == 1 ? 3072 : 2048;
    }

    partial void OnAlgorithmIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsRsaAlgorithm));
        OnPropertyChanged(nameof(IsEcAlgorithm));
        OnPropertyChanged(nameof(ApkKeySizes));
        if (KeySizeIndex >= ApkKeySizes.Length) KeySizeIndex = 0;
    }

    partial void OnKeySizeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedApkKeySize));
        OnPropertyChanged(nameof(IsKeySize2048));
        OnPropertyChanged(nameof(IsKeySize3072));
        OnPropertyChanged(nameof(IsKeySize4096));
        OnPropertyChanged(nameof(IsKeySizeP256));
        OnPropertyChanged(nameof(IsKeySizeP384));
        OnPropertyChanged(nameof(IsKeySizeP521));
    }

    partial void OnFormatIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsFormatAll));
        OnPropertyChanged(nameof(IsFormatJks));
        OnPropertyChanged(nameof(IsFormatPkcs12));
        OnPropertyChanged(nameof(IsFormatPem));
    }
    partial void OnSearchChanged(string value) => Filter();
    partial void OnSelectedKeyChanged(ManagedKey? value)
    {
        ConfirmRemoval = false;
        ConfirmDeletion = false;
        RenameText = value?.Name ?? "";
        PublicKey = "";
        try { if (value is not null) PublicKey = _library.PublicText(value); }
        catch (Exception ex) { Status = global::UotanStudio.Services.UserError.Describe(ex); }
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

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private Task GenerateAsync()
    {
        // APK certificate generation goes through the dedicated generator + export bundle.
        // APK 证书生成走专用生成器 + 格式导出。
        if (IsApkCertificate) return GenerateApkCertificateAsync();
        // Other purposes map to the managed key library; JAR (index 3) maps to ManagedKeyKind.Jar.
        // 其余用途映射到密钥库；JAR（下标 3）映射到 ManagedKeyKind.Jar。
        if (!IsLibraryPurpose) return Task.CompletedTask;
        var name = Name;
        var kind = KindIndex switch
        {
            (int)ManagedKeyKind.Jar => ManagedKeyKind.Jar,
            _ => (ManagedKeyKind)KindIndex,
        };
        var bits = kind switch { ManagedKeyKind.Adb => 2048, ManagedKeyKind.Hdc => 3072, _ => PayloadBits };
        return AddAsync(() => _library.Generate(name, kind, bits));
    }

    private Task GenerateApkCertificateAsync()
    {
        var name = string.IsNullOrWhiteSpace(CertificateAlias) ? Name : CertificateAlias;
        var subject = new UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Keys.ApkSigningKeyOptions
        {
            CommonName = CommonName,
            OrganizationalUnit = OrganizationalUnit,
            Organization = Organization,
            Locality = Locality,
            State = State,
            Country = CountryCode,
        };
        var keyType = AlgorithmIndex == 1
            ? UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Keys.ApkSigningKeyType.EcP256
            : UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Keys.ApkSigningKeyType.Rsa2048;
        // Map the selected size/curve onto the concrete key type: RSA sizes first, then EC curves.
        // 把选中的位数/曲线映射到具体密钥类型：先 RSA 位数，再 EC 曲线。
        var size = SelectedApkKeySize;
        keyType = size switch
        {
            2048 => UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Keys.ApkSigningKeyType.Rsa2048,
            3072 => UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Keys.ApkSigningKeyType.Rsa3072,
            4096 => UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Keys.ApkSigningKeyType.Rsa4096,
            384 => UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Keys.ApkSigningKeyType.EcP384,
            521 => UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Keys.ApkSigningKeyType.EcP521,
            _ => UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Keys.ApkSigningKeyType.EcP256,
        };
        var password = KeystorePassword;
        var validity = ValidityYears;
        var format = (ApkCertificateExportFormat)FormatIndex;
        var directory = OutputDirectory;
        return RunAsync(async () =>
        {
            var bundle = await Task.Run(() => _library.GenerateApkCertificate(
                name, password, password, validity, subject, keyType, format,
                string.IsNullOrWhiteSpace(directory) ? _library.Root : directory));
            _all = await Task.Run(_library.List);
            Search = "";
            Filter();
            SelectedKey = Keys.First(k => k.Id == bundle.Key.Id);
            Status = string.Join(Environment.NewLine, bundle.ExportedFiles);
            KeystorePassword = "";
        });
    }

    public Task ImportAsync(string path)
    {
        if (!IsLibraryPurpose) return Task.CompletedTask;
        var name = Name;
        // JAR (index 3) imports as ManagedKeyKind.Jar; APK certificates use index 4.
        // JAR（下标 3）导入为 ManagedKeyKind.Jar；APK 证书使用下标 4。
        var kind = KindIndex switch
        {
            (int)ManagedKeyKind.Jar => ManagedKeyKind.Jar,
            _ => (ManagedKeyKind)KindIndex,
        };
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
        if (IsBusy || SelectedKey is not { Kind: ManagedKeyKind.Adb or ManagedKeyKind.Hdc } key) return Task.CompletedTask;
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
        catch (Exception ex) { Status = global::UotanStudio.Services.UserError.Describe(ex); }
        finally { IsBusy = false; }
    }
}