using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UotanStudio.Delivery;
using UotanStudio.Services;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Ui;
using UotanStudio.Services.Ui.Projects;
using UotanStudio.UiAbstractions;

namespace UotanStudio.Views.Shell;

public partial class DeliveryView
{
    private static readonly HttpClient DeliveryHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _deliverySaveGate = new(1, 1);
    private readonly SemaphoreSlim _deliveryRefreshGate = new(1, 1);
    private readonly DispatcherTimer _deliverySaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private CancellationTokenSource? _deliveryCancellation;
    private TaskCompletionSource? _deliveryCompletion;
    private string? _deliveryRoot;
    private bool _deliveryLoaded;

    private int _loadedSolutionGeneration;
    private bool _restoringDelivery;
    private bool _deliveryChangePending;
    private string _recoveryUpdaterPath = "";

    private void InitializeDelivery()
    {

        ProjectOperationsService.Instance.CaptureDeliveryState = CaptureDeliveryState;
        InitializePayloadSigningKeys();
        ManageZipKeysButton.Click += (_, _) => UiWindowService.Shared.ShowHostWindow(HostWindowKind.KeyManager);
        InitializeZipSigningKeys();
        Composition.ReadOptions = ReadDeliverySuperOptions;
        Composition.ReadPlan = CaptureDeliveryPlan;
        Composition.Changed += (_, _) => QueueDeliveryChange();
        Composition.OutputView.Changed += (_, _) => QueueDeliveryChange();
        Composition.OutputView.GenerateRequested += async (_, _) => await GenerateScriptsAsync();
        Composition.OutputView.SaveRequested += async (_, _) =>
        {
            if (_deliveryCancellation is null) await SaveDeliveryAsync(true);
        };
        Composition.OutputRequested += async (_, _) => await RunDeliveryAsync();
        Composition.OperationFailed += async (_, error) => await ShowDeliveryMessageAsync(UserError.Describe(error), true);
        Composition.Model.NoticeRequested += async (_, message) => await ShowDeliveryMessageAsync(message, false);
        Composition.Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Composition.Model.Status)) ToolTip.SetTip(Composition, Composition.Model.Status);
        };
        foreach (var box in new[] { OutputDirectoryBox, SuperSizeBox, SuperGroupNameBox, SuperDeviceNameBox,
                     SuperMetadataSizeBox, SuperBlockSizeBox, SuperMetadataSlotsBox, PayloadVersionBox, PayloadMinorVersionBox,
                     PayloadBuildFingerprintBox, PayloadIncrementalBox, PayloadSdkLevelBox,
                     DynamicPartitionsFileBox, PayloadPostinstallBox, PayloadTimestampBox, PayloadSecurityPatchBox })
            box.TextChanged += (_, _) => QueueDeliveryChange();
        foreach (var box in new ToggleButton[] { DirectOutputBox, PackSuperBox, PackPayloadBox, SuperRawBox, SuperSparseBox,
                     SuperZstdBox, SuperZstdChunkedBox, SuperVabBox, SuperAbBox, SuperAOnlyBox, GenerateSuperEmptyBox })
            box.IsCheckedChanged += (_, _) => QueueDeliveryChange();

        SuperZstdBox.IsCheckedChanged += (_, _) =>
        {
            if (SuperZstdBox.IsChecked == true && GenerateSuperEmptyBox.IsChecked == true) GenerateSuperEmptyBox.IsChecked = false;
            UpdateSuperFormatOptions();
        };
        GenerateSuperEmptyBox.IsCheckedChanged += (_, _) =>
        {

            Composition.SuperEmptyOnly = GenerateSuperEmptyBox.IsChecked == true;
            if (GenerateSuperEmptyBox.IsChecked == true && SuperZstdBox.IsChecked == true) SuperZstdBox.IsChecked = false;
            UpdateSidebarLock();
        };
        GenerateDynamicPartitionsButton.Click += async (_, _) => await GenerateDynamicPartitionsAsync();
        _deliverySaveTimer.Tick += async (_, _) => { _deliverySaveTimer.Stop(); await SaveDeliveryAsync(false); };
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.S && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            { e.Handled = true; await SaveDeliveryAsync(true); }
        };
    }

    private bool _generatingScripts;

    internal async Task GenerateScriptsAsync()
    {
        if (_generatingScripts || _deliveryCancellation is not null || _restoringDelivery || Composition.IsSuperEmptyOnly) return;
        _generatingScripts = true;
        var root = _deliveryRoot;
        try
        {
            var plan = CaptureDeliveryPlan();
            if (!Composition.OutputView.SynchronizeScripts(plan, force: true))
            {
                if (TopLevel.GetTopLevel(this) is not Window owner || !await DialogService.ConfirmAsync(owner,
                        LanguageService.Instance.GetString("Tab_Delivery"),
                        LanguageService.Instance.GetString("Delivery_ReplaceEditedScripts"))) return;
                if (root != _deliveryRoot || _restoringDelivery || _deliveryCancellation is not null) return;
                Composition.OutputView.SynchronizeScripts(CaptureDeliveryPlan(), replaceModified: true, force: true);
            }
            QueueDeliveryChange();
            await SaveDeliveryAsync(false);
        }
        catch (Exception ex) { await ShowDeliveryMessageAsync(UserError.Describe(ex), true); }
        finally { _generatingScripts = false; }
    }

    /// <summary>
    /// Reads the Super geometry from the form. Reading never throws: fields the author has not filled in yet
    /// fall back to the record defaults, so merely opening a project can capture and persist the panel state.
    /// Callers that are about to build or generate something validate the plan instead (see <c>DeliveryValidation</c>).
    /// <para>从表单读取 Super 几何。读取不抛异常：作者尚未填写的字段回退到记录默认值，因此仅打开工程也能捕获并
    /// 持久化面板状态。真正要构建或生成输出的调用方改为校验计划（见 <c>DeliveryValidation</c>）。</para>
    /// </summary>
    /// <returns>The current Super options.<para>当前的 Super 选项。</para></returns>
    private DeliverySuperOptions ReadDeliverySuperOptions()
    {
        static uint Number(string? text, uint fallback) => uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : fallback;
        var defaults = new DeliverySuperOptions();
        _ = ulong.TryParse(SuperSizeBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var size);
        return new()
        {
            Groups = _deliveryGroups,
            DeviceSize = size,
            Group = SuperGroupNameBox.Text?.Trim() ?? "default",
            DeviceName = SuperDeviceNameBox.Text?.Trim() ?? "super",
            MetadataSize = Number(SuperMetadataSizeBox.Text, defaults.MetadataSize),
            Slots = Number(SuperMetadataSlotsBox.Text, defaults.Slots),
            BlockSize = Number(SuperBlockSizeBox.Text, defaults.BlockSize),
            Sparse = SuperSparseBox.IsChecked == true,
            VirtualAb = SuperVabBox.IsChecked == true,
            AutoZero = Composition.AutoZero && SuperAOnlyBox.IsChecked != true,
            DeviceType = SuperVabBox.IsChecked == true ? "VAB" : SuperAbBox.IsChecked == true ? "AB" : "AOnly",
            Zstd = SuperZstdBox.IsChecked == true,
            ZstdChunkedParallel = SuperZstdChunkedBox.IsChecked == true,
            Empty = GenerateSuperEmptyBox.IsChecked == true
        };
    }

    /// <summary>
    /// Reads the Payload options from the form. Like the Super reader this is total: an unparsable version
    /// falls back to the record default so an untouched panel still captures, and the builders re-validate.
    /// <para>从表单读取 Payload 选项。与 Super 读取一致，本方法不做抛出：无法解析的版本号回退到记录默认值，
    /// 使未填写的面板仍可被捕获，构建方再行校验。</para>
    /// </summary>
    /// <returns>The current Payload options.<para>当前的 Payload 选项。</para></returns>
    private DeliveryPayloadOptions ReadDeliveryPayloadOptions()
    {
        var defaults = new DeliveryPayloadOptions();
        _ = uint.TryParse(PayloadVersionBox.Text, out var version);
        _ = uint.TryParse(PayloadMinorVersionBox.Text, out var minor);
        if (version == 0) version = defaults.Version;
        return new()
        {
            BlockSize = uint.TryParse(SuperBlockSizeBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var block) ? block : defaults.BlockSize,
            MaxTimestamp = long.TryParse(PayloadTimestampBox.Text, out var timestamp) ? timestamp : -1,
            SecurityPatchLevel = PayloadSecurityPatchBox.Text?.Trim() ?? "",
            Version = version,
            MinorVersion = minor,
            DynamicPartitionsFile = DynamicPartitionsFileBox.Text?.Trim() ?? "",
            SignatureFile = _legacyPayloadSignatureFile,
            SigningKeyPath = _payloadSigningIdentityPath.Trim(),
            SigningKeyFingerprint = _payloadSigningFingerprint,
            PostinstallFile = PayloadPostinstallBox.Text?.Trim() ?? ""
        };
    }

    private bool _generatingDynamicPartitions;

    private async Task GenerateDynamicPartitionsAsync()
    {
        if (PackPayloadBox.IsChecked != true || _deliveryCancellation is not null || _generatingDynamicPartitions) return;
        _generatingDynamicPartitions = true;
        try
        {
            var text = DeliveryPayloadBuilder.GenerateDynamicLayout(CaptureDeliveryPlan());
            var root = _deliveryRoot;
            if (TopLevel.GetTopLevel(this) is not { StorageProvider.CanSave: true } owner) return;
            using var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            { Title = LanguageService.Instance.GetString("Delivery_DynamicPartitionsFile"), SuggestedFileName = "dynamic_partitions_info.txt", DefaultExtension = "txt" });
            if (root != _deliveryRoot || _deliveryCancellation is not null || PackPayloadBox.IsChecked != true || file?.TryGetLocalPath() is not { } path) return;
            var temporary = path + "." + Guid.NewGuid().ToString("N");
            try { await File.WriteAllTextAsync(temporary, text); File.Move(temporary, path, true); }
            finally { File.Delete(temporary); }
            if (root == _deliveryRoot && PackPayloadBox.IsChecked == true) DynamicPartitionsFileBox.Text = path;
        }
        catch (Exception ex) { await ShowDeliveryMessageAsync(UserError.Describe(ex), true); }
        finally { _generatingDynamicPartitions = false; }
    }

    internal DeliveryPlan CaptureDeliveryPlan() => Composition.OutputView.Capture(new()
    {
        OutputDirectory = OutputDirectoryBox.Text?.Trim() ?? "",
        OrganizeDirectory = DirectOutputBox.IsChecked == true,
        PackPayload = PackPayloadBox.IsChecked == true,
        Payload = ReadDeliveryPayloadOptions(),
        PackSuper = PackSuperBox.IsChecked == true,
        ContainerConfirmed = Composition.Model.Confirmed,
        Super = ReadDeliverySuperOptions(),
        Container = [.. Composition.Model.Container.Select(i => i.Snapshot())]
    }) with
    {
        ArchiveName = ZipArchiveNameBox.Text?.Trim() ?? "",
        Package = new()
        {
            Ota = PackPayloadBox.IsChecked == true,
            RecoveryUpdaterPath = _recoveryUpdaterPath,
            SigningKeyPath = DirectOutputBox.IsChecked == true ? _zipSigningIdentityPath.Trim() : "",
            CertificatePath = DirectOutputBox.IsChecked == true ? _zipSigningCertificatePath.Trim() : "",
            BuildFingerprint = PayloadBuildFingerprintBox.Text?.Trim() ?? "",
            Incremental = PayloadIncrementalBox.Text?.Trim() ?? "",
            SdkLevel = uint.TryParse(PayloadSdkLevelBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var sdk) ? sdk : 0
        }
    };

    /// <summary>
    /// Capture hook invoked before solution saves; returns null (keeping the stored state) when the
    /// form is not loaded or is being restored.
    /// <para>解决方案保存前的捕获钩子：表单未加载或正在恢复时返回 null（保留原状态）。</para>
    /// </summary>
    /// <returns>
    /// The captured state element, or null when capture is skipped.
    /// <para>捕获到的状态元素；跳过捕获时为 null。</para>
    /// </returns>
    private JsonElement? CaptureDeliveryState()
    {
        var root = _deliveryRoot;
        if (root is null || !_deliveryLoaded || _restoringDelivery) return null;
        try { return DeliveryPlanStore.CaptureElement(root, CaptureDeliveryPlan()); }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "Delivery", $"Delivery state capture skipped: {ex.Message}");
            _ = ShowDeliveryMessageAsync(UserError.Describe(ex), true);
            return null;
        }
    }

    private IEnumerable<string> GetReadOnlyDeliverySources() =>
        (Composition.Model.EditingContainer ? Composition.Model.Output : Composition.Model.Container)
        .Where(image => image.FilePath is not null).Select(image => image.FilePath!);

    private void QueueDeliveryChange()
    {
        if (_restoringDelivery || _deliveryChangePending) return;
        _deliveryChangePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _deliveryChangePending = false;
            if (_restoringDelivery || Composition.Model.IsRunning) return;
            GenerateDynamicPartitionsButton.IsEnabled = PackPayloadBox.IsChecked == true && Composition.Model.Container.Any(i => i.IncludeInSuper);
            ImageSelection.SetAssignedPaths(Composition.Model.Container.Concat(Composition.Model.Output)
                .Where(i => i.FilePath is not null).Select(i => i.FilePath!), GetReadOnlyDeliverySources());
            try
            {
                if (!Composition.IsSuperEmptyOnly)
                {
                    var plan = CaptureDeliveryPlan();
                    if (plan.ContainerConfirmed && (plan.PackSuper || plan.PackPayload) && Composition.Model.Output.FirstOrDefault(i => i.Generated && i.FileName != "super_empty.img") is { } artifact)
                    {
                        artifact.FileName = DeliveryOutputService.ContainerFileName(plan);
                        artifact.PartitionName = plan.PackPayload ? "payload" : "super";
                        plan = CaptureDeliveryPlan();
                    }
                    if (plan.OrganizeDirectory && (!(plan.PackSuper || plan.PackPayload) || plan.ContainerConfirmed) && plan.Files.Count > 0)
                        if (!Composition.OutputView.SynchronizeScripts(plan))
                            Composition.Model.Status = LanguageService.Instance.GetString("Delivery_EditedScriptsNeedReview");
                }
            }
            catch (Exception ex) { _ = ShowDeliveryMessageAsync(UserError.Describe(ex), true); }
            finally
            {
                _deliverySaveTimer.Stop();
                if (_attached && _deliveryLoaded) _deliverySaveTimer.Start();
            }
        });
    }

    private async Task PersistDeliveryAsync()
    {
        await _deliverySaveGate.WaitAsync();
        try { await ProjectOperationsService.Instance.PersistSolutionStateAsync(); }
        finally { _deliverySaveGate.Release(); }
    }

    private async Task SaveDeliveryAsync(bool report)
    {
        if (_restoringDelivery || !_deliveryLoaded) return;
        try
        {
            if (_deliveryRoot is null)
            {
                if (report) await ShowDeliveryMessageAsync(LanguageService.Instance.GetString("Delivery_OpenProjectToSave"), false);
                return;
            }
            await PersistDeliveryAsync();
            if (report)
            {
                Composition.Model.Status = LanguageService.Instance.GetString("Delivery_PlanSaved");
                LogService.Instance.Write(LogLevel.Info, "Delivery", Composition.Model.Status);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Delivery", ex.Message);
            if (report) await ShowDeliveryMessageAsync(UserError.Describe(ex), true);
        }
    }

    private async Task RefreshDeliveryAsync()
    {
        await _deliveryRefreshGate.WaitAsync();
        _restoringDelivery = true;
        try
        {
            var root = ProjectContextService.Instance.RootDirectory;

            var generation = ProjectOperationsService.Instance.SolutionReloadGeneration;
            var reloaded = generation != _loadedSolutionGeneration;
            var rootChanged = root != _deliveryRoot;
            if (reloaded || !_deliveryLoaded || rootChanged)
            {
                _deliverySaveTimer.Stop();
                _deliveryCancellation?.Cancel();
                if (_deliveryCompletion is { } completion) await completion.Task;

                if (rootChanged && _deliveryLoaded && _deliveryRoot is not null) await PersistDeliveryAsync();
                if (root != ProjectContextService.Instance.RootDirectory) return;
                _restoringDelivery = true;
                _deliveryRoot = root;
                UpdateProjectDefaults();
                if (root is not null && DeliveryPlanStore.LoadFromState(ProjectOperationsService.Instance.PendingDeliveryState, root) is { } plan)
                    RestoreDelivery(plan);
                _deliveryLoaded = true;
                _loadedSolutionGeneration = generation;
            }
            await ImageSelection.RefreshAsync();
            ImageSelection.RestoreAssignedSources(Composition.Model.Container.Concat(Composition.Model.Output)
                .Where(i => i.FilePath is not null).Select(i => i.FilePath!), GetReadOnlyDeliverySources());
        }
        catch (Exception ex) { await ShowDeliveryMessageAsync(UserError.Describe(ex), true); }
        finally { _restoringDelivery = false; _deliveryRefreshGate.Release(); }
        QueueDeliveryChange();
    }

    internal void RestoreDelivery(DeliveryPlan plan)
    {
        var previous = _restoringDelivery; _restoringDelivery = true;
        try
        {
            UseProjectSuperSettingsBox.IsChecked = false;
            DirectOutputBox.IsChecked = plan.OrganizeDirectory; PackSuperBox.IsChecked = plan.PackSuper; PackPayloadBox.IsChecked = plan.PackPayload;
            PayloadVersionBox.Text = "2";
            PayloadMinorVersionBox.Text = "0";
            PayloadBuildFingerprintBox.Text = plan.Package.BuildFingerprint;
            PayloadIncrementalBox.Text = plan.Package.Incremental;
            PayloadSdkLevelBox.Text = plan.Package.SdkLevel == 0 ? "" : plan.Package.SdkLevel.ToString(CultureInfo.InvariantCulture);
            DynamicPartitionsFileBox.Text = plan.Payload.DynamicPartitionsFile;
            _legacyPayloadSignatureFile = plan.Payload.SignatureFile;
            _payloadSigningIdentityPath = plan.Payload.SigningKeyPath;
            _payloadSigningFingerprint = plan.Payload.SigningKeyFingerprint;
            RefreshPayloadSigningKeys();
            _zipSigningIdentityPath = plan.Package.SigningKeyPath;
            _recoveryUpdaterPath = plan.Package.RecoveryUpdaterPath;
            _zipSigningCertificatePath = plan.Package.CertificatePath;
            RefreshZipSigningKeys();
            PayloadPostinstallBox.Text = plan.Payload.PostinstallFile;
            PayloadTimestampBox.Text = plan.Payload.MaxTimestamp.ToString(CultureInfo.InvariantCulture);
            PayloadSecurityPatchBox.Text = plan.Payload.SecurityPatchLevel;
            SuperZstdBox.IsChecked = plan.Super.Zstd && !plan.Super.Empty;
            SuperZstdChunkedBox.IsChecked = plan.Super.ZstdChunkedParallel;
            _deliveryGroups = plan.Super.Groups;
            OutputDirectoryBox.Text = plan.OutputDirectory;
            SuperDeviceNameBox.Text = plan.PackPayload ? "payload.bin" : plan.Super.DeviceName; SuperGroupNameBox.Text = plan.Super.Group;
            SuperSizeBox.Text = plan.Super.DeviceSize == 0 ? "" : plan.Super.DeviceSize.ToString(CultureInfo.InvariantCulture);
            SuperMetadataSizeBox.Text = plan.Super.MetadataSize.ToString(CultureInfo.InvariantCulture);
            SuperBlockSizeBox.Text = (plan.PackPayload ? plan.Payload.BlockSize : plan.Super.BlockSize).ToString(CultureInfo.InvariantCulture);
            var deviceType = plan.Super.DeviceType.Length > 0 ? plan.Super.DeviceType : plan.Super.VirtualAb ? "VAB" : plan.Super.AutoZero ? "AB" : "AOnly";
            SuperVabBox.IsChecked = deviceType == "VAB"; SuperAbBox.IsChecked = deviceType == "AB";
            SuperAOnlyBox.IsChecked = deviceType == "AOnly";
            SuperMetadataSlotsBox.Text = plan.Super.Slots.ToString(CultureInfo.InvariantCulture);
            SuperRawBox.IsChecked = !plan.Super.Sparse; SuperSparseBox.IsChecked = plan.Super.Sparse;
            GenerateSuperEmptyBox.IsChecked = plan.Super.Empty;
            Composition.SuperEmptyOnly = plan.Super.Empty;
            Composition.AutoZero = plan.Super.AutoZero;
            Composition.Model.Reset(plan); Composition.OutputView.Restore(plan);

            UpdateDeviceTypeOptions();
            UpdateSidebarLock();
        }
        finally { _restoringDelivery = previous; }
    }

    internal async Task RunDeliveryAsync(bool showCompletion = true)
    {
        if (_deliveryCancellation is not null) { _deliveryCancellation.Cancel(); return; }
        using var cancellation = new CancellationTokenSource();
        _deliveryCancellation = cancellation; _deliveryCompletion = new();
        Composition.Model.ResetProgressStatistics();
        Composition.Model.Status = string.Empty;
        var acceptingProgress = true;
        var root = _deliveryRoot;
        try
        {
            if (_restoringDelivery || !_deliveryLoaded || root != ProjectContextService.Instance.RootDirectory)
                throw new InvalidOperationException(LanguageService.Instance.GetString("Delivery_ProjectTransition"));
            var plan = CaptureDeliveryPlan();
            DeliveryValidation.Validate(plan);
            if (plan.OrganizeDirectory && !plan.IsSuperEmptyOnly && !Composition.OutputView.SynchronizeScripts(plan))
            {
                if (TopLevel.GetTopLevel(this) is not Window owner || !await DialogService.ConfirmAsync(owner, LanguageService.Instance.GetString("Tab_Delivery"),
                        LanguageService.Instance.GetString("Delivery_ReplaceEditedScripts"))) return;

                cancellation.Token.ThrowIfCancellationRequested();
                if (root != _deliveryRoot) return;
                Composition.OutputView.SynchronizeScripts(CaptureDeliveryPlan(), true);
            }
            plan = CaptureDeliveryPlan();

            await CheckDeliveryDiskSpaceAsync(plan);

            var planJson = DeliveryPlanSerializer.ToJson(plan);
            IReadOnlySet<string>? overwrite = null;
            if (!string.IsNullOrWhiteSpace(plan.OutputDirectory))
            {
                var existing = DeliveryService.ListOutputs(planJson).Existing.ToArray();
                if (existing.Length > 0 && TopLevel.GetTopLevel(this) is Window owner)
                {
                    var language = LanguageService.Instance;
                    const int maxListed = 10;
                    var listed = existing.Take(maxListed).Select(path => Path.GetRelativePath(plan.OutputDirectory, path)).ToArray();
                    var message = language.GetString("Delivery_OverwritePrompt") + "\n" + string.Join("\n", listed);
                    if (existing.Length > maxListed)
                        message += "\n" + string.Format(language.GetString("Delivery_OverwriteMore"), existing.Length - maxListed);
                    var overwriteAll = await DialogService.ConfirmYesNoAsync(owner, language.GetString("Delivery_OverwriteTitle"), message,
                        language.GetString("Explorer_Yes"), language.GetString("Explorer_No"));

                    cancellation.Token.ThrowIfCancellationRequested();
                    if (root != _deliveryRoot) return;
                    var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                    overwrite = overwriteAll ? existing.ToHashSet(comparison) : new HashSet<string>(comparison);
                }
            }
            if (root is not null) await PersistDeliveryAsync();
            Composition.SetRunning(true, 0); SidebarForm.IsEnabled = false; ImageSelection.IsEnabled = false;
            var progress = new Progress<DeliveryBuildProgress>(value =>
            {
                if (!acceptingProgress || !ReferenceEquals(_deliveryCancellation, cancellation) || root != _deliveryRoot) return;
                Composition.Model.ReportProgress(value.ToDeliveryProgress());
                Composition.SetRunning(true, Composition.Model.Progress);
            });
            var outputs = (await new DeliveryService(DeliveryHttp, signingKeys: new DeliveryKeyProvider())
                .BuildAsync(planJson, overwrite, progress, cancellation.Token)).ToArray();
            acceptingProgress = false;
            Composition.Model.ResetProgressStatistics();
            Composition.SetRunning(true, 100);
            var completionMessage = LanguageService.Instance.GetString("Delivery_OutputComplete", outputs.Length, plan.OutputDirectory);
            Composition.Model.Status = string.Empty;
            LogService.Instance.Write(LogLevel.Info, "Delivery", completionMessage);

            ImageSelection.AddDeliveryOutputs(outputs);
            if (showCompletion && _attached && root == ProjectContextService.Instance.RootDirectory)
                await ShowDeliveryMessageAsync(completionMessage, false);
        }
        catch (OperationCanceledException)
        {
            acceptingProgress = false;
            var cancellationMessage = LanguageService.Instance.GetString("Delivery_OutputCancelled");
            Composition.Model.Status = cancellationMessage;
            LogService.Instance.Write(LogLevel.Info, "Delivery", cancellationMessage);
        }
        catch (Exception ex)
        {
            acceptingProgress = false;
            await ShowDeliveryMessageAsync(UserError.Describe(ex), true);
        }
        finally
        {
            _deliveryCancellation = null;
            Composition.Model.ResetProgressStatistics();
            Composition.SetRunning(false, Composition.Model.Progress);
            UpdateSidebarLock();
            _deliveryCompletion!.TrySetResult(); _deliveryCompletion = null;
        }
    }

    /// <summary>
    /// Estimates disk demand before the export and compares it with free space; on shortage the user is
    /// asked to confirm, and declining aborts the export.
    /// <para>输出前预估磁盘需求并对比剩余空间；不足时弹确认框，用户取消则中断输出。</para>
    /// </summary>
    /// <param name="plan">
    /// The delivery plan whose output directory and demand are checked.
    /// <para>要检查输出目录与磁盘需求的交付计划。</para>
    /// </param>
    private async Task CheckDeliveryDiskSpaceAsync(DeliveryPlan plan)
    {
        try
        {
            var outputDirectory = plan.OutputDirectory;
            if (string.IsNullOrWhiteSpace(outputDirectory)) return;

            var required = DiskSpaceEstimator.EstimateExportDemand(plan);
            if (required <= 0) return;
            var free = DiskSpaceEstimator.GetFreeBytes(outputDirectory);
            if (free < 0 || free >= required) return;
            var language = LanguageService.Instance;
            var message = string.Format(language.GetString("Delivery_DiskSpaceWarning"),
                ProgressMeter.FormatSize(free), ProgressMeter.FormatSize(required));
            if (TopLevel.GetTopLevel(this) is not Window owner || !await DialogService.ConfirmAsync(owner,
                    language.GetString("Delivery_DiskSpaceTitle"), message,
                    language.GetString("Delivery_DiskSpaceContinue"), language.GetString("Delivery_DiskSpaceCancel")))
                throw new OperationCanceledException(language.GetString("Delivery_OutputCancelled"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "Delivery", $"Disk space estimation failed: {ex.Message}");
        }
    }

    private async Task ShowDeliveryMessageAsync(string message, bool error)
    {
        Composition.Model.Status = string.Empty;
        LogService.Instance.Write(error ? LogLevel.Error : LogLevel.Info, "Delivery", message);
        if (_attached && TopLevel.GetTopLevel(this) is Window owner)
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Tab_Delivery"), message, error ? Ursa.Controls.MessageBoxIcon.Error : Ursa.Controls.MessageBoxIcon.Information);
    }
}