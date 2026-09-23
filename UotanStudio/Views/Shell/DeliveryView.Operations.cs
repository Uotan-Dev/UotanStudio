using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UotanStudio.Services;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Shell;

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
    // 已应用到面板的解决方案完整重载代号；与 ProjectOperationsService 的计数器比较决定是否强制重载。
    // The full-reload generation already applied to the panel; compared against the service counter to
    // decide whether the plan and sub-project choices must be rebuilt from the solution file.
    private int _loadedSolutionGeneration;
    private bool _restoringDelivery;
    private bool _deliveryChangePending;
    private string _recoveryUpdaterPath = "";

    private void InitializeDelivery()
    {
        // 交付计划随解决方案文件持久化：保存解决方案前由钩子捕获当前表单状态。
        // The delivery plan persists inside the solution file: a hook captures the form state before solution saves.
        ProjectOperationsService.Instance.CaptureDeliveryState = CaptureDeliveryState;
        InitializePayloadSigningKeys();
        ManageZipKeysButton.Click += (_, _) => MenuViewModel.ShowKeyManagerWindow();
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
        Composition.OperationFailed += async (_, error) => await ShowDeliveryMessageAsync(global::UotanStudio.Services.UserError.Describe(error), true);
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
                     SuperZstdBox, SuperVabBox, SuperAbBox, SuperAOnlyBox, GenerateSuperEmptyBox })
            box.IsCheckedChanged += (_, _) => QueueDeliveryChange();
        // 压缩为ZST 与 仅生成Super_Empty 互斥：勾选其一自动取消另一项。
        // ZST compression and Super_Empty-only are mutually exclusive; checking one clears the other.
        SuperZstdBox.IsCheckedChanged += (_, _) =>
        {
            if (SuperZstdBox.IsChecked == true && GenerateSuperEmptyBox.IsChecked == true) GenerateSuperEmptyBox.IsChecked = false;
            UpdateSuperFormatOptions();
        };
        GenerateSuperEmptyBox.IsCheckedChanged += (_, _) =>
        {
            // 仅生成Super_Empty：右栏输出设置（镜像列表与脚本生成器）禁用，开始输出按钮保持可用。
            // Super_Empty only: right-pane output settings (image list and script generator) disabled; start output stays available.
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
        catch (Exception ex) { await ShowDeliveryMessageAsync(global::UotanStudio.Services.UserError.Describe(ex), true); }
        finally { _generatingScripts = false; }
    }

    private DeliverySuperOptions ReadDeliverySuperOptions()
    {
        static uint Number(string? text, string name) => uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : throw new InvalidDataException(LanguageService.Instance.GetString("Delivery_InvalidParameter", LanguageService.Instance.GetString(name)));
        if (!ulong.TryParse(SuperSizeBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            throw new InvalidDataException(LanguageService.Instance.GetString("Delivery_InvalidSuperSize"));
        return new()
        {
            Groups = _deliveryGroups,
            DeviceSize = size,
            Group = SuperGroupNameBox.Text?.Trim() ?? "default",
            DeviceName = SuperDeviceNameBox.Text?.Trim() ?? "super",
            MetadataSize = Number(SuperMetadataSizeBox.Text, "Delivery_MetadataSize"),
            Slots = Number(SuperMetadataSlotsBox.Text, "Delivery_MetadataSlots"),
            BlockSize = Number(SuperBlockSizeBox.Text, "Delivery_BlockSize"),
            Sparse = SuperSparseBox.IsChecked == true,
            VirtualAb = SuperVabBox.IsChecked == true,
            AutoZero = Composition.AutoZero && SuperAOnlyBox.IsChecked != true,
            DeviceType = SuperVabBox.IsChecked == true ? "VAB" : SuperAbBox.IsChecked == true ? "AB" : "AOnly",
            Zstd = SuperZstdBox.IsChecked == true,
            Empty = GenerateSuperEmptyBox.IsChecked == true
        };
    }

    private DeliveryPayloadOptions ReadDeliveryPayloadOptions()
    {
        if (!uint.TryParse(PayloadVersionBox.Text, out var version) || !uint.TryParse(PayloadMinorVersionBox.Text, out var minor))
            throw new InvalidDataException(LanguageService.Instance.GetString("Delivery_InvalidPayloadVersion"));
        return new()
        {
            BlockSize = uint.TryParse(SuperBlockSizeBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var block) ? block : 0,
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
        catch (Exception ex) { await ShowDeliveryMessageAsync(global::UotanStudio.Services.UserError.Describe(ex), true); }
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
        Container = Composition.Model.Container.Select(i => i.Snapshot()).ToList()
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

    /// <summary><para>解决方案保存前的捕获钩子：表单未加载或正在恢复时返回 null（保留原状态）。</para>
    /// Capture hook invoked before solution saves; returns null (keeping the stored state) when the
    /// form is not loaded or is being restored.</summary>
    private JsonElement? CaptureDeliveryState()
    {
        var root = _deliveryRoot;
        if (root is null || !_deliveryLoaded || _restoringDelivery) return null;
        try { return DeliveryPlanStore.CaptureElement(root, CaptureDeliveryPlan()); }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "Delivery", $"Delivery state capture skipped: {ex.Message}");
            return null;
        }
    }

    // The inactive list still owns its sources; expose that ownership to the picker without another selection state.
    // 非编辑列表继续持有其镜像，只向选择器同步只读状态，不额外维护镜像归属。
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
            catch (Exception ex) { Composition.Model.Status = global::UotanStudio.Services.UserError.Describe(ex); }
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
            if (report) await ShowDeliveryMessageAsync(global::UotanStudio.Services.UserError.Describe(ex), true);
        }
    }

    private async Task RefreshDeliveryAsync()
    {
        await _deliveryRefreshGate.WaitAsync();
        _restoringDelivery = true;
        try
        {
            var root = ProjectContextService.Instance.RootDirectory;
            // 完整重载（打开、添加/删除子项目）由会话计数器标记：即使解决方案路径未变，也要丢弃内存计划，
            // 从解决方案文件重新恢复并重建子项目下拉列表；面板当时未在可视树、错过消息时也能在再次进入时补上。
            // A full reload (open, add/remove sub-project) is marked by the session counter: even with an
            // unchanged path the in-memory plan is dropped, restored from the solution file and the sub-project
            // choices rebuilt; a detached panel that missed the message catches up on the next refresh.
            var generation = ProjectOperationsService.Instance.SolutionReloadGeneration;
            var reloaded = generation != _loadedSolutionGeneration;
            var rootChanged = root != _deliveryRoot;
            if (reloaded || !_deliveryLoaded || rootChanged)
            {
                _deliverySaveTimer.Stop();
                _deliveryCancellation?.Cancel();
                if (_deliveryCompletion is { } completion) await completion.Task;
                // 仅“换到另一解决方案”才回写旧计划；同文件完整重载必须从文件读取，
                // 否则刚被重新加载的交付状态会被内存中的旧表单覆盖。
                // Only a move to a different solution writes the outgoing plan back; a full reload of the
                // same file must read the stored state, or the reloaded plan would be overwritten by the
                // stale in-memory form.
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
        catch (Exception ex) { await ShowDeliveryMessageAsync(global::UotanStudio.Services.UserError.Describe(ex), true); }
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
            _deliveryGroups = plan.Super.Groups;
            OutputDirectoryBox.Text = plan.OutputDirectory;
            SuperDeviceNameBox.Text = plan.PackPayload ? "payload.bin" : plan.Super.DeviceName; SuperGroupNameBox.Text = plan.Super.Group;
            SuperSizeBox.Text = plan.Super.DeviceSize.ToString(CultureInfo.InvariantCulture);
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
            // 设备类型联动（含 A-Only 清理自动 B 槽行）必须在 AutoZero/Reset 之后应用，确保还原后状态一致。
            // Device-type linkage (incl. the A-Only cleanup of auto B-slot rows) must run after AutoZero/Reset so the restored state is consistent.
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
                // A project transition may have cancelled the operation while the dialog was open.
                cancellation.Token.ThrowIfCancellationRequested();
                if (root != _deliveryRoot) return;
                Composition.OutputView.SynchronizeScripts(CaptureDeliveryPlan(), true);
            }
            plan = CaptureDeliveryPlan();
            // 输出前的磁盘剩余空间预估；估算本身出错不阻塞输出。
            // Disk space pre-check before exporting; an estimation failure never blocks the export.
            await CheckDeliveryDiskSpaceAsync(plan);
            // 提前分析已存在的输出文件，弹窗提示冲突并列出文件名（过多时只报数量）；
            // 是=覆盖全部冲突文件，否=保留原文件。
            // Scan existing output files in advance and ask via a yes/no dialog whether to overwrite them
            // all; the message lists the conflicting names, or just the count when there are too many.
            IReadOnlySet<string>? overwrite = null;
            if (!string.IsNullOrWhiteSpace(plan.OutputDirectory))
            {
                var existing = DeliveryOutputService.FindExistingOutputs(plan);
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
                    // A project transition may have cancelled the operation while the dialog was open.
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (root != _deliveryRoot) return;
                    var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                    overwrite = overwriteAll ? existing.ToHashSet(comparison) : new HashSet<string>(comparison);
                }
            }
            if (root is not null) await PersistDeliveryAsync();
            Composition.SetRunning(true, 0); SidebarForm.IsEnabled = false; ImageSelection.IsEnabled = false;
            var progress = new Progress<DeliveryProgress>(value =>
            {
                if (!acceptingProgress || !ReferenceEquals(_deliveryCancellation, cancellation) || root != _deliveryRoot) return;
                Composition.Model.ReportProgress(value);
                Composition.SetRunning(true, Composition.Model.Progress);
            });
            var service = new DeliveryOutputService(new DeliveryToolService(DeliveryHttp,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UotanStudio", "delivery-tools")),
                new DeliveryKeyProvider());
            var outputs = await service.ExportAsync(plan, progress, cancellation.Token, overwrite);
            acceptingProgress = false;
            Composition.Model.ResetProgressStatistics();
            Composition.SetRunning(true, 100);
            Composition.Model.Status = LanguageService.Instance.GetString("Delivery_OutputComplete", outputs.Length, plan.OutputDirectory);
            LogService.Instance.Write(LogLevel.Info, "Delivery", Composition.Model.Status);
            // 生成的镜像同步映射到镜像文件管理面板，重名条目由面板标注来源。
            // Map generated images into the image manager panel; the view annotates colliding file names with the source.
            ImageSelection.AddDeliveryOutputs(outputs);
            if (showCompletion && _attached && root == ProjectContextService.Instance.RootDirectory)
                await ShowDeliveryMessageAsync(Composition.Model.Status, false);
        }
        catch (OperationCanceledException)
        {
            acceptingProgress = false;
            Composition.Model.Status = LanguageService.Instance.GetString("Delivery_OutputCancelled");
            LogService.Instance.Write(LogLevel.Info, "Delivery", Composition.Model.Status);
        }
        catch (Exception ex)
        {
            acceptingProgress = false;
            await ShowDeliveryMessageAsync(global::UotanStudio.Services.UserError.Describe(ex), true);
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

    /// <summary><para>输出前预估磁盘需求并对比剩余空间；不足时弹确认框，用户取消则中断输出。</para>
    /// Estimates disk demand before the export and compares it with free space; on shortage the user is
    /// asked to confirm, and declining aborts the export.</summary>
    private async Task CheckDeliveryDiskSpaceAsync(DeliveryPlan plan)
    {
        try
        {
            var outputDirectory = plan.OutputDirectory;
            if (string.IsNullOrWhiteSpace(outputDirectory)) return;
            // 需求按交付计划估算：源镜像 + 生成的 Super/Payload + 工具，乘安全系数。
            // Demand is estimated from the plan: source images + generated Super/Payload + tools, times the safety factor.
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
        Composition.Model.Status = message;
        LogService.Instance.Write(error ? LogLevel.Error : LogLevel.Info, "Delivery", message);
        if (_attached && TopLevel.GetTopLevel(this) is Window owner)
            await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Tab_Delivery"), message, error ? Ursa.Controls.MessageBoxIcon.Error : Ursa.Controls.MessageBoxIcon.Information);
    }
}