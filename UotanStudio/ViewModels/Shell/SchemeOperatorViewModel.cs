using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Projects;
using UotanStudio.Projects.Schemes;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Ui.Projects;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Read-only operator view model for a <c>.uotanscheme</c> package: declared metadata, the generated
/// input form, assets, per-stage run status, resume selection, logs and the <c>SchemeRunner</c> run.
/// <para><c>.uotanscheme</c> 软件包的只读操作员视图模型：声明元数据、生成的输入表单、资产、逐阶段运行状态、
/// 续跑选择、日志，以及 <c>SchemeRunner</c> 运行。</para>
/// </summary>
public sealed partial class SchemeOperatorViewModel : ObservableObject
{
    private readonly Func<SchemePackage, SchemeRunOptions, CancellationToken, Task<SchemeRunResult>> _runAsync;
    private readonly Func<string, CancellationToken, Task<WorkflowSnapshot?>> _workflowLoader;
    private readonly PublisherTrustStore _trustStore;
    private readonly Func<string, string, string, Task<bool>>? _confirm;
    private readonly Func<string?> _projectRootProvider;
    private readonly Func<string?> _temporaryDirectory;
    private SchemePackage? _package;
    private SchemeRunResult? _lastResult;
    private CancellationTokenSource? _runCts;

    /// <summary>
    /// Creates the operator view model backed by <see cref="SchemeRunner"/> and the active project.
    /// <para>创建由 <see cref="SchemeRunner"/> 与当前工程支撑的操作员视图模型。</para>
    /// </summary>
    public SchemeOperatorViewModel()
        : this(null, null)
    {
    }

    /// <summary>
    /// Creates the operator view model with optional runner and workflow-loader overrides.
    /// <para>创建可替换运行器与工作流加载器的操作员视图模型。</para>
    /// </summary>
    /// <param name="runAsync">The runner delegate, or <see langword="null"/> to use <see cref="SchemeRunner"/>.
    /// <para>运行器委托；为 <see langword="null"/> 时使用 <see cref="SchemeRunner"/>。</para></param>
    /// <param name="workflowLoader">The project-workflow loader, or <see langword="null"/> to read the active project.
    /// <para>项目工作流加载器；为 <see langword="null"/> 时读取当前工程。</para></param>
    internal SchemeOperatorViewModel(
        Func<SchemePackage, SchemeRunOptions, CancellationToken, Task<SchemeRunResult>>? runAsync,
        Func<string, CancellationToken, Task<WorkflowSnapshot?>>? workflowLoader)
        : this(runAsync, workflowLoader, null, null, null, null)
    {
    }

    /// <summary>
    /// Creates the operator view model with a signature confirmation prompt and library defaults.
    /// <para>创建带签名确认提示、其余使用库默认的操作员视图模型。</para>
    /// </summary>
    /// <param name="confirm">Signature confirmation delegate receiving the title, message and accept label.
    /// <para>签名确认委托，接收标题、正文与接受按钮文本。</para></param>
    internal SchemeOperatorViewModel(Func<string, string, string, Task<bool>> confirm)
        : this(null, null, null, confirm, null, null)
    {
    }

    /// <summary>
    /// Creates the operator view model with optional runner, workflow, trust-store, confirmation and path overrides.
    /// <para>创建可替换运行器、工作流、信任库、确认与路径来源的操作员视图模型。</para>
    /// </summary>
    /// <param name="runAsync">The runner delegate, or <see langword="null"/> to use <see cref="SchemeRunner"/>.
    /// <para>运行器委托；为 <see langword="null"/> 时使用 <see cref="SchemeRunner"/>。</para></param>
    /// <param name="workflowLoader">The project-workflow loader, or <see langword="null"/> to read the active project.
    /// <para>项目工作流加载器；为 <see langword="null"/> 时读取当前工程。</para></param>
    /// <param name="trustStore">Publisher pin store, or <see langword="null"/> for the per-user store.
    /// <para>发布者固定记录库；为 <see langword="null"/> 时使用按用户存储。</para></param>
    /// <param name="confirm">Signature confirmation delegate, or <see langword="null"/> to accept unsigned packages without a prompt.
    /// <para>签名确认委托；为 <see langword="null"/> 时不再提示地接受未签名软件包。</para></param>
    /// <param name="projectRootProvider">Project root provider used as the asset fallback, or <see langword="null"/> for the project session.
    /// <para>用作资产回退的工程根提供者；为 <see langword="null"/> 时读取工程会话。</para></param>
    /// <param name="temporaryDirectory">Parent directory for the run asset directory, or <see langword="null"/> for the host temp directory.
    /// <para>运行资产目录的父目录；为 <see langword="null"/> 时使用主机临时目录。</para></param>
    internal SchemeOperatorViewModel(
        Func<SchemePackage, SchemeRunOptions, CancellationToken, Task<SchemeRunResult>>? runAsync,
        Func<string, CancellationToken, Task<WorkflowSnapshot?>>? workflowLoader,
        PublisherTrustStore? trustStore,
        Func<string, string, string, Task<bool>>? confirm,
        Func<string?>? projectRootProvider,
        Func<string?>? temporaryDirectory)
    {
        _runAsync = runAsync ?? RunWithSchemeRunnerAsync;
        _workflowLoader = workflowLoader ?? LoadProjectWorkflowAsync;
        _trustStore = trustStore ?? new PublisherTrustStore();
        _confirm = confirm;
        _projectRootProvider = projectRootProvider ?? LoadProjectRoot;
        _temporaryDirectory = temporaryDirectory ?? (() => null);
    }

    /// <summary>
    /// Scheme file last opened by the operator.
    /// <para>操作员最近打开的 Scheme 文件。</para>
    /// </summary>
    [ObservableProperty]
    private string _filePath = string.Empty;

    /// <summary>
    /// Stable scheme identifier.
    /// <para>稳定的 Scheme 标识符。</para>
    /// </summary>
    [ObservableProperty]
    private string _schemeId = string.Empty;

    /// <summary>
    /// Display name of the scheme.
    /// <para>Scheme 的显示名称。</para>
    /// </summary>
    [ObservableProperty]
    private string _schemeName = string.Empty;

    /// <summary>
    /// Author-declared scheme version.
    /// <para>作者声明的 Scheme 版本。</para>
    /// </summary>
    [ObservableProperty]
    private string _schemeVersion = string.Empty;

    /// <summary>
    /// Optional scheme description.
    /// <para>可选的 Scheme 说明。</para>
    /// </summary>
    [ObservableProperty]
    private string _schemeDescription = string.Empty;

    /// <summary>
    /// Generated input form in declaration order.
    /// <para>按声明顺序生成的输入表单。</para>
    /// </summary>
    public ObservableCollection<SchemeInputViewModel> Inputs { get; } = [];

    /// <summary>
    /// Declared assets with their latest materialization state.
    /// <para>声明的资产及其最近一次物化状态。</para>
    /// </summary>
    public ObservableCollection<SchemeAssetRunViewModel> Assets { get; } = [];

    /// <summary>
    /// Declared stages in execution order, carrying the latest run status.
    /// <para>按执行顺序排列的声明阶段，携带最近一次运行状态。</para>
    /// </summary>
    public ObservableCollection<SchemeStageRunViewModel> Stages { get; } = [];

    /// <summary>
    /// Resume choices: "run every stage" followed by each declared stage.
    /// <para>续跑选择：“运行全部阶段”在前，随后是每个声明阶段。</para>
    /// </summary>
    public ObservableCollection<SchemeResumeOption> ResumeOptions { get; } = [];

    /// <summary>
    /// Stage lines written to the unified log by the latest run.
    /// <para>最近一次运行写入统一日志的阶段行。</para>
    /// </summary>
    public ObservableCollection<string> LogEntries { get; } = [];

    /// <summary>
    /// Selected resume point; the default runs every stage.
    /// <para>选中的续跑点；默认运行全部阶段。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResumeFromStageId))]
    private SchemeResumeOption? _selectedResumeOption;

    /// <summary>
    /// Whether a run is currently in progress.
    /// <para>当前是否有运行进行中。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    private bool _isRunning;

    /// <summary>
    /// Run progress in the range 0 to 1; the window shows an indeterminate bar while a run is active.
    /// <para>运行进度，范围 0 到 1；运行期间窗口显示不确定进度条。</para>
    /// </summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>
    /// Caption describing the current run status.
    /// <para>描述当前运行状态的标题。</para>
    /// </summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// Localized aggregate result, empty before the first run.
    /// <para>本地化的总体结果；首次运行前为空。</para>
    /// </summary>
    [ObservableProperty]
    private string _resultText = string.Empty;

    /// <summary>
    /// Contract or execution error of the latest run.
    /// <para>最近一次运行的契约或执行错误。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    /// <summary>
    /// Identifier of the stage that stopped the latest run.
    /// <para>停止最近一次运行的阶段标识符。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailedStage))]
    [NotifyPropertyChangedFor(nameof(FailedStageText))]
    private string? _failedStageId;

    /// <summary>
    /// Aggregate status reported by the latest run.
    /// <para>最近一次运行报告的总体状态。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private ExecutionStatus? _runStatus;

    /// <summary>
    /// Trust relationship of the loaded publisher, or <see langword="null"/> when no signed package is presented.
    /// <para>已加载发布者的信任关系；未呈现已签名软件包时为 <see langword="null"/>。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSigned))]
    [NotifyPropertyChangedFor(nameof(HasSignatureWarning))]
    [NotifyPropertyChangedFor(nameof(ShowTrustPublisher))]
    [NotifyPropertyChangedFor(nameof(SignatureText))]
    [NotifyPropertyChangedFor(nameof(SignatureStatusKey))]
    private PublisherTrustStatus? _signatureStatus;

    /// <summary>
    /// Publisher display text combining the name and identifier, empty when unsigned.
    /// <para>组合名称与标识符的发布者显示文本；未签名时为空。</para>
    /// </summary>
    [ObservableProperty]
    private string _publisherText = string.Empty;

    /// <summary>
    /// Pinned public-key fingerprint of the loaded publisher, empty when unsigned.
    /// <para>已加载发布者固定的公钥指纹；未签名时为空。</para>
    /// </summary>
    [ObservableProperty]
    private string _fingerprintText = string.Empty;

    /// <summary>
    /// Whether a scheme package is loaded.
    /// <para>是否已加载 Scheme 软件包。</para>
    /// </summary>
    public bool HasScheme => _package is not null;

    /// <summary>
    /// Whether the loaded scheme declares any input.
    /// <para>已加载的 Scheme 是否声明了输入。</para>
    /// </summary>
    public bool HasInputs => Inputs.Count > 0;

    /// <summary>
    /// Whether the loaded scheme declares any asset.
    /// <para>已加载的 Scheme 是否声明了资产。</para>
    /// </summary>
    public bool HasAssets => Assets.Count > 0;

    /// <summary>
    /// Whether the loaded scheme declares any stage.
    /// <para>已加载的 Scheme 是否声明了阶段。</para>
    /// </summary>
    public bool HasStages => Stages.Count > 0;

    /// <summary>
    /// Whether the latest run produced log lines.
    /// <para>最近一次运行是否产生日志行。</para>
    /// </summary>
    public bool HasLog => LogEntries.Count > 0;

    /// <summary>
    /// Whether the latest run produced an aggregate status.
    /// <para>最近一次运行是否产生总体状态。</para>
    /// </summary>
    public bool HasResult => RunStatus is not null;

    /// <summary>
    /// Whether the latest run carries an error.
    /// <para>最近一次运行是否携带错误。</para>
    /// </summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// Whether a stage stopped the latest run.
    /// <para>最近一次运行是否由某个阶段停止。</para>
    /// </summary>
    public bool HasFailedStage => !string.IsNullOrEmpty(FailedStageId);

    /// <summary>
    /// Localized failure line naming the stage that stopped the run; empty when no stage failed.
    /// <para>指明停止运行阶段的本地化失败行；没有阶段失败时为空。</para>
    /// </summary>
    public string FailedStageText => HasFailedStage
        ? string.Format(CultureInfo.CurrentCulture, LanguageService.Instance.GetString("Node_OperationFailed"), FailedStageId)
        : string.Empty;

    /// <summary>
    /// Whether a run can start: a package is loaded and no run is active.
    /// <para>是否可以开始运行：已加载软件包且没有运行进行中。</para>
    /// </summary>
    public bool CanRun => _package is not null && !IsRunning;

    /// <summary>
    /// Whether the active run can be cancelled.
    /// <para>是否可以取消当前运行。</para>
    /// </summary>
    public bool CanStop => IsRunning;

    /// <summary>
    /// Stage id selected for resumption, or <see langword="null"/> to run every stage.
    /// <para>选中用于续跑的阶段 ID；为 <see langword="null"/> 时运行全部阶段。</para>
    /// </summary>
    public string? ResumeFromStageId => SelectedResumeOption?.StageId;

    /// <summary>
    /// Whether the loaded package carries a publisher identity and signature.
    /// <para>已加载的软件包是否携带发布者身份与签名。</para>
    /// </summary>
    public bool IsSigned => _package?.IsSigned == true;

    /// <summary>
    /// Whether the loaded publisher is unknown or rotated and must be confirmed before a run.
    /// <para>已加载发布者未知或已轮换，运行前必须确认。</para>
    /// </summary>
    public bool HasSignatureWarning => SignatureStatus is PublisherTrustStatus.UnknownPublisher
        or PublisherTrustStatus.FingerprintMismatch;

    /// <summary>
    /// Whether the explicit trust action is available for the loaded publisher.
    /// <para>已加载发布者是否可以使用显式信任操作。</para>
    /// </summary>
    public bool ShowTrustPublisher => IsSigned && SignatureStatus is PublisherTrustStatus.UnknownPublisher
        or PublisherTrustStatus.FingerprintMismatch;

    /// <summary>
    /// Localization key describing <see cref="SignatureStatus"/>.
    /// <para>描述 <see cref="SignatureStatus"/> 的本地化键。</para>
    /// </summary>
    public string SignatureStatusKey => SignatureStatus switch
    {
        PublisherTrustStatus.Trusted => "Plugin_Trust_Trusted",
        PublisherTrustStatus.UnknownPublisher => "Plugin_Trust_Unknown",
        PublisherTrustStatus.FingerprintMismatch => "Plugin_Trust_Changed",
        _ => "Plugin_Trust_Unsigned"
    };

    /// <summary>
    /// Localized signature state caption.
    /// <para>本地化的签名状态标题。</para>
    /// </summary>
    public string SignatureText => LanguageService.Instance.GetString(SignatureStatusKey);

    /// <summary>
    /// Loads a scheme package from disk and rebuilds the operator form.
    /// <para>从磁盘加载 Scheme 软件包并重建操作员表单。</para>
    /// </summary>
    /// <param name="path">The scheme file to read. <para>要读取的 Scheme 文件。</para></param>
    /// <param name="cancellationToken">Cancels the bounded read. <para>取消有界读取。</para></param>
    /// <returns><see langword="true"/> when an unsigned or validly signed package was loaded; <see langword="false"/>
    /// when the file is unreadable or its signature no longer matches its content.
    /// <para>成功加载未签名或签名有效的软件包时返回 <see langword="true"/>；文件不可读或其签名与内容不再匹配时返回 <see langword="false"/>。</para></returns>
    public async Task<bool> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var package = await SchemeFile.LoadAsync(path, cancellationToken).ConfigureAwait(true);
        if (package is null)
        {
            ClearPackageState();
            ErrorMessage = LanguageService.Instance.GetString("Menu_OpenFailed");
            ResultText = string.Empty;
            return false;
        }

        var verification = SchemeSigner.Verify(package);
        if (verification.IsSigned && !verification.IsValid)
        {
            ClearPackageState();
            SignatureStatus = null;
            ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                LanguageService.Instance.GetString("Node_OperationFailed"), verification.Error);
            ResultText = LanguageService.Instance.GetString("Scheme_RunFailed");
            return false;
        }

        if (!SetPackage(package, path))
        {
            return false;
        }
        await RefreshAssetStatusesAsync(cancellationToken).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Replaces the presented package and rebuilds inputs, assets, stages and resume choices.
    /// <para>替换当前呈现的软件包，并重建输入、资产、阶段与续跑选择。</para>
    /// </summary>
    /// <param name="package">The package to present; must not be <see langword="null"/>.
    /// <para>要呈现的软件包；不能为 <see langword="null"/>。</para></param>
    /// <param name="filePath">The originating file path, or <see langword="null"/> for an in-memory package.
    /// <para>来源文件路径；内存中的软件包为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when the package is presented; <see langword="false"/> when its signed content does not verify.
    /// <para>已呈现软件包时返回 <see langword="true"/>；已签名内容无法通过校验时返回 <see langword="false"/>。</para></returns>
    public bool SetPackage(SchemePackage package, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        package.Normalize();
        var verification = SchemeSigner.Verify(package);
        if (verification.IsSigned && !verification.IsValid)
        {
            ClearPackageState();
            ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                LanguageService.Instance.GetString("Node_OperationFailed"), verification.Error);
            ResultText = LanguageService.Instance.GetString("Scheme_RunFailed");
            return false;
        }
        _package = package;
        _lastResult = null;
        FilePath = filePath ?? string.Empty;
        SchemeId = package.Id ?? string.Empty;
        SchemeName = package.Name ?? string.Empty;
        SchemeVersion = package.Version ?? string.Empty;
        SchemeDescription = package.Description ?? string.Empty;

        Stages.Clear();
        foreach (var stage in package.Stages)
        {
            Stages.Add(new SchemeStageRunViewModel(stage));
        }

        Inputs.Clear();
        foreach (var input in package.Inputs)
        {
            Inputs.Add(new SchemeInputViewModel(input));
        }

        Assets.Clear();
        foreach (var asset in package.Assets)
        {
            Assets.Add(new SchemeAssetRunViewModel(asset));
        }

        RebuildResumeOptions();
        ResetRunState();
        RefreshSignatureState();
        StatusText = LanguageService.Instance.GetString("Node_Status_Idle");
        NotifyPackageChanged();
        return true;
    }

    /// <summary>
    /// Runs the loaded scheme through <see cref="SchemeRunner"/> and applies the reported stage statuses.
    /// <para>通过 <see cref="SchemeRunner"/> 运行已加载的 Scheme，并应用报告的阶段状态。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the current stage and stops the run.
    /// <para>取消当前阶段并停止运行。</para></param>
    /// <returns><see langword="true"/> when the run started; <see langword="false"/> for no package, a concurrent
    /// run, a missing required input, a declined signature confirmation, an untrusted or rotated publisher, or assets
    /// that cannot be materialized.
    /// <para>运行已启动时返回 <see langword="true"/>；没有软件包、已有运行、缺少必填输入、签名确认被拒绝、
    /// 发布者未信任或已轮换，或资产无法物化时返回 <see langword="false"/>。</para></returns>
    public async Task<bool> RunAsync(CancellationToken cancellationToken = default)
    {
        if (_package is not { } package || IsRunning)
        {
            return false;
        }

        ResetRunState();
        var invalid = Inputs.FirstOrDefault(input => input.HasError);
        if (invalid is not null)
        {
            ErrorMessage = invalid.ValidationMessage;
            ResultText = LanguageService.Instance.GetString("Scheme_RunFailed");
            StatusText = LanguageService.Instance.GetString("Node_Status_Failed");
            return false;
        }

        if (!await ConfirmSignatureAsync().ConfigureAwait(true))
        {
            return false;
        }

        var assets = await PrepareAssetsAsync(package, cancellationToken).ConfigureAwait(true);
        if (assets.Error is not null)
        {
            ErrorMessage = assets.Error;
            ResultText = LanguageService.Instance.GetString("Scheme_RunFailed");
            StatusText = LanguageService.Instance.GetString("Node_Status_Failed");
            return false;
        }

        var startedAt = DateTime.Now;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = cts;
        IsRunning = true;
        StatusText = LanguageService.Instance.GetString("Node_Status_Running");
        try
        {
            var options = new SchemeRunOptions
            {
                Inputs = BuildInputValues(),
                AssetPaths = assets.Paths,
                ResumeFromStageId = ResumeFromStageId,
                CompletedStages = BuildCompletedStages(),
                ProjectWorkflowResolver = await ResolveProjectWorkflowsAsync(package, cancellationToken).ConfigureAwait(true)
            };
            var result = await _runAsync(package, options, cts.Token).ConfigureAwait(true);
            ApplyResult(result);
            return true;
        }
        catch (OperationCanceledException)
        {
            RunStatus = ExecutionStatus.Cancelled;
            StatusText = LanguageService.Instance.GetString("Node_Status_Cancelled");
            ResultText = LanguageService.Instance.GetString("Scheme_RunFailed");
            return false;
        }
        catch (Exception ex)
        {
            RunStatus = ExecutionStatus.Failed;
            StatusText = LanguageService.Instance.GetString("Node_Status_Failed");
            ResultText = LanguageService.Instance.GetString("Scheme_RunFailed");
            ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                LanguageService.Instance.GetString("Node_OperationFailed"), ex.Message);
            return false;
        }
        finally
        {
            assets.Store?.Dispose();
            CaptureLogs(startedAt);
            if (ReferenceEquals(_runCts, cts))
            {
                _runCts = null;
            }
            IsRunning = false;
            Progress = 1;
        }
    }

    /// <summary>
    /// Requests cancellation of the active run; has no effect when no run is active.
    /// <para>请求取消当前运行；没有运行进行中时不产生任何影响。</para>
    /// </summary>
    public void Stop() => _runCts?.Cancel();

    /// <summary>
    /// Pins the loaded publisher after an explicit confirmation prompt and updates the trust state.
    /// <para>在显式确认提示后固定已加载的发布者，并更新信任状态。</para>
    /// </summary>
    /// <returns><see langword="true"/> when the publisher is pinned; <see langword="false"/> when no signed package is
    /// loaded, the user declined, or the trust store rejected the pin.
    /// <para>发布者已固定时返回 <see langword="true"/>；未加载已签名软件包、用户拒绝或信任库拒绝固定时返回 <see langword="false"/>。</para></returns>
    public async Task<bool> ConfirmAndTrustPublisherAsync()
    {
        if (_package is not { IsSigned: true } package || package.Publisher is not { } publisher)
        {
            return false;
        }
        if (_confirm is null)
        {
            return TrustPublisher() is not null;
        }
        var confirmed = await _confirm(
            LanguageService.Instance.GetString("Plugin_TrustConfirmTitle"),
            string.Format(CultureInfo.CurrentCulture, LanguageService.Instance.GetString("Plugin_TrustConfirm"),
                publisher.Fingerprint, publisher.Id),
            LanguageService.Instance.GetString("Plugin_TrustPublisher")).ConfigureAwait(true);
        return confirmed && TrustPublisher() is not null;
    }

    /// <summary>
    /// Pins the loaded publisher fingerprint without prompting; the caller must have obtained explicit confirmation.
    /// <para>不提示地固定已加载的发布者指纹；调用方必须已经取得显式确认。</para>
    /// </summary>
    /// <returns>The stored pin, or <see langword="null"/> when no signed package is loaded or the store rejected it.
    /// <para>已保存的固定记录；未加载已签名软件包或信任库拒绝时为 <see langword="null"/>。</para></returns>
    public PublisherTrustRecord? TrustPublisher()
    {
        if (_package is not { IsSigned: true } package)
        {
            return null;
        }
        try
        {
            var record = SchemeSigner.TrustPublisher(package, _trustStore);
            RefreshSignatureState();
            return record;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException)
        {
            ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                LanguageService.Instance.GetString("Node_OperationFailed"), ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Re-inspects the declared assets without copying and updates the presented asset statuses.
    /// <para>不复制文件地重新检查声明资产，并更新呈现的资产状态。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels hashing. <para>取消哈希计算。</para></param>
    /// <returns>A task that completes when every asset row reflects the latest inspection.
    /// <para>每个资产行都反映最近一次检查结果后完成的任务。</para></returns>
    public async Task RefreshAssetStatusesAsync(CancellationToken cancellationToken = default)
    {
        if (_package is not { } package || package.Assets.Count == 0)
        {
            return;
        }
        var schemeDirectory = SchemeDirectory();
        if (schemeDirectory is null)
        {
            foreach (var asset in Assets)
            {
                asset.Reset();
            }
            return;
        }
        try
        {
            var checks = await SchemeAssetStore.InspectAsync(package, schemeDirectory, _projectRootProvider(),
                cancellationToken).ConfigureAwait(true);
            ApplyAssetChecks(checks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            foreach (var asset in Assets)
            {
                asset.Reset();
            }
            ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                LanguageService.Instance.GetString("Node_OperationFailed"), ex.Message);
        }
    }

    private sealed record AssetPreparation(Dictionary<string, string> Paths, SchemeAssetStore? Store, string? Error);

    private async Task<AssetPreparation> PrepareAssetsAsync(SchemePackage package,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> paths = new(StringComparer.Ordinal);
        if (package.Assets.Count == 0)
        {
            return new AssetPreparation(paths, null, null);
        }
        var schemeDirectory = SchemeDirectory();
        if (schemeDirectory is null)
        {
            ApplyAssetChecks([.. package.Assets.Select(asset => new SchemeAssetCheck
            {
                AssetId = asset.Id,
                State = SchemeAssetState.Missing,
                Error = $"Scheme asset '{asset.Id}' requires a saved scheme file path."
            })]);
            return new AssetPreparation(paths, null,
                "Scheme assets require a saved scheme file path before a run.");
        }
        var destination = SchemeAssetStore.CreateRunDirectory(_temporaryDirectory(), package.Id);
        try
        {
            var checks = await SchemeAssetStore.InspectAsync(package, schemeDirectory, _projectRootProvider(),
                cancellationToken).ConfigureAwait(true);
            ApplyAssetChecks(checks);
            var invalid = checks.FirstOrDefault(check => !check.IsValid);
            if (invalid is not null)
            {
                Directory.Delete(destination, recursive: true);
                return new AssetPreparation(paths, null,
                    invalid.Error ?? $"Scheme asset '{invalid.AssetId}' is not available.");
            }
            var store = await SchemeAssetStore.MaterializeAsync(package, schemeDirectory, destination,
                _projectRootProvider(), deleteOnDispose: true, cancellationToken: cancellationToken)
                .ConfigureAwait(true);
            foreach (var (assetId, path) in store.Paths)
            {
                paths[assetId] = path;
            }
            return new AssetPreparation(paths, store, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(destination);
            return new AssetPreparation(paths, null, ex.Message);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LogService.Instance.Write(LogLevel.Warn, "Scheme", error.Message);
        }
    }

    private async Task<bool> ConfirmSignatureAsync()
    {
        switch (SignatureStatus)
        {
            case PublisherTrustStatus.FingerprintMismatch:
                ErrorMessage = LanguageService.Instance.GetString("Plugin_PublisherFingerprintChanged");
                ResultText = LanguageService.Instance.GetString("Scheme_RunFailed");
                StatusText = LanguageService.Instance.GetString("Node_Status_Failed");
                return false;
            case PublisherTrustStatus.UnknownPublisher:
                {
                    if (!await ConfirmAndTrustPublisherAsync().ConfigureAwait(true))
                    {
                        ErrorMessage = LanguageService.Instance.GetString("Plugin_PublisherUntrusted");
                        ResultText = LanguageService.Instance.GetString("Scheme_RunFailed");
                        StatusText = LanguageService.Instance.GetString("Node_Status_Failed");
                        return false;
                    }
                    return true;
                }
            case PublisherTrustStatus.Unsigned when _confirm is not null:
                {
                    var confirmed = await _confirm(
                        LanguageService.Instance.GetString("Scheme_Run"),
                        LanguageService.Instance.GetString("Plugin_Trust_Unsigned"),
                        LanguageService.Instance.GetString("Scheme_Run")).ConfigureAwait(true);
                    if (!confirmed)
                    {
                        ResultText = LanguageService.Instance.GetString("Scheme_RunFailed");
                        StatusText = LanguageService.Instance.GetString("Node_Status_Idle");
                    }
                    return confirmed;
                }
            default:
                return true;
        }
    }

    private void ApplyAssetChecks(IReadOnlyList<SchemeAssetCheck> checks)
    {
        Dictionary<string, SchemeAssetCheck> byId = new(StringComparer.Ordinal);
        foreach (var check in checks)
        {
            byId[check.AssetId] = check;
        }
        foreach (var asset in Assets)
        {
            if (byId.TryGetValue(asset.AssetId, out var check))
            {
                asset.ApplyCheck(check);
            }
            else
            {
                asset.Reset();
            }
        }
    }

    private string? SchemeDirectory() => string.IsNullOrWhiteSpace(FilePath)
        ? null
        : Path.GetDirectoryName(Path.GetFullPath(FilePath));

    private void RefreshSignatureState()
    {
        if (_package is not { } package)
        {
            SignatureStatus = null;
            PublisherText = string.Empty;
            FingerprintText = string.Empty;
            return;
        }
        SignatureStatus = SchemeSigner.GetTrustStatus(package, _trustStore);
        PublisherText = package.Publisher is { } publisher
            ? string.IsNullOrWhiteSpace(publisher.Name)
                ? publisher.Id
                : $"{publisher.Name} ({publisher.Id})"
            : string.Empty;
        FingerprintText = package.Publisher?.Fingerprint ?? string.Empty;
    }

    private void ClearPackageState()
    {
        _package = null;
        _lastResult = null;
        FilePath = string.Empty;
        SchemeId = string.Empty;
        SchemeName = string.Empty;
        SchemeVersion = string.Empty;
        SchemeDescription = string.Empty;
        Inputs.Clear();
        Assets.Clear();
        Stages.Clear();
        ResumeOptions.Clear();
        SelectedResumeOption = null;
        SignatureStatus = null;
        PublisherText = string.Empty;
        FingerprintText = string.Empty;
        ResetRunState();
        NotifyPackageChanged();
    }

    private static string? LoadProjectRoot() => ProjectContextService.Instance.RootDirectory;


    /// <summary>
    /// Applies one runner result to the stage list, result text and failure details.
    /// <para>把一份运行器结果应用到阶段列表、结果文本与失败详情。</para>
    /// </summary>
    /// <param name="result">The result to apply; must not be <see langword="null"/>.
    /// <para>要应用的结果；不能为 <see langword="null"/>。</para></param>
    internal void ApplyResult(SchemeRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        RunStatus = result.Status;
        Progress = 1;
        foreach (var stage in Stages)
        {
            var match = result.Stages.FirstOrDefault(item => string.Equals(item.StageId, stage.StageId, StringComparison.Ordinal));
            if (match is null)
            {
                stage.Reset();
            }
            else
            {
                stage.ApplyResult(match);
            }
        }

        _lastResult = result;
        FailedStageId = result.FailedStageId;
        ErrorMessage = result.ErrorMessage ?? string.Empty;
        ResultText = LanguageService.Instance.GetString(
            result.Status == ExecutionStatus.Succeeded ? "Scheme_RunSucceeded" : "Scheme_RunFailed");
        StatusText = LanguageService.Instance.GetString(result.Status switch
        {
            ExecutionStatus.Succeeded => "Node_Status_Succeeded",
            ExecutionStatus.Cancelled => "Node_Status_Cancelled",
            _ => "Node_Status_Failed"
        });
    }

    private IReadOnlyList<SchemeStageResult> BuildCompletedStages()
    {
        if (ResumeFromStageId is null || _lastResult is null)
        {
            return [];
        }

        return
        [
            .. _lastResult.Stages.Where(stage =>
                stage.Status is SchemeStageStatus.Executed or SchemeStageStatus.Reused)
        ];
    }

    private Dictionary<string, string> BuildInputValues()
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (var input in Inputs)
        {
            values[input.Id] = input.GetRunValue();
        }
        return values;
    }

    private async Task<Func<string, WorkflowSnapshot?>?> ResolveProjectWorkflowsAsync(SchemePackage package,
        CancellationToken cancellationToken)
    {
        Dictionary<string, WorkflowSnapshot> resolved = new(StringComparer.Ordinal);
        foreach (var stage in package.Stages)
        {
            var name = stage.ProjectWorkflowName;
            if (string.IsNullOrWhiteSpace(name) || resolved.ContainsKey(name))
            {
                continue;
            }

            try
            {
                if (await _workflowLoader(name, cancellationToken).ConfigureAwait(true) is { } snapshot)
                {
                    resolved[name] = snapshot;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Instance.Write(LogLevel.Warn, "Scheme", $"Could not resolve workflow '{name}': {ex.Message}");
            }
        }

        if (resolved.Count == 0)
        {
            return null;
        }

        return name => resolved.GetValueOrDefault(name);
    }

    private void RebuildResumeOptions()
    {
        var previousStageId = SelectedResumeOption?.StageId;
        ResumeOptions.Clear();
        ResumeOptions.Add(new SchemeResumeOption(null, LanguageService.Instance.GetString("Menu_RunWorkflow")));
        for (var index = 0; index < Stages.Count; index++)
        {
            var stage = Stages[index];
            ResumeOptions.Add(new SchemeResumeOption(stage.StageId,
                string.Format(CultureInfo.CurrentCulture, "{0}. {1}", index + 1, stage.DisplayName)));
        }

        SelectedResumeOption = ResumeOptions.FirstOrDefault(option =>
            string.Equals(option.StageId, previousStageId, StringComparison.Ordinal)) ?? ResumeOptions[0];
    }

    private void ResetRunState()
    {
        foreach (var stage in Stages)
        {
            stage.Reset();
        }
        foreach (var asset in Assets)
        {
            asset.Reset();
        }
        LogEntries.Clear();
        ResultText = string.Empty;
        ErrorMessage = string.Empty;
        FailedStageId = null;
        RunStatus = null;
        Progress = 0;
        OnPropertyChanged(nameof(HasLog));
    }

    private void NotifyPackageChanged()
    {
        OnPropertyChanged(nameof(HasScheme));
        OnPropertyChanged(nameof(HasInputs));
        OnPropertyChanged(nameof(HasAssets));
        OnPropertyChanged(nameof(HasStages));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(IsSigned));
        OnPropertyChanged(nameof(HasSignatureWarning));
        OnPropertyChanged(nameof(ShowTrustPublisher));
        OnPropertyChanged(nameof(SignatureStatusKey));
        OnPropertyChanged(nameof(SignatureText));
    }

    private void CaptureLogs(DateTime startedAt)
    {
        LogEntries.Clear();
        foreach (var entry in LogService.Instance.Entries)
        {
            if (entry.Timestamp < startedAt
                || (entry.Source is not ("Scheme" or "Engine")))
            {
                continue;
            }
            LogEntries.Add($"{entry.Level}: {entry.Message}");
            if (LogEntries.Count >= 500)
            {
                break;
            }
        }
        OnPropertyChanged(nameof(HasLog));
    }

    private static Task<SchemeRunResult> RunWithSchemeRunnerAsync(SchemePackage package, SchemeRunOptions options,
        CancellationToken cancellationToken)
        => new SchemeRunner(LogService.Instance).RunAsync(package, options, cancellationToken);

    private static async Task<WorkflowSnapshot?> LoadProjectWorkflowAsync(string name,
        CancellationToken cancellationToken)
    {
        try
        {
            if (ProjectOperationsService.Instance.NodeEditorVM?.FindDocument(name) is { } document)
            {
                return new WorkflowSnapshot
                {
                    Nodes = WorkflowPersistenceService.BuildNodeDtos(document.Nodes),
                    Connections = WorkflowPersistenceService.BuildConnectionDtos(document.Nodes, document.Connections)
                };
            }
        }
        catch (InvalidOperationException)
        {
        }

        var context = ProjectContextService.Instance;
        var registration = context.NodeProjects.FirstOrDefault(node =>
            string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase));
        if (registration is not null && context.ResolveNodeProjectPath(registration) is { } path && File.Exists(path))
        {
            return await WorkflowPersistenceService.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }
}