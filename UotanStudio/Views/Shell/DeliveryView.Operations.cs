using System.Globalization;
using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using UotanStudio.Services;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Projects;

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
    private bool _restoringDelivery;
    private bool _deliveryChangePending;

    private void InitializeDelivery()
    {
        Composition.ReadOptions = ReadDeliverySuperOptions;
        Composition.Changed += (_, _) => QueueDeliveryChange();
        Composition.OutputView.Changed += (_, _) => QueueDeliveryChange();
        Composition.OutputRequested += async (_, _) => await RunDeliveryAsync();
        Composition.OperationFailed += async (_, error) => await ShowDeliveryMessageAsync(error.Message, true);
        Composition.Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Composition.Model.Status)) ToolTip.SetTip(Composition, Composition.Model.Status);
        };
        foreach (var box in new[] { OutputDirectoryBox, SuperSizeBox, SuperGroupNameBox, SuperDeviceNameBox,
                     SuperMetadataSizeBox, SuperBlockSizeBox, SuperMetadataSlotsBox, PayloadVersionBox, PayloadMinorVersionBox,
                     DynamicPartitionsFileBox, PayloadSignatureBox, PayloadPostinstallBox })
            box.TextChanged += (_, _) => QueueDeliveryChange();
        foreach (var box in new ToggleButton[] { DirectOutputBox, PackSuperBox, PackPayloadBox, SuperRawBox, SuperSparseBox,
                     SuperVabBox, SuperAbBox, SuperAOnlyBox, ForceFullSuperImageBox, GenerateSuperEmptyBox })
            box.IsCheckedChanged += (_, _) => QueueDeliveryChange();
        GenerateDynamicPartitionsButton.IsEnabled = true;
        GenerateDynamicPartitionsButton.Click += async (_, _) => await GenerateDynamicPartitionsAsync();
        _deliverySaveTimer.Tick += async (_, _) => { _deliverySaveTimer.Stop(); await SaveDeliveryAsync(false); };
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.S && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            { e.Handled = true; await SaveDeliveryAsync(true); }
        };
    }

    private DeliverySuperOptions ReadDeliverySuperOptions()
    {
        if (PackSuperBox.IsChecked == true && ForceFullSuperImageBox.IsChecked != true && GenerateSuperEmptyBox.IsChecked != true)
            throw new InvalidDataException("请选择完整 Super 或 Super_Empty / Select a full Super or Super_Empty image.");
        static uint Number(string? text, string name) => uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : throw new InvalidDataException("参数无效 / Invalid " + name);
        if (!ulong.TryParse(SuperSizeBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            throw new InvalidDataException("Super 大小无效 / Invalid Super size.");
        return new()
        {
            DeviceSize = size, Group = SuperGroupNameBox.Text?.Trim() ?? "default", DeviceName = SuperDeviceNameBox.Text?.Trim() ?? "super",
            MetadataSize = Number(SuperMetadataSizeBox.Text, "metadata size"), Slots = Number(SuperMetadataSlotsBox.Text, "metadata slots"),
            BlockSize = Number(SuperBlockSizeBox.Text, "block size"), Sparse = SuperSparseBox.IsChecked == true,
            VirtualAb = SuperVabBox.IsChecked == true, AutoZero = Composition.AutoZero && SuperAOnlyBox.IsChecked != true,
            DeviceType = SuperVabBox.IsChecked == true ? "VAB" : SuperAbBox.IsChecked == true ? "AB" : "AOnly",
            Empty = GenerateSuperEmptyBox.IsChecked == true
        };
    }

    private DeliveryPayloadOptions ReadDeliveryPayloadOptions()
    {
        if (!uint.TryParse(PayloadVersionBox.Text, out var version) || !uint.TryParse(PayloadMinorVersionBox.Text, out var minor))
            throw new InvalidDataException("Payload 版本无效 / Invalid Payload version.");
        return new() { Version = version, MinorVersion = minor, DynamicPartitionsFile = DynamicPartitionsFileBox.Text?.Trim() ?? "",
            SignatureFile = PayloadSignatureBox.Text?.Trim() ?? "", PostinstallFile = PayloadPostinstallBox.Text?.Trim() ?? "" };
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
            { Title = "动态分区配置 / Dynamic partition configuration", SuggestedFileName = "dynamic_partitions_info.txt", DefaultExtension = "txt" });
            if (root != _deliveryRoot || _deliveryCancellation is not null || PackPayloadBox.IsChecked != true || file?.TryGetLocalPath() is not { } path) return;
            var temporary = path + "." + Guid.NewGuid().ToString("N");
            try { await File.WriteAllTextAsync(temporary, text); File.Move(temporary, path, true); }
            finally { File.Delete(temporary); }
            if (root == _deliveryRoot && PackPayloadBox.IsChecked == true) DynamicPartitionsFileBox.Text = path;
        }
        catch (Exception ex) { await ShowDeliveryMessageAsync(ex.Message, true); }
        finally { _generatingDynamicPartitions = false; }
    }

    internal DeliveryPlan CaptureDeliveryPlan() => Composition.OutputView.Capture(new()
    {
        OutputDirectory = OutputDirectoryBox.Text?.Trim() ?? "", OrganizeDirectory = DirectOutputBox.IsChecked == true,
        PackPayload = PackPayloadBox.IsChecked == true, Payload = ReadDeliveryPayloadOptions(),
        PackSuper = PackSuperBox.IsChecked == true, ContainerConfirmed = Composition.Model.Confirmed,
        Super = PackSuperBox.IsChecked == true || PackPayloadBox.IsChecked == true ? ReadDeliverySuperOptions() : new(),
        Container = Composition.Model.Container.Select(i => i.Snapshot()).ToList()
    });

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
            ImageSelection.SetAssignedPaths(Composition.Model.Container.Concat(Composition.Model.Output)
                .Where(i => i.FilePath is not null).Select(i => i.FilePath!), GetReadOnlyDeliverySources());
            try
            {
                if (!Composition.OutputView.RecoveryRequested)
                {
                    var plan = CaptureDeliveryPlan();
                    if (plan.ContainerConfirmed && (plan.PackSuper || plan.PackPayload) && Composition.Model.Output.FirstOrDefault(i => i.Generated) is { } artifact)
                    {
                        artifact.FileName = DeliveryOutputService.ContainerFileName(plan);
                        artifact.PartitionName = plan.PackPayload ? "payload" : plan.Super.Empty ? "super_empty" : "super";
                        plan = CaptureDeliveryPlan();
                    }
                    if (plan.OrganizeDirectory && (!(plan.PackSuper || plan.PackPayload) || plan.ContainerConfirmed) && plan.Files.Count > 0)
                        if (!Composition.OutputView.SynchronizeScripts(plan))
                            Composition.Model.Status = "配置已变化，输出前需处理已修改脚本 / Edited scripts need review before output.";
                }
            }
            catch (Exception ex) { Composition.Model.Status = ex.Message; }
            finally
            {
                _deliverySaveTimer.Stop();
                if (_attached && _deliveryLoaded) _deliverySaveTimer.Start();
            }
        });
    }

    private async Task PersistDeliveryAsync(string root, DeliveryPlan plan)
    {
        await _deliverySaveGate.WaitAsync();
        try { await DeliveryPlanStore.SaveAsync(root, plan); }
        finally { _deliverySaveGate.Release(); }
    }

    private async Task SaveDeliveryAsync(bool report)
    {
        if (_restoringDelivery || !_deliveryLoaded) return;
        try
        {
            var root = _deliveryRoot;
            if (root is null)
            {
                if (report) await ShowDeliveryMessageAsync("请先打开工程再保存交付计划 / Open a project to save the delivery plan.", false);
                return;
            }
            await PersistDeliveryAsync(root, CaptureDeliveryPlan());
            if (report) LogService.Instance.Write(LogLevel.Info, "Delivery", "交付计划和文本已保存 / Delivery plan and drafts saved.");
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Delivery", ex.Message);
            if (report) await ShowDeliveryMessageAsync(ex.Message, true);
        }
    }

    private async Task RefreshDeliveryAsync()
    {
        await _deliveryRefreshGate.WaitAsync();
        _restoringDelivery = true;
        try
        {
            var root = ProjectContextService.Instance.RootDirectory;
            if (!_deliveryLoaded || root != _deliveryRoot)
            {
                _deliverySaveTimer.Stop();
                _deliveryCancellation?.Cancel();
                if (_deliveryCompletion is { } completion) await completion.Task;
                if (_deliveryLoaded && _deliveryRoot is { } previous)
                    await PersistDeliveryAsync(previous, CaptureDeliveryPlan());
                if (root != ProjectContextService.Instance.RootDirectory) return;
                _restoringDelivery = true;
                _deliveryRoot = root;
                UpdateProjectDefaults();
                if (root is not null && await DeliveryPlanStore.LoadAsync(root) is { } plan)
                    RestoreDelivery(plan);
                _deliveryLoaded = true;
            }
            await ImageSelection.RefreshAsync();
            ImageSelection.RestoreAssignedSources(Composition.Model.Container.Concat(Composition.Model.Output)
                .Where(i => i.FilePath is not null).Select(i => i.FilePath!), GetReadOnlyDeliverySources());
        }
        catch (Exception ex) { await ShowDeliveryMessageAsync(ex.Message, true); }
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
            PayloadVersionBox.Text = plan.Payload.Version.ToString(CultureInfo.InvariantCulture);
            PayloadMinorVersionBox.Text = plan.Payload.MinorVersion.ToString(CultureInfo.InvariantCulture);
            DynamicPartitionsFileBox.Text = plan.Payload.DynamicPartitionsFile;
            PayloadSignatureBox.Text = plan.Payload.SignatureFile;
            PayloadPostinstallBox.Text = plan.Payload.PostinstallFile;
            OutputDirectoryBox.Text = plan.OutputDirectory;
            SuperDeviceNameBox.Text = plan.Super.DeviceName; SuperGroupNameBox.Text = plan.Super.Group;
            SuperSizeBox.Text = plan.Super.DeviceSize.ToString(CultureInfo.InvariantCulture);
            SuperMetadataSizeBox.Text = plan.Super.MetadataSize.ToString(CultureInfo.InvariantCulture);
            SuperBlockSizeBox.Text = plan.Super.BlockSize.ToString(CultureInfo.InvariantCulture);
            var deviceType = plan.Super.DeviceType.Length > 0 ? plan.Super.DeviceType : plan.Super.VirtualAb ? "VAB" : plan.Super.AutoZero ? "AB" : "AOnly";
            SuperVabBox.IsChecked = deviceType == "VAB"; SuperAbBox.IsChecked = deviceType == "AB";
            SuperAOnlyBox.IsChecked = deviceType == "AOnly";
            SuperMetadataSlotsBox.Text = plan.Super.Slots.ToString(CultureInfo.InvariantCulture);
            SuperRawBox.IsChecked = !plan.Super.Sparse; SuperSparseBox.IsChecked = plan.Super.Sparse;
            GenerateSuperEmptyBox.IsChecked = plan.Super.Empty; ForceFullSuperImageBox.IsChecked = !plan.Super.Empty;
            Composition.AutoZero = plan.Super.AutoZero;
            Composition.Model.Reset(plan); Composition.OutputView.Restore(plan);
        }
        finally { _restoringDelivery = previous; }
    }

    internal async Task RunDeliveryAsync(bool showCompletion = true)
    {
        if (_deliveryCancellation is not null) { _deliveryCancellation.Cancel(); return; }
        using var cancellation = new CancellationTokenSource();
        _deliveryCancellation = cancellation; _deliveryCompletion = new();
        var root = _deliveryRoot;
        try
        {
            if (_restoringDelivery || !_deliveryLoaded || root != ProjectContextService.Instance.RootDirectory)
                throw new InvalidOperationException("正在切换工程，请稍后重试 / Project transition in progress.");
            var plan = CaptureDeliveryPlan();
            DeliveryValidation.Validate(plan);
            if (plan.OrganizeDirectory && !Composition.OutputView.SynchronizeScripts(plan))
            {
                if (TopLevel.GetTopLevel(this) is not Window owner || !await DialogService.ConfirmAsync(owner, "Delivery",
                        "配置已变化，现有脚本含手动修改。是否用新配置重新生成？取消将保留草稿并停止输出。\nConfiguration changed. Replace edited scripts? Cancel preserves drafts and stops output.")) return;
                // A project transition may have cancelled the operation while the dialog was open.
                cancellation.Token.ThrowIfCancellationRequested();
                if (root != _deliveryRoot) return;
                Composition.OutputView.SynchronizeScripts(CaptureDeliveryPlan(), true);
            }
            plan = CaptureDeliveryPlan();
            if (root is not null) await PersistDeliveryAsync(root, plan);
            Composition.SetRunning(true, 0); SidebarForm.IsEnabled = false; ImageSelection.IsEnabled = false;
            var progress = new Progress<DeliveryProgress>(value =>
            {
                if (!ReferenceEquals(_deliveryCancellation, cancellation) || root != _deliveryRoot) return;
                Composition.Model.Status = value.Message; Composition.SetRunning(true, value.Percent);
            });
            var service = new DeliveryOutputService(new DeliveryToolService(DeliveryHttp,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UotanStudio", "delivery-tools")));
            var outputs = await service.ExportAsync(plan, progress, cancellation.Token);
            Composition.Model.Status = $"已输出 {outputs.Length} 个文件 / Output complete: {plan.OutputDirectory}";
            LogService.Instance.Write(LogLevel.Info, "Delivery", Composition.Model.Status);
            if (showCompletion && _attached && root == ProjectContextService.Instance.RootDirectory)
                await ShowDeliveryMessageAsync(Composition.Model.Status, false);
        }
        catch (OperationCanceledException)
        {
            Composition.Model.Status = "输出已取消 / Output cancelled.";
            LogService.Instance.Write(LogLevel.Info, "Delivery", Composition.Model.Status);
        }
        catch (Exception ex) { await ShowDeliveryMessageAsync(ex.Message, true); }
        finally
        {
            _deliveryCancellation = null;
            Composition.SetRunning(false, Composition.Model.Progress);
            SidebarForm.IsEnabled = true; ImageSelection.IsEnabled = true;
            _deliveryCompletion!.TrySetResult(); _deliveryCompletion = null;
        }
    }

    private async Task ShowDeliveryMessageAsync(string message, bool error)
    {
        Composition.Model.Status = message;
        LogService.Instance.Write(error ? LogLevel.Error : LogLevel.Info, "Delivery", message);
        if (_attached && TopLevel.GetTopLevel(this) is Window owner)
            await DialogService.ShowAsync(owner, "Delivery", message, error ? Ursa.Controls.MessageBoxIcon.Error : Ursa.Controls.MessageBoxIcon.Information);
    }
}
