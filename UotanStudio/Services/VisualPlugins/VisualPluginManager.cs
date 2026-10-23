using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using UotanStudio.Projects;
using UotanStudio.Services.Plugins;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Registry;

namespace UotanStudio.Services.VisualPlugins;

/// <summary>
/// Explicit decision that authorizes installing one package.
/// <para>授权安装单个包的显式决定。</para>
/// </summary>
/// <remarks>
/// The manager never widens a grant beyond the permissions a package declares or derives from its graph. A signed
/// package additionally requires <see cref="TrustPublisher"/> the first time its publisher is seen and again after
/// a key rotation; an unsigned package requires <see cref="AllowUntrusted"/>.
/// <para>管理器不会把授权扩展到包声明或由图推导的权限之外。已签名包在首次见到其发布者以及密钥轮换后都需要
/// <see cref="TrustPublisher"/>；未签名包需要 <see cref="AllowUntrusted"/>。</para>
/// </remarks>
public sealed record VisualPluginInstallConsent
{
    /// <summary>Whether an unsigned package may be installed locally. <para>是否允许在本地安装未签名包。</para></summary>
    /// <value><see langword="true"/> to accept a package without a signature.
    /// <para>接受无签名包时为 <see langword="true"/>。</para></value>
    public bool AllowUntrusted { get; init; }

    /// <summary>Whether the publisher fingerprint may be pinned or rotated. <para>是否允许固定或轮换发布者指纹。</para></summary>
    /// <value><see langword="true"/> after the user confirmed the publisher id and fingerprint.
    /// <para>用户确认发布者标识与指纹后为 <see langword="true"/>。</para></value>
    public bool TrustPublisher { get; init; }

    /// <summary>Permissions the user authorized. <para>用户授权的权限。</para></summary>
    /// <value>The requested subset; the manager stores it intersected with the permissions the package declares.
    /// <para>请求的子集；管理器会与包声明的权限取交集后保存。</para></value>
    public PluginPermission Permissions { get; init; }

    /// <summary>
    /// Builds the default consent for a programmatic local install.
    /// <para>构建程序化本地安装的默认同意。</para>
    /// </summary>
    /// <param name="package">Package being installed; its declared and graph-derived permissions are granted.
    /// <para>待安装的包；其声明与图推导的权限会被授予。</para></param>
    /// <returns>A consent that accepts unsigned content and grants exactly the package's requested permissions, without pinning a publisher.
    /// <para>接受未签名内容、仅授予包所请求权限且不固定发布者的同意。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 为 <see langword="null"/>。</para></exception>
    public static VisualPluginInstallConsent LocalInstall(VisualPluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new VisualPluginInstallConsent
        {
            AllowUntrusted = true,
            Permissions = PluginPermissions.Derive(package)
        };
    }
}

/// <summary>
/// Installs and activates JSON workflow and composite-node packages rendered as visual plugins.
/// <para>安装并激活以可视化插件形式呈现的 JSON 工作流与复合节点包。</para>
/// </summary>
public sealed class VisualPluginManager
{
    private const long MaximumStateFileSize = 256 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private readonly string _statePath;
    private readonly IPluginRegistry _registry;
    private readonly PublisherTrustStore _trustStore;
    private readonly HashSet<string> _registeredTypeKeys = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _disabledIds = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PluginPermission> _grants = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Application-wide visual-plugin manager.
    /// <para>应用范围的可视化插件管理器。</para>
    /// </summary>
    /// <value>
    /// The shared manager instance.
    /// <para>共享的管理器实例。</para>
    /// </value>
    public static VisualPluginManager Instance { get; } = new();

    /// <summary>
    /// Current installed package registrations and diagnostics.
    /// <para>当前的已安装包注册信息与诊断。</para>
    /// </summary>
    /// <value>
    /// One registration per installed package file.
    /// <para>每个已安装包文件对应一条注册信息。</para>
    /// </value>
    public ObservableCollection<VisualPluginRegistration> Registrations { get; } = [];

    /// <summary>
    /// Raised after installed packages or activation state change.
    /// <para>在已安装包或激活状态发生变化后引发。</para>
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Creates a manager over an installation directory and node registry.
    /// <para>基于安装目录与节点注册表创建管理器。</para>
    /// </summary>
    /// <param name="directory">
    /// Installation directory, defaulting to per-user application data.
    /// <para>安装目录；缺省时使用按用户的应用数据目录。</para>
    /// </param>
    /// <param name="registry">
    /// Node registry receiving activated packages.
    /// <para>接收已激活节点的注册表。</para>
    /// </param>
    /// <param name="trustStore">
    /// Publisher pin store consulted by installs and refreshes; defaults to the per-user store.
    /// <para>安装与刷新查询的发布者固定库；缺省使用按用户位置的信任库。</para>
    /// </param>
    public VisualPluginManager(string? directory = null, IPluginRegistry? registry = null,
        PublisherTrustStore? trustStore = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UotanStudio", "visual-plugins");
        _statePath = Path.Combine(_directory, "state.json");
        _registry = registry ?? PluginRegistry.Shared;
        _trustStore = trustStore ?? new PublisherTrustStore();
        Refresh();
    }

    /// <summary>
    /// Publisher pins used by installs and refreshes.
    /// <para>安装与刷新使用的发布者固定库。</para>
    /// </summary>
    /// <value>The store passed to the constructor or the per-user default.
    /// <para>构造函数传入的信任库，或按用户位置的默认库。</para></value>
    public PublisherTrustStore TrustStore => _trustStore;

    /// <summary>
    /// Re-scans installed packages, validates dependencies, and refreshes the node registrations.
    /// <para>重新扫描已安装包、校验依赖，并刷新节点注册信息。</para>
    /// </summary>
    public void Refresh()
    {
        LoadState();
        RefreshCore();
    }

    private void RefreshCore()
    {
        Directory.CreateDirectory(_directory);
        foreach (var typeKey in _registeredTypeKeys.ToArray())
            _registry.UnregisterNode(typeKey);
        _registeredTypeKeys.Clear();

        List<(string Path, VisualPluginPackage Package)> loaded = [];
        List<VisualPluginRegistration> invalid = [];
        foreach (var path in Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => path.EndsWith(".uotanplugin.json", StringComparison.OrdinalIgnoreCase)
                         || path.EndsWith(".uotanplugin", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                loaded.Add((path, VisualPluginFile.Load(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                invalid.Add(VisualPluginRegistration.Invalid(path, ex.Message));
            }
        }

        var capability = RuntimeCapabilityProbe.EffectiveCapability;
        var duplicates = loaded.GroupBy(entry => entry.Package.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compositeByType = loaded
            .Where(entry => IsNodeKind(entry.Package.Kind))
            .GroupBy(entry => entry.Package.NodeTypeKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Package, StringComparer.OrdinalIgnoreCase);
        var cycleTypes = FindDependencyCycles(compositeByType);
        var available = new HashSet<string>(_registry.BuiltInTypeKeys, StringComparer.OrdinalIgnoreCase);
        var errorsById = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var trustById = new Dictionary<string, PublisherTrustStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, package) in loaded)
        {
            trustById.TryAdd(package.Manifest.Id, _trustStore.GetStatus(package));
        }
        List<VisualPluginPackage> pending = [];
        var enabledPackages = loaded.Select(entry => entry.Package)
            .Where(package => IsNodeKind(package.Kind) && !_disabledIds.Contains(package.Manifest.Id))
            .ToArray();

        foreach (var (_, package) in loaded)
        {
            List<string> errors = [];
            errorsById[package.Manifest.Id] = errors;
            if (duplicates.Contains(package.Manifest.Id)) errors.Add("Multiple installed files use the same plugin id.");
            if (!SupportsCurrentPlatform(package.Manifest.Platforms)) errors.Add("This package does not support the current platform.");
            if (cycleTypes.Contains(package.NodeTypeKey)) errors.Add("Composite-plugin dependency cycle detected.");
            if (PluginKindPolicy.IsActivationBlocked(package.Kind, capability)) errors.Add(LanguageService.Instance.GetString("Plugin_CodeRuntimeDisabled"));
            var trustStatus = trustById.GetValueOrDefault(package.Manifest.Id, PublisherTrustStatus.Unsigned);
            if (trustStatus is PublisherTrustStatus.UnknownPublisher)
                errors.Add(LanguageService.Instance.GetString("Plugin_PublisherUntrusted"));
            else if (trustStatus is PublisherTrustStatus.FingerprintMismatch)
                errors.Add(LanguageService.Instance.GetString("Plugin_PublisherFingerprintChanged"));
            var missingPermissions = PluginPermissions.Missing(PluginPermissions.Derive(package),
                _grants.GetValueOrDefault(package.Manifest.Id));
            if (missingPermissions != PluginPermission.None)
                errors.Add(LanguageService.Instance.GetString("Plugin_PermissionMissing", DescribePermissions(missingPermissions)));
            errors.AddRange(GetRequirementErrors(package, enabledPackages));
            if (IsNodeKind(package.Kind)
                && !_disabledIds.Contains(package.Manifest.Id)
                && errors.Count == 0)
            {
                pending.Add(package);
            }
        }

        var madeProgress = true;
        while (madeProgress && pending.Count > 0)
        {
            madeProgress = false;
            for (var index = pending.Count - 1; index >= 0; index--)
            {
                var package = pending[index];
                if (!GetExternalNodeTypes(package).All(available.Contains)) continue;
                if (!package.Manifest.Dependencies.All(dependency => available.Contains("VisualPlugin." + dependency.Id))) continue;
                var descriptor = package.Kind switch
                {
                    VisualPluginKind.CompositeNode => NodeCatalogSeed.CreateCompositePluginDescriptor(
                        package, () => GetExecutionErrors(package)),
                    VisualPluginKind.ScriptNode => NodeCatalogSeed.CreateScriptPluginDescriptor(
                        package, () => GetExecutionErrors(package)),
                    _ => throw new InvalidOperationException("Only node packages can be registered in the toolbox.")
                };
                _registry.RegisterNode(descriptor, package);
                _registeredTypeKeys.Add(package.NodeTypeKey);
                available.Add(package.NodeTypeKey);
                pending.RemoveAt(index);
                madeProgress = true;
            }
        }

        foreach (var package in pending)
        {
            var missing = GetExternalNodeTypes(package).Where(type => !available.Contains(type)).ToArray();
            if (missing.Length > 0)
            {
                errorsById[package.Manifest.Id].Add("Unavailable node dependencies: " + string.Join(", ", missing));
            }
            foreach (var dependency in package.Manifest.Dependencies.Where(dependency => !available.Contains("VisualPlugin." + dependency.Id)))
            {
                errorsById[package.Manifest.Id].Add(LanguageService.Instance.GetString("Plugin_DependencyUnavailable", dependency.Id));
            }
        }

        Registrations.Clear();
        foreach (var (path, package) in loaded.OrderBy(entry => entry.Package.Manifest.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var enabled = IsNodeKind(package.Kind) && !_disabledIds.Contains(package.Manifest.Id);
            Registrations.Add(new VisualPluginRegistration(path, package, enabled,
                [.. errorsById[package.Manifest.Id]], _grants.GetValueOrDefault(package.Manifest.Id),
                trustById.GetValueOrDefault(package.Manifest.Id, PublisherTrustStatus.Unsigned)));
        }
        foreach (var registration in invalid) Registrations.Add(registration);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Installs or replaces a package with the default local consent.
    /// <para>以默认的本地同意选项安装或替换一个包。</para></summary>
    /// <param name="sourcePath">Path of the package file to install.<para>要安装的包文件路径。</para></param>
    /// <param name="cancellationToken">Token cancelling the pending installation.
    /// <para>取消待执行安装的令牌。</para></param>
    /// <returns>The registration describing the installed package.<para>描述已安装包的注册信息。</para></returns>
    public Task<VisualPluginRegistration> InstallAsync(string sourcePath,
        CancellationToken cancellationToken = default) =>
        InstallAsync(sourcePath, consent: null, cancellationToken);

    /// <summary>Installs or replaces a package after bounded parsing, structural validation and an explicit consent decision.
    /// <para>在有界解析、结构校验与显式同意决定之后安装或替换一个包。</para></summary>
    /// <param name="sourcePath">Path of the package file to install.<para>要安装的包文件路径。</para></param>
    /// <param name="consent">Explicit install decision, or <see langword="null"/> for the default local consent that grants exactly the permissions the package declares.
    /// <para>显式安装决定；为 <see langword="null"/> 时使用仅授予包已声明权限的默认本地同意。</para></param>
    /// <param name="cancellationToken">Token cancelling the pending installation.
    /// <para>取消待执行安装的令牌。</para></param>
    /// <returns>The registration describing the installed package.<para>描述已安装包的注册信息。</para></returns>
    /// <exception cref="InvalidOperationException">The package is unsigned and <see cref="VisualPluginInstallConsent.AllowUntrusted"/> is false, or its publisher is unknown or rotated without <see cref="VisualPluginInstallConsent.TrustPublisher"/>.
    /// <para>包未签名且 <see cref="VisualPluginInstallConsent.AllowUntrusted"/> 为 false，或其发布者未知或已轮换却没有 <see cref="VisualPluginInstallConsent.TrustPublisher"/>。</para></exception>
    public async Task<VisualPluginRegistration> InstallAsync(string sourcePath,
        VisualPluginInstallConsent? consent, CancellationToken cancellationToken = default)
    {
        var package = await VisualPluginFile.LoadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        return await InstallPackageAsync(package, consent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Installs or replaces an already-built package with the default local consent.
    /// <para>以默认的本地同意选项安装或替换一个已构建的包。</para></summary>
    /// <param name="package">The package to install; it must already pass <see cref="VisualPluginValidation.Validate"/>.
    /// <para>要安装的包；它必须已经通过 <see cref="VisualPluginValidation.Validate"/>。</para></param>
    /// <param name="cancellationToken">Cancels the publication while preserving the previous target.
    /// <para>取消发布过程，同时保留原有目标。</para></param>
    /// <returns>The registration describing the installed package.<para>描述已安装包的注册信息。</para></returns>
    public Task<VisualPluginRegistration> InstallPackageAsync(VisualPluginPackage package,
        CancellationToken cancellationToken = default) =>
        InstallPackageAsync(package, consent: null, cancellationToken);

    /// <summary>Installs or replaces an already-built package after structural validation and an explicit consent decision.
    /// <para>在结构校验与显式同意决定之后安装或替换一个已构建的包。</para></summary>
    /// <param name="package">The package to install; it must already pass <see cref="VisualPluginValidation.Validate"/>.
    /// <para>要安装的包；它必须已经通过 <see cref="VisualPluginValidation.Validate"/>。</para></param>
    /// <param name="consent">Explicit install decision, or <see langword="null"/> for the default local consent that grants exactly the permissions the package declares.
    /// <para>显式安装决定；为 <see langword="null"/> 时使用仅授予包已声明权限的默认本地同意。</para></param>
    /// <param name="cancellationToken">Cancels the publication while preserving the previous target.
    /// <para>取消发布过程，同时保留原有目标。</para></param>
    /// <returns>The registration describing the installed package.<para>描述已安装包的注册信息。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is null.
    /// <para><paramref name="package"/> 为 null。</para></exception>
    /// <exception cref="InvalidDataException">Validation reports errors.
    /// <para>校验报错。</para></exception>
    /// <exception cref="InvalidOperationException">The package is unsigned and <see cref="VisualPluginInstallConsent.AllowUntrusted"/> is false, or its publisher is unknown or rotated without <see cref="VisualPluginInstallConsent.TrustPublisher"/>.
    /// <para>包未签名且 <see cref="VisualPluginInstallConsent.AllowUntrusted"/> 为 false，或其发布者未知或已轮换却没有 <see cref="VisualPluginInstallConsent.TrustPublisher"/>。</para></exception>
    public async Task<VisualPluginRegistration> InstallPackageAsync(VisualPluginPackage package,
        VisualPluginInstallConsent? consent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        var effective = consent ?? VisualPluginInstallConsent.LocalInstall(package);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureTrustAccepted(package, effective);
            Directory.CreateDirectory(_directory);
            var matches = Registrations.Where(candidate => string.Equals(candidate.Id, package.Manifest.Id,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1)
            {
                throw new InvalidOperationException("Resolve duplicate installed package ids before replacing a package.");
            }
            EnsureTrustPinned(package, effective);
            var granted = effective.Permissions & PluginPermissions.Derive(package);
            var hadGrant = _grants.TryGetValue(package.Manifest.Id, out var previousGrant);
            _grants[package.Manifest.Id] = granted;
            try
            {
                await PublishPackageAsync(matches.FirstOrDefault(), package, cancellationToken);
            }
            catch
            {
                if (hadGrant) _grants[package.Manifest.Id] = previousGrant;
                else _grants.Remove(package.Manifest.Id);
                throw;
            }
            if (IsNodeKind(package.Kind)) _disabledIds.Remove(package.Manifest.Id);
            await SaveStateAsync(cancellationToken);
            RefreshCore();
            return Registrations.First(registration =>
                string.Equals(registration.Id, package.Manifest.Id, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Builds and installs one script-node package from a <c>.lua</c> source file with the default local consent.
    /// <para>以默认的本地同意选项由 <c>.lua</c> 源文件构建并安装一个脚本节点包。</para></summary>
    /// <param name="luaPath">Path to the Lua source file.<para>Lua 源文件的路径。</para></param>
    /// <param name="cancellationToken">Cancels the read and installation.
    /// <para>取消读取与安装过程。</para></param>
    /// <exception cref="ArgumentException"><paramref name="luaPath"/> is null, empty, or whitespace.
    /// <para><paramref name="luaPath"/> 为 null、空或仅空白。</para></exception>
    /// <exception cref="InvalidDataException">The script is empty, exceeds the package bound, or fails validation.
    /// <para>脚本为空、超出包大小上限或未通过校验。</para></exception>
    /// <returns>The registration describing the installed package.<para>描述已安装包的注册信息。</para></returns>
    public Task<VisualPluginRegistration> InstallScriptAsync(string luaPath,
        CancellationToken cancellationToken = default) =>
        InstallScriptAsync(luaPath, consent: null, cancellationToken);

    /// <summary>Builds and installs one script-node package from a <c>.lua</c> source file.
    /// <para>由 <c>.lua</c> 源文件构建并安装一个脚本节点包。</para></summary>
    /// <param name="luaPath">Path to the Lua source file.<para>Lua 源文件的路径。</para></param>
    /// <param name="consent">Explicit install decision, or <see langword="null"/> for the default local consent that grants exactly the permissions the package declares.
    /// <para>显式安装决定；为 <see langword="null"/> 时使用仅授予包已声明权限的默认本地同意。</para></param>
    /// <param name="cancellationToken">Cancels the read and installation.
    /// <para>取消读取与安装过程。</para></param>
    /// <exception cref="ArgumentException"><paramref name="luaPath"/> is null, empty, or whitespace.
    /// <para><paramref name="luaPath"/> 为 null、空或仅空白。</para></exception>
    /// <exception cref="InvalidDataException">The script is empty, exceeds the package bound, or fails validation.
    /// <para>脚本为空、超出包大小上限或未通过校验。</para></exception>
    /// <returns>The registration describing the installed package.<para>描述已安装包的注册信息。</para></returns>
    public async Task<VisualPluginRegistration> InstallScriptAsync(string luaPath,
        VisualPluginInstallConsent? consent, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(luaPath);
        var script = await File.ReadAllTextAsync(luaPath, cancellationToken).ConfigureAwait(false);
        if (script.Length > VisualPluginValidation.MaximumScriptLength)
            throw new InvalidDataException($"Plugin script exceeds {VisualPluginValidation.MaximumScriptLength} characters.");
        var name = Path.GetFileNameWithoutExtension(luaPath);
        var package = VisualPluginPackageBuilder.CreateScript(name, VisualPluginPackageBuilder.NormalizeId(name), script);
        return await InstallPackageAsync(package, consent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Enables or disables an installed node package without modifying its portable content.
    /// <para>启用或禁用一个已安装的节点包，不修改其可移植内容。</para></summary>
    /// <param name="pluginId">Identifier of the installed package.<para>已安装包的标识。</para></param>
    /// <param name="enabled">Whether the package becomes active.<para>该包是否转为活动。</para></param>
    /// <param name="cancellationToken">Token cancelling the pending state write.
    /// <para>取消待执行状态写入的令牌。</para></param>
    public async Task SetEnabledAsync(string pluginId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled) _disabledIds.Remove(pluginId);
            else _disabledIds.Add(pluginId);
            await SaveStateAsync(cancellationToken);
            RefreshCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Atomically replaces an installed package without changing its activation state or granted permissions.
    /// <para>原子地替换已安装的包，不改变其激活状态或已授权限。</para></summary>
    /// <param name="registration">Registration of the package being replaced.
    /// <para>被替换包的注册信息。</para></param>
    /// <param name="package">Replacement package content.<para>替换后的包内容。</para></param>
    /// <param name="cancellationToken">Token cancelling the pending publication.
    /// <para>取消待执行发布的令牌。</para></param>
    /// <returns>The refreshed registration for the replacement.<para>替换后刷新得到的注册信息。</para></returns>
    public Task<VisualPluginRegistration> UpdateAsync(VisualPluginRegistration registration,
        VisualPluginPackage package, CancellationToken cancellationToken = default) =>
        UpdateAsync(registration, package, consent: null, cancellationToken);

    /// <summary>Atomically replaces an installed package after an explicit consent decision.
    /// <para>在显式同意决定之后原子替换已安装的包。</para></summary>
    /// <param name="registration">Registration of the package being replaced.
    /// <para>被替换包的注册信息。</para></param>
    /// <param name="package">Replacement package content.<para>替换后的包内容。</para></param>
    /// <param name="consent">Explicit consent for the replacement; <see langword="null"/> keeps the granted permissions and accepts an unsigned local edit.
    /// <para>替换所需的显式同意；<see langword="null"/> 表示保留已授权限并接受未签名的本地编辑。</para></param>
    /// <param name="cancellationToken">Token cancelling the pending publication.
    /// <para>取消待执行发布的令牌。</para></param>
    /// <returns>The refreshed registration for the replacement.<para>替换后刷新得到的注册信息。</para></returns>
    /// <exception cref="InvalidOperationException">The replacement is signed by an unknown or rotated publisher without explicit trust, or it changes the plugin id or kind.
    /// <para>替换包由未知或已轮换的发布者签名且没有显式信任，或改变插件标识或类型。</para></exception>
    public async Task<VisualPluginRegistration> UpdateAsync(VisualPluginRegistration registration,
        VisualPluginPackage package, VisualPluginInstallConsent? consent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(package);
        if (!string.Equals(registration.Id, package.Manifest.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An installed plugin cannot be replaced with a different plugin id.");
        if (registration.Kind != package.Kind)
            throw new InvalidOperationException("An installed plugin cannot change its package kind.");
        var effective = consent ?? new VisualPluginInstallConsent
        {
            AllowUntrusted = true,
            Permissions = registration.GrantedPermissions
        };
        EnsureTrustAccepted(package, effective);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var fullPath = Path.GetFullPath(registration.FilePath);
            var root = Path.GetFullPath(_directory) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Plugin file is outside the managed directory.");

            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException("The installed plugin has been removed. Import it again before editing.");
            }
            if (consent is not null)
            {
                EnsureTrustPinned(package, effective);
                _grants[package.Manifest.Id] = effective.Permissions & PluginPermissions.Derive(package);
            }
            var updated = await PublishPackageAsync(registration, package, cancellationToken);
            if (consent is not null)
            {
                await SaveStateAsync(cancellationToken);
            }
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes one installed package and its activation state.
    /// <para>移除一个已安装的包及其激活状态。</para>
    /// </summary>
    /// <param name="pluginId">
    /// Identifier of the package to remove.
    /// <para>要移除的包的标识。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending removal.
    /// <para>取消待执行移除的令牌。</para>
    /// </param>
    public Task RemoveAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var matches = Registrations.Where(candidate =>
            string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException("Select one installed plugin file to remove.");
        }
        return RemoveAsync(matches[0], cancellationToken);
    }

    /// <summary>
    /// Deletes the selected managed file, including invalid or duplicate-id packages.
    /// <para>删除选中的受管文件，包含无效或标识重复的包。</para>
    /// </summary>
    /// <param name="registration">
    /// Registration naming the file to delete.
    /// <para>指明待删除文件的注册信息。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token cancelling the pending deletion.
    /// <para>取消待执行删除的令牌。</para>
    /// </param>
    public async Task RemoveAsync(VisualPluginRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var fullPath = Path.GetFullPath(registration.FilePath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_directory));
            if (!string.Equals(Path.GetDirectoryName(fullPath), root, comparison)
                || !Registrations.Any(candidate => string.Equals(
                    Path.GetFullPath(candidate.FilePath), fullPath, comparison)))
            {
                throw new InvalidOperationException("Plugin file is outside the managed directory.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(fullPath);
            DeleteDetachedSignature(fullPath);
            try
            {
                if (!Registrations.Any(candidate => string.Equals(candidate.Id, registration.Id,
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFullPath(candidate.FilePath), fullPath, comparison)))
                {
                    _disabledIds.Remove(registration.Id);
                    _grants.Remove(registration.Id);
                }
                await SaveStateAsync(CancellationToken.None);
            }
            finally
            {
                RefreshCore();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<VisualPluginRegistration> PublishPackageAsync(VisualPluginRegistration? previous,
        VisualPluginPackage package, CancellationToken cancellationToken)
    {
        var oldPath = previous?.FilePath;
        var destination = oldPath ?? Path.Combine(_directory, package.Manifest.Id + ".uotanplugin.json");
        if (VisualPluginBundle.EnumerateScopes(package).Any(scope => scope.Assets.Count > 0)
            && !destination.EndsWith(".uotanplugin", StringComparison.OrdinalIgnoreCase))
        {
            destination = Path.Combine(_directory, package.Manifest.Id + ".uotanplugin");
        }
        var moved = oldPath is not null && !string.Equals(oldPath, destination, StringComparison.Ordinal);
        if ((oldPath is null || moved) && File.Exists(destination))
        {
            throw new IOException("The package destination already exists and is not the selected installation.");
        }
        var backup = oldPath is null ? null : oldPath + "." + Guid.NewGuid().ToString("N") + ".backup";
        var published = false;
        var recovered = false;
        try
        {
            if (backup is not null)
            {
                await using var input = new FileStream(oldPath!, FileMode.Open, FileAccess.Read, FileShare.Read,
                    131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, 131072, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            await VisualPluginFile.SaveAsync(destination, package, cancellationToken);
            published = true;
            if (moved)
            {
                File.Delete(oldPath!);
                DeleteDetachedSignature(oldPath!);
            }
            RefreshCore();
            var updated = Registrations.First(candidate =>
                string.Equals(candidate.Id, package.Manifest.Id, StringComparison.OrdinalIgnoreCase));
            if (previous is { IsAvailable: true } && !updated.IsAvailable)
            {
                throw new InvalidDataException($"The update would make the plugin unavailable: {updated.ErrorText}");
            }
            recovered = true;
            return updated;
        }
        catch
        {
            if (published)
            {
                if (backup is not null)
                {
                    File.Move(backup, oldPath!, overwrite: true);
                    DeleteDetachedSignature(oldPath!);
                }
                if (oldPath is null || moved)
                {
                    File.Delete(destination);
                    DeleteDetachedSignature(destination);
                }
                RefreshCore();
            }
            recovered = true;
            throw;
        }
        finally
        {
            if ((!published || recovered) && backup is not null && File.Exists(backup))
            {
                File.Delete(backup);
            }
        }
    }

    private static bool IsNodeKind(VisualPluginKind kind)
        => kind is VisualPluginKind.CompositeNode or VisualPluginKind.ScriptNode;

    private static void DeleteDetachedSignature(string packagePath)
    {
        var sidecar = VisualPluginFile.DetachedSignaturePath(packagePath);
        if (File.Exists(sidecar)) File.Delete(sidecar);
    }

    private void EnsureTrustAccepted(VisualPluginPackage package, VisualPluginInstallConsent consent)
    {
        switch (_trustStore.GetStatus(package))
        {
            case PublisherTrustStatus.Unsigned when !consent.AllowUntrusted:
                throw new InvalidOperationException(
                    "The package is not signed; installing it requires an explicit local-install confirmation.");
            case PublisherTrustStatus.UnknownPublisher when !consent.TrustPublisher:
                throw new InvalidOperationException(
                    "The publisher is not trusted yet; confirm its fingerprint before installing the package.");
            case PublisherTrustStatus.FingerprintMismatch when !consent.TrustPublisher:
                throw new InvalidOperationException(
                    "The publisher key changed since it was trusted; confirm the new fingerprint before installing the package.");
        }
    }

    private void EnsureTrustPinned(VisualPluginPackage package, VisualPluginInstallConsent consent)
    {
        if (consent.TrustPublisher && package.IsSigned && package.Manifest.Publisher is not null)
        {
            _trustStore.Trust(package.Manifest);
        }
    }

    /// <summary>
    /// Pins the publisher of an installed package after explicit user confirmation.
    /// <para>在用户显式确认后固定一个已安装包的发布者。</para>
    /// </summary>
    /// <param name="pluginId">Identifier of the installed package. <para>已安装包的标识。</para></param>
    /// <exception cref="InvalidOperationException">The id is not installed exactly once, or the package carries no signed publisher.
    /// <para>该标识未恰好安装一份，或包没有已签名的发布者。</para></exception>
    public void TrustPublisher(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var matches = Registrations.Where(candidate =>
            string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException("Select one installed package to trust its publisher.");
        }
        if (!matches[0].Package.IsSigned || matches[0].Package.Manifest.Publisher is null)
        {
            throw new InvalidOperationException("This package carries no signed publisher identity.");
        }
        _trustStore.Trust(matches[0].Package.Manifest);
        RefreshCore();
    }

    /// <summary>
    /// Removes the pinned fingerprint of the publisher behind an installed package.
    /// <para>移除某个已安装包所对应发布者的固定指纹。</para>
    /// </summary>
    /// <param name="pluginId">Identifier of the installed package. <para>已安装包的标识。</para></param>
    /// <returns><see langword="true"/> when a pin was removed. <para>移除了固定记录时为 <see langword="true"/>。</para></returns>
    /// <exception cref="InvalidOperationException">The id is not installed exactly once, or the package carries no signed publisher.
    /// <para>该标识未恰好安装一份，或包没有已签名的发布者。</para></exception>
    public bool UntrustPublisher(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var matches = Registrations.Where(candidate =>
            string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException("Select one installed package to untrust its publisher.");
        }
        var publisherId = matches[0].Package.Manifest.Publisher?.Id;
        if (string.IsNullOrWhiteSpace(publisherId))
        {
            throw new InvalidOperationException("This package carries no signed publisher identity.");
        }
        var removed = _trustStore.Untrust(publisherId);
        if (removed) RefreshCore();
        return removed;
    }

    /// <summary>
    /// Returns the permissions the user granted to one installed package.
    /// <para>返回用户为某个已安装包授予的权限。</para>
    /// </summary>
    /// <param name="pluginId">Identifier of the installed package. <para>已安装包的标识。</para></param>
    /// <returns>The granted set, <see cref="PluginPermission.None"/> when the package never obtained a grant.
    /// <para>已授权的集合；包从未获得授权时为 <see cref="PluginPermission.None"/>。</para></returns>
    public PluginPermission GrantedPermissions(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _grants.GetValueOrDefault(pluginId);
    }

    internal static string DescribePermissions(PluginPermission permissions)
    {
        if (permissions == PluginPermission.None)
        {
            return LanguageService.Instance.GetString("Plugin_PermissionsNone");
        }
        return string.Join(", ", PluginPermissions.Known
            .Where(permission => permissions.HasFlag(permission))
            .Select(permission => LanguageService.Instance.GetString("Plugin_Permission_" + permission)));
    }

    private void LoadState()
    {
        _disabledIds = new(StringComparer.OrdinalIgnoreCase);
        _grants = new Dictionary<string, PluginPermission>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_statePath)) return;
        try
        {
            var info = new FileInfo(_statePath);
            if (info.Length > MaximumStateFileSize) return;
            using var stream = info.OpenRead();
            var state = JsonSerializer.Deserialize(stream, VisualPluginManagerJsonContext.Default.VisualPluginManagerState);
            if (state is null) return;
            _disabledIds = (state.DisabledIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (state.Grants is { } grants)
            {
                foreach (var (pluginId, names) in grants)
                {
                    var granted = PluginPermission.None;
                    foreach (var name in names ?? [])
                    {
                        if (PluginPermissions.TryParse(name, out var permission)) granted |= permission;
                    }
                    if (!string.IsNullOrWhiteSpace(pluginId)) _grants[pluginId] = granted;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogService.Instance.Write(LogLevel.Warn, "VisualPlugins", ex.Message);
        }
    }

    private async Task SaveStateAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var state = new VisualPluginManagerState
                {
                    DisabledIds = [.. _disabledIds.Order()],
                    Grants = _grants.Where(pair => pair.Value != PluginPermission.None)
                        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(pair => pair.Key, pair => PluginPermissions.Known
                            .Where(permission => pair.Value.HasFlag(permission))
                            .Select(permission => permission.ToString())
                            .ToList(), StringComparer.OrdinalIgnoreCase)
                };
                await JsonSerializer.SerializeAsync(stream, state,
                    VisualPluginManagerJsonContext.Default.VisualPluginManagerState, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, _statePath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static IEnumerable<string> GetExternalNodeTypes(VisualPluginPackage package)
    {
        var embedded = package.Graph.Nodes.Where(node => node.CompositePackage is not null)
            .ToLookup(node => node.TypeKey, node => node.CompositePackage!, StringComparer.OrdinalIgnoreCase);
        foreach (var type in package.Manifest.RequiredNodeTypes)
        {
            if (embedded.Contains(type))
            {
                if (embedded[type].Any(child => child.Assets.Count > 0 && child.BundlePath is null))
                {
                    yield return type;
                }
                foreach (var dependency in embedded[type].SelectMany(GetExternalNodeTypes))
                {
                    yield return dependency;
                }
            }
            else
            {
                yield return type;
            }
        }
    }

    private static bool SupportsCurrentPlatform(IReadOnlyCollection<string> platforms)
    {
        if (platforms.Count == 0) return true;
        var current = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "macos"
            : "unknown";
        return platforms.Any(platform => string.Equals(platform, current, StringComparison.OrdinalIgnoreCase)
            || (current == "macos" && string.Equals(platform, "osx", StringComparison.OrdinalIgnoreCase))
            || string.Equals(platform, "any", StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> FindDependencyCycles(
        IReadOnlyDictionary<string, VisualPluginPackage> packages)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        List<string> stack = [];

        void Visit(string typeKey)
        {
            if (states.GetValueOrDefault(typeKey) == 2) return;
            if (states.GetValueOrDefault(typeKey) == 1)
            {
                var start = stack.FindIndex(item => string.Equals(item, typeKey, StringComparison.OrdinalIgnoreCase));
                if (start >= 0)
                    foreach (var item in stack.Skip(start)) result.Add(item);
                return;
            }

            states[typeKey] = 1;
            stack.Add(typeKey);
            foreach (var dependency in GetExternalNodeTypes(packages[typeKey]).Concat(
                         packages[typeKey].Manifest.Dependencies.Select(dependency => "VisualPlugin." + dependency.Id)))
                if (packages.ContainsKey(dependency)) Visit(dependency);
            stack.RemoveAt(stack.Count - 1);
            states[typeKey] = 2;
        }

        foreach (var typeKey in packages.Keys) Visit(typeKey);
        return result;
    }

    /// <summary>
    /// Checks an edited package against the currently available installations.
    /// <para>依据当前可用的安装情况检查一个已编辑的包。</para>
    /// </summary>
    /// <param name="package">
    /// Package to check.
    /// <para>要检查的包。</para>
    /// </param>
    /// <returns>
    /// Messages describing unmet requirements, empty when all are met.
    /// <para>描述未满足要求的消息；全部满足时为空。</para>
    /// </returns>
    public IReadOnlyList<string> ValidateRequirements(VisualPluginPackage package)
    {
        var structural = VisualPluginValidation.Validate(package);
        if (structural.Count > 0)
        {
            return structural;
        }
        var errors = GetRequirementErrors(package, [.. Registrations.Where(registration => registration.IsEnabled).Select(registration => registration.Package)]);
        foreach (var dependency in package.Manifest.Dependencies)
        {
            if (!Registrations.Any(registration => registration.IsEnabled && registration.IsAvailable
                    && string.Equals(registration.Id, dependency.Id, StringComparison.OrdinalIgnoreCase)))
            {
                var message = LanguageService.Instance.GetString("Plugin_DependencyUnavailable", dependency.Id);
                if (!errors.Contains(message)) errors.Add(message);
            }
        }
        return errors;
    }

    private static List<string> GetRequirementErrors(VisualPluginPackage package, IReadOnlyList<VisualPluginPackage> enabled)
    {
        List<string> errors = [];
        foreach (var dependency in package.Manifest.Dependencies)
        {
            var candidates = enabled.Where(candidate => string.Equals(candidate.Manifest.Id, dependency.Id,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length != 1)
            {
                errors.Add(LanguageService.Instance.GetString("Plugin_DependencyUnavailable", dependency.Id));
            }
            else if (!dependency.MatchesVersion(candidates[0].Manifest.Version))
            {
                errors.Add(LanguageService.Instance.GetString("Plugin_DependencyVersionMismatch", dependency.Id,
                    candidates[0].Manifest.Version, dependency.MinimumVersion ?? "*", dependency.MaximumVersionExclusive ?? "*"));
            }
        }
        foreach (var other in enabled)
        {
            if (string.Equals(other.Manifest.Id, package.Manifest.Id, StringComparison.OrdinalIgnoreCase)) continue;
            if (package.Manifest.Conflicts.Contains(other.Manifest.Id, StringComparer.OrdinalIgnoreCase)
                || other.Manifest.Conflicts.Contains(package.Manifest.Id, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(LanguageService.Instance.GetString("Plugin_PackageConflict", other.Manifest.Id));
            }
        }
        return errors;
    }

    private IReadOnlyList<string> GetExecutionErrors(VisualPluginPackage package)
    {
        var installed = Registrations.Where(registration => string.Equals(registration.Id, package.Manifest.Id,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (installed.Length != 1 || !installed[0].IsEnabled || !installed[0].IsAvailable
            || !string.Equals(installed[0].Version, package.Manifest.Version, StringComparison.Ordinal))
        {
            return [LanguageService.Instance.GetString("Plugin_RuntimeUnavailable", package.Manifest.Id)];
        }
        var missing = PluginPermissions.Missing(PluginPermissions.Derive(package), installed[0].GrantedPermissions);
        if (missing != PluginPermission.None)
        {
            return [LanguageService.Instance.GetString("Plugin_PermissionMissing", DescribePermissions(missing))];
        }
        if (installed[0].TrustStatus is PublisherTrustStatus.UnknownPublisher or PublisherTrustStatus.FingerprintMismatch)
        {
            return [LanguageService.Instance.GetString("Plugin_PublisherUntrusted")];
        }
        return ValidateRequirements(package);
    }
}

/// <summary>
/// Installed visual-plugin metadata and current compatibility state.
/// <para>已安装可视化插件的元数据与当前兼容性状态。</para>
/// </summary>
public sealed class VisualPluginRegistration
{
    internal VisualPluginRegistration(string filePath, VisualPluginPackage package, bool enabled,
        IReadOnlyList<string> errors, PluginPermission grantedPermissions = PluginPermission.None,
        PublisherTrustStatus trustStatus = PublisherTrustStatus.Unsigned)
    {
        FilePath = filePath;
        Package = package;
        IsEnabled = enabled;
        Errors = errors;
        GrantedPermissions = grantedPermissions;
        TrustStatus = trustStatus;
    }

    internal static VisualPluginRegistration Invalid(string path, string error) => new(path,
        new VisualPluginPackage
        {
            Manifest = new VisualPluginManifest
            {
                Id = Path.GetFileNameWithoutExtension(path),
                Name = Path.GetFileName(path),
                Version = "?"
            }
        }, false, [error], PluginPermission.None, PublisherTrustStatus.Unsigned);

    /// <summary>
    /// Managed file holding this package.
    /// <para>存放该包的受管文件。</para>
    /// </summary>
    /// <value>
    /// Full path of the installed package file.
    /// <para>已安装包文件的完整路径。</para>
    /// </value>
    public string FilePath { get; }
    /// <summary>
    /// Parsed package content.
    /// <para>解析后的包内容。</para>
    /// </summary>
    /// <value>
    /// The package this registration describes.
    /// <para>该注册信息所描述的包。</para>
    /// </value>
    public VisualPluginPackage Package { get; }
    /// <summary>
    /// Identifier declared by the package manifest.
    /// <para>包清单声明的标识。</para>
    /// </summary>
    /// <value>
    /// The package identifier.
    /// <para>包的标识。</para>
    /// </value>
    public string Id => Package.Manifest.Id;
    /// <summary>
    /// Display name declared by the package manifest.
    /// <para>包清单声明的显示名称。</para>
    /// </summary>
    /// <value>
    /// The package name.
    /// <para>包的名称。</para>
    /// </value>
    public string Name => Package.Manifest.Name;
    /// <summary>
    /// Version declared by the package manifest.
    /// <para>包清单声明的版本。</para>
    /// </summary>
    /// <value>
    /// The package version text.
    /// <para>包的版本文本。</para>
    /// </value>
    public string Version => Package.Manifest.Version;
    /// <summary>
    /// Author declared by the package manifest.
    /// <para>包清单声明的作者。</para>
    /// </summary>
    /// <value>
    /// The package author text.
    /// <para>包作者文本。</para>
    /// </value>
    public string Author => Package.Manifest.Author;
    /// <summary>
    /// Description declared by the package manifest.
    /// <para>包清单声明的说明。</para>
    /// </summary>
    /// <value>
    /// The package description text.
    /// <para>包的说明文本。</para>
    /// </value>
    public string Description => Package.Manifest.Description;
    /// <summary>
    /// Package kind controlling how the entry is activated.
    /// <para>决定该条目如何激活的包类型。</para>
    /// </summary>
    /// <value>
    /// The declared package kind.
    /// <para>声明的包类型。</para>
    /// </value>
    public VisualPluginKind Kind => Package.Kind;
    /// <summary>
    /// Whether the package is currently enabled.
    /// <para>该包当前是否已启用。</para>
    /// </summary>
    /// <value>
    /// True when enabled, otherwise false.
    /// <para>已启用时为 true，否则为 false。</para>
    /// </value>
    public bool IsEnabled { get; }
    /// <summary>
    /// Permissions the user granted to this package.
    /// <para>用户为该包授予的权限。</para>
    /// </summary>
    /// <value>The stored grant, restored from the manager state file; <see cref="PluginPermission.None"/> when nothing was granted.
    /// <para>从管理器状态文件恢复的授权；未授予任何权限时为 <see cref="PluginPermission.None"/>。</para></value>
    public PluginPermission GrantedPermissions { get; }
    /// <summary>
    /// Trust relationship between the package publisher and this user profile.
    /// <para>包发布者与当前用户配置之间的信任关系。</para>
    /// </summary>
    /// <value><see cref="PublisherTrustStatus.Unsigned"/> for a package without a signature.
    /// <para>无签名包为 <see cref="PublisherTrustStatus.Unsigned"/>。</para></value>
    public PublisherTrustStatus TrustStatus { get; }
    /// <summary>
    /// Whether the package carries a publisher signature.
    /// <para>包是否携带发布者签名。</para>
    /// </summary>
    /// <value>True when a signature is present; its validity was checked during load.
    /// <para>存在签名时为 true；加载期间已校验其有效性。</para></value>
    public bool IsSigned => Package.IsSigned;
    /// <summary>
    /// Publisher id bound by the signature, or an empty string when unsigned.
    /// <para>由签名绑定的发布者标识；未签名时为空字符串。</para>
    /// </summary>
    /// <value>The stable id from the embedded publisher identity.
    /// <para>内嵌发布者身份的稳定标识。</para></value>
    public string PublisherId => Package.Manifest.Publisher?.Id ?? string.Empty;
    /// <summary>
    /// Publisher display text combining name and id.
    /// <para>组合名称与标识的发布者显示文本。</para>
    /// </summary>
    /// <value>Empty when the package declares no publisher.
    /// <para>包未声明发布者时为空。</para></value>
    public string PublisherText => Package.Manifest.Publisher is { } publisher
        ? string.IsNullOrWhiteSpace(publisher.Name) ? publisher.Id : $"{publisher.Name} · {publisher.Id}"
        : string.Empty;
    /// <summary>
    /// Fingerprint pinned or observed for the publisher.
    /// <para>为发布者固定或观察到的指纹。</para>
    /// </summary>
    /// <value>The canonical fingerprint, or an empty string when unsigned.
    /// <para>规范指纹；未签名时为空字符串。</para></value>
    public string FingerprintText => Package.Manifest.Publisher?.Fingerprint ?? string.Empty;
    /// <summary>
    /// Localized description of the permissions the package requests.
    /// <para>包所请求权限的本地化说明。</para>
    /// </summary>
    /// <value>The declared and graph-derived permissions rendered with localized labels.
    /// <para>以本地化标签呈现的声明权限与图推导权限。</para></value>
    public string PermissionText => VisualPluginManager.DescribePermissions(PluginPermissions.Derive(Package));
    /// <summary>
    /// Localized description of the permissions the user granted.
    /// <para>用户已授权限的本地化说明。</para>
    /// </summary>
    /// <value>The stored grant rendered with localized labels.
    /// <para>以本地化标签呈现的已授权集合。</para></value>
    public string GrantedPermissionText => VisualPluginManager.DescribePermissions(GrantedPermissions);
    /// <summary>
    /// Localized trust state text.
    /// <para>本地化的信任状态文本。</para>
    /// </summary>
    /// <value>One of the trust state labels for the current relationship.
    /// <para>当前信任关系对应的一个状态标签。</para></value>
    public string TrustText => LanguageService.Instance.GetString(TrustStatus switch
    {
        PublisherTrustStatus.Trusted => "Plugin_Trust_Trusted",
        PublisherTrustStatus.UnknownPublisher => "Plugin_Trust_Unknown",
        PublisherTrustStatus.FingerprintMismatch => "Plugin_Trust_Changed",
        _ => "Plugin_Trust_Unsigned"
    });
    /// <summary>
    /// Whether the publisher can be trusted from the manager.
    /// <para>是否可以从管理器信任该发布者。</para>
    /// </summary>
    /// <value>True for a signed package whose publisher is unknown or rotated.
    /// <para>发布者未知或已轮换的已签名包为 true。</para></value>
    public bool ShowTrustAction => IsSigned && TrustStatus is PublisherTrustStatus.UnknownPublisher
        or PublisherTrustStatus.FingerprintMismatch;
    /// <summary>
    /// Whether the publisher pin can be removed from the manager.
    /// <para>是否可以从管理器移除该发布者的固定记录。</para>
    /// </summary>
    /// <value>True for a signed package whose publisher is currently trusted.
    /// <para>发布者当前受信任的已签名包为 true。</para></value>
    public bool ShowUntrustAction => IsSigned && TrustStatus == PublisherTrustStatus.Trusted;
    /// <summary>
    /// Whether the package is a workflow template.
    /// <para>该包是否为工作流模板。</para>
    /// </summary>
    /// <value>
    /// True for template packages, otherwise false.
    /// <para>模板包为 true，否则为 false。</para>
    /// </value>
    public bool IsTemplate => Kind == VisualPluginKind.WorkflowTemplate;
    /// <summary>
    /// Whether a disabled node package can be enabled.
    /// <para>已禁用的节点包是否可以启用。</para>
    /// </summary>
    /// <value>
    /// True when an enable action applies, otherwise false.
    /// <para>需要启用操作时为 true，否则为 false。</para>
    /// </value>
    public bool ShowEnableAction => Kind is VisualPluginKind.CompositeNode or VisualPluginKind.ScriptNode && !IsEnabled;
    /// <summary>
    /// Whether an enabled node package can be disabled.
    /// <para>已启用的节点包是否可以禁用。</para>
    /// </summary>
    /// <value>
    /// True when a disable action applies, otherwise false.
    /// <para>需要禁用操作时为 true，否则为 false。</para>
    /// </value>
    public bool ShowDisableAction => Kind is VisualPluginKind.CompositeNode or VisualPluginKind.ScriptNode && IsEnabled;
    /// <summary>
    /// Localized label for the edit action, matching the package kind.
    /// <para>编辑操作的本地化标签，与包类型相匹配。</para>
    /// </summary>
    /// <value>
    /// The localized edit label.
    /// <para>本地化的编辑标签。</para>
    /// </value>
    public string EditActionText => LanguageService.Instance.GetString(
        Kind == VisualPluginKind.ScriptNode ? "Plugin_EditScript" : "Plugin_EditGraph");
    /// <summary>
    /// Localized label describing the package kind.
    /// <para>描述包类型的本地化标签。</para>
    /// </summary>
    /// <value>
    /// The localized kind label.
    /// <para>本地化的类型标签。</para>
    /// </value>
    public string KindText => LanguageService.Instance.GetString(Kind switch
    {
        VisualPluginKind.WorkflowTemplate => "Plugin_Template",
        VisualPluginKind.ScriptNode => "Plugin_Script",
        _ => "Plugin_Composite"
    });
    /// <summary>
    /// Validation and dependency problems found for this package.
    /// <para>为该包发现的校验与依赖问题。</para>
    /// </summary>
    /// <value>
    /// The collected problem messages.
    /// <para>收集到的问题消息。</para>
    /// </value>
    public IReadOnlyList<string> Errors { get; }
    /// <summary>
    /// Whether the package passed every check.
    /// <para>该包是否通过了全部检查。</para>
    /// </summary>
    /// <value>
    /// True when no problem was found, otherwise false.
    /// <para>未发现问题时为 true，否则为 false。</para>
    /// </value>
    public bool IsAvailable => Errors.Count == 0;
    /// <summary>
    /// Node types the package requires in order to run.
    /// <para>该包运行所依赖的节点类型。</para>
    /// </summary>
    /// <value>
    /// The required node types joined for display.
    /// <para>拼接以便显示的必需节点类型。</para>
    /// </value>
    public string RequiredNodes => string.Join(", ", Package.Manifest.RequiredNodeTypes);
    /// <summary>
    /// Capabilities declared by the package manifest.
    /// <para>包清单声明的能力。</para>
    /// </summary>
    /// <value>
    /// The declared capabilities joined for display.
    /// <para>拼接以便显示的声明能力。</para>
    /// </value>
    public string Capabilities => string.Join(", ", Package.Manifest.Capabilities);
    /// <summary>
    /// Platforms the package declares support for.
    /// <para>该包声明支持的平台。</para>
    /// </summary>
    /// <value>
    /// The declared platform names, or the full platform set when none were declared.
    /// <para>声明的平台名；未声明时列出全部平台。</para>
    /// </value>
    public string Platforms => Package.Manifest.Platforms.Count == 0
        ? "Windows / Linux / macOS"
        : string.Join(", ", Package.Manifest.Platforms);
    /// <summary>
    /// Package dependencies shown in the manager detail view.
    /// <para>管理器详情区展示的包依赖。</para>
    /// </summary>
    /// <value>
    /// The declared dependencies joined for display, each rendered as an id with its optional version range; empty when the package declares none.
    /// <para>拼接以便显示的声明依赖，每项渲染为标识及其可选版本范围；包未声明依赖时为空。</para>
    /// </value>
    public string Dependencies => Package.Manifest.Dependencies.Count == 0
        ? string.Empty
        : string.Join(", ", Package.Manifest.Dependencies.Select(FormatDependency));
    /// <summary>
    /// Package conflicts shown in the manager detail view.
    /// <para>管理器详情区展示的包冲突。</para>
    /// </summary>
    /// <value>
    /// The declared conflicting package ids joined for display; empty when the package declares none.
    /// <para>拼接以便显示的冲突包标识；包未声明冲突时为空。</para>
    /// </value>
    public string Conflicts => string.Join(", ", Package.Manifest.Conflicts);
    /// <summary>
    /// Raw validation and dependency messages.
    /// <para>原始校验与依赖消息。</para>
    /// </summary>
    /// <value>
    /// The problem messages joined by line breaks.
    /// <para>以换行拼接的问题消息。</para>
    /// </value>
    public string ErrorText => string.Join(Environment.NewLine, Errors);
    /// <summary>
    /// Localized compatibility explanation retaining the raw validation details.
    /// <para>本地化的兼容性说明，并保留原始校验细节。</para>
    /// </summary>
    /// <value>
    /// The formatted explanation, empty when the package is available.
    /// <para>格式化后的说明；包可用时为空。</para>
    /// </value>
    public string DisplayErrorText => IsAvailable ? string.Empty : UserError.WithDetails(ErrorText, "UserError_PluginUnavailable");

    private static string FormatDependency(VisualPluginDependency dependency)
        => dependency.MinimumVersion is null && dependency.MaximumVersionExclusive is null
            ? dependency.Id
            : $"{dependency.Id} [{dependency.MinimumVersion ?? "*"}, {dependency.MaximumVersionExclusive ?? "*"})";
}

internal sealed class VisualPluginManagerState
{
    public List<string> DisabledIds { get; set; } = [];

    public Dictionary<string, List<string>> Grants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

[JsonSerializable(typeof(VisualPluginManagerState))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class VisualPluginManagerJsonContext : JsonSerializerContext;