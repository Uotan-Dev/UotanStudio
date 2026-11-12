using Avalonia.Controls;
using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Ui;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// SpacemiT package actions in the two halves the vendor flow actually has: sending the boot chain, which stages FSBL
/// and U-Boot into RAM and lifts the board out of the BootROM download mode, and — once the U-Boot gadget runs — writing
/// the package's partition tables and images. The page owns no SpacemiT section; both actions hang off the shared
/// buttons.
/// <para>进迭时空烧录包动作按厂商流程真实存在的两段划分：发送引导链，把 FSBL 与 U-Boot 暂存进内存并将板卡带离 BootROM
/// 下载模式；以及在该 U-Boot gadget 运行之后写入包内分区表与镜像。页面不再拥有进迭时空分类，两个动作都挂在通用按钮上。</para>
/// </summary>
public sealed partial class FlashViewModel
{
    /// <summary>The loaded SpacemiT package whose script drives both actions; <see langword="null"/> until a package
    /// folder validates. <para>两个动作所依据的已加载进迭时空烧录包；包目录校验通过前为 <see langword="null"/>。</para></summary>
    private SpacemiTFlashPackage? _spacemiTPackage;

    /// <summary>Whether a SpacemiT package is loaded, so the shared flash action knows it has a script to run.
    /// <para>是否已加载进迭时空烧录包，使通用烧录动作知道有脚本可执行。</para></summary>
    internal bool HasSpacemiTPackage => _spacemiTPackage is not null;

    /// <summary>
    /// Whether the running section has no measurable overall fraction: the boot-chain section only stages two files into
    /// RAM and hands over, so its transfers share no denominator and a percentage would restart from zero at each staged
    /// file. Both overall bars scroll for the whole run instead of jumping, and the caption keeps the section's own
    /// wording rather than the transport's diagnostic lines.
    /// <para>本次运行的段落是否没有可度量的整体比例：引导链段落只把两个文件暂存进内存并移交，各次传输没有共同的分母，
    /// 百分比会在每个暂存文件上从零重新开始。运行时两条总进度条改为持续滚动而不是来回跳动，说明文本保持该段落自己的措辞，
    /// 而不是传输层的诊断行。</para>
    /// </summary>
    private bool _overallScrolls;

    /// <summary>
    /// Sends the package's boot-chain section: FSBL and U-Boot are staged into RAM and handed over with <c>continue</c>,
    /// which turns the BootROM download mode into the U-Boot fastboot gadget. Nothing reaches storage, so the run needs no
    /// confirmation phrase; a board already running the U-Boot gadget simply has its staging actions skipped by the
    /// script.
    /// <para>发送包的引导链段落：把 FSBL 与 U-Boot 暂存进内存并用 <c>continue</c> 移交，使 BootROM 下载模式转为 U-Boot
    /// fastboot gadget。不会有任何内容写入存储，因此该运行不需要确认短语；已在运行 U-Boot gadget 的板卡会由脚本自行跳过
    /// 暂存动作。</para>
    /// </summary>
    /// <returns>A task that completes when the boot chain finished, was cancelled or failed.
    /// <para>引导链完成、被取消或失败时结束的任务。</para></returns>
    internal async Task SendSpacemiTBootChainAsync()
    {
        if (_busy || _confirming)
        {
            return;
        }

        if (_spacemiTPackage is not { } package)
        {
            AppendLog(_localizer.GetString("Flash_SpacemiTNoPackage"));
            return;
        }

        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Fastboot } device)
        {
            return;
        }

        if (device.FastbootStage == SpacemiTBootStage.Uboot)
        {
            AppendLog(_localizer.GetString("Flash_SpacemiTBootChainAlreadyUboot"));
            return;
        }

        if (await RunSpacemiTPackageAsync(device, package, SpacemiTFlashScope.BootChain).ConfigureAwait(true))
        {
            AppendLog(_localizer.GetString("Flash_SpacemiTBootChainSent"));
        }
    }

    /// <summary>
    /// Flashes the loaded package to the selected board: partition tables and the images the selected rows name are
    /// written, and the boot-chain section is never attempted, so the board has to be running the U-Boot gadget already.
    /// The typed confirmation is required because this writes storage.
    /// <para>把已加载的包烧写到所选板卡：写入分区表与所选行引用的镜像，且绝不尝试引导链段落，因此板卡必须已在运行
    /// U-Boot gadget。由于会写入存储，必须通过输入式确认。</para>
    /// </summary>
    /// <returns>A task that completes when the flash finished, was cancelled or failed.
    /// <para>烧录完成、被取消或失败时结束的任务。</para></returns>
    internal async Task FlashSpacemiTPackageAsync()
    {
        if (_busy || _confirming)
        {
            return;
        }

        if (_spacemiTPackage is not { } package)
        {
            AppendLog(_localizer.GetString("Flash_SpacemiTNoPackage"));
            return;
        }

        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Fastboot } device)
        {
            return;
        }

        if (device.FastbootStage == SpacemiTBootStage.BromFdl)
        {
            AppendLog(_localizer.GetString("Flash_SpacemiTNeedsBootChain"));
            return;
        }

        if (!Partitions.Any(row => row.IsSelected && row.Name.Length > 0))
        {
            AppendLog(_localizer.GetString("Flash_SpacemiTNoPartitions"));
            return;
        }

        if (TopLevel.GetTopLevel(Surface) is not Window owner)
        {
            return;
        }

        var language = _localizer;
        bool confirmed;
        _confirming = true;
        try
        {
            confirmed = await DialogService.ConfirmDangerousAsync(owner, new DangerousConfirmRequest
            {
                TabTitle = language.GetString("Flash_FlashSelected"),
                Title = language.GetString("Flash_SpacemiTFlashTitle"),
                Detail = language.GetString("Flash_SpacemiTConfirmDetail"),
                ConfirmWord = SpacemiTFlashOptions.RequiredConfirmation,
                DeviceIdentifier = _deviceViewModel?.SelectedDevice?.ConfirmationIdentity
            }).ConfigureAwait(true);
        }
        finally
        {
            _confirming = false;
        }

        if (!confirmed)
        {
            return;
        }

        if (await RunSpacemiTPackageAsync(device, package, SpacemiTFlashScope.Storage).ConfigureAwait(true))
        {
            AppendLog(_localizer.GetString("Flash_SpacemiTStorageDone"));
        }
    }

    /// <summary>
    /// Runs one section of the package script against the selected board and turns the flasher's reports into page
    /// state instead of log lines: byte progress moves the row of the partition being written and the overall bar,
    /// while the log keeps only what the run is doing and what the user should do next. A staging transfer has no
    /// partition, so its progress drives the overall bar alone.
    /// <para>对所选板卡执行包脚本的一个段落，并把烧录器的报告变成页面状态而不是日志行：字节进度推动正在写入的分区行与总进度条，
    /// 日志只保留本次运行在做什么以及用户下一步该做什么。暂存传输没有分区，其进度只驱动总进度条。</para>
    /// </summary>
    /// <param name="device">The selected SpacemiT fastboot endpoint. <para>所选进迭时空 fastboot 端点。</para></param>
    /// <param name="package">The loaded package. <para>已加载的烧录包。</para></param>
    /// <param name="scope">The section to execute. <para>要执行的段落。</para></param>
    /// <returns><see langword="true"/> when the section finished; <see langword="false"/> when it was cancelled or failed.
    /// <para>段落完成时为 <see langword="true"/>；被取消或失败时为 <see langword="false"/>。</para></returns>
    private async Task<bool> RunSpacemiTPackageAsync(DetectedDevice device, SpacemiTFlashPackage package,
        SpacemiTFlashScope scope)
    {
        var options = new SpacemiTFlashOptions
        {
            Scope = scope,
            FlashConfirmation = scope == SpacemiTFlashScope.BootChain
                ? string.Empty
                : SpacemiTFlashOptions.RequiredConfirmation,
            RebootAfterFlash = scope == SpacemiTFlashScope.Storage &&
                Field<CheckBox>("RebootAfterBox").IsChecked == true,
            VerifyAfterFlash = scope == SpacemiTFlashScope.Storage && this.VerifyAfterFlash
        };
        if (scope == SpacemiTFlashScope.Storage)
        {
            // Script-step rows never enter the storage selection: they have no individually runnable write, and a
            // staged file's name is not a partition the device would honour.
            // 脚本步骤行绝不进入存储选择：它们没有可单独执行的写入，且暂存文件名也不是设备认可的分区。
            var partitionCount = Partitions.Count(entry => entry.Name.Length > 0 && !entry.IsScriptStep);
            var chosen = Partitions.Where(entry => entry.IsSelected && entry.Name.Length > 0 && !entry.IsScriptStep)
                .Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (chosen.Length > 0 && chosen.Length < partitionCount)
            {
                options.OnlyPartitions = chosen;
            }

            // A row the package gives no image for flashes the file picked in its own cell; its file path is the
            // override the flasher writes for exactly that partition. The flasher applies overrides only to
            // partitions the tables leave without an image, so a package-image row's path here is inert. Rows with
            // neither a package image nor a picked file are skipped and said so.
            // 包内未给镜像的行刷其文件列选择的文件；该文件路径就是烧录器为此分区写入的覆盖。烧录器只对布局表未给
            // 镜像的分区应用覆盖，因此包内镜像行在此处的路径不会生效。既无包内镜像也未选文件的行被跳过并记录。
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            var skipped = new List<string>();
            foreach (var row in Partitions.Where(row => row.IsSelected && row.Name.Length > 0 && !row.IsScriptStep))
            {
                if (row.FilePath.Length > 0)
                {
                    overrides.TryAdd(row.Name, row.FilePath);
                }
                else if (row.FileName.Length == 0 && row.AcceptsFile)
                {
                    skipped.Add(row.Name);
                }
            }

            if (overrides.Count > 0)
            {
                options.PartitionImageOverrides = overrides;
            }

            if (skipped.Count > 0)
            {
                AppendLog(string.Format(_localizer.GetString("Flash_SpacemiTNoImageSkipped"),
                    string.Join(", ", skipped)));
            }
        }

        var targetRows = scope == SpacemiTFlashScope.Storage
            ? Partitions.Where(row => row.Name.Length > 0 && !row.IsScriptStep &&
                    // A selected row without a package image and without a picked file is logged as skipped above and
                    // never queues: marking it successful would describe a write the run did not perform.
                    // 有勾选但既无包内镜像也未选文件的行已在上方日志记为跳过，绝不排队：标成成功等于描述一次未发生的写入。
                    !(row.AcceptsFile && row.FileName.Length == 0 && row.FilePath.Length == 0) &&
                    (options.OnlyPartitions is not { Count: > 0 } chosenNames ||
                        chosenNames.Contains(row.Name, StringComparer.OrdinalIgnoreCase)))
                .ToArray()
            : [];
        var rowsByName = new Dictionary<string, FlashPartitionEntry>(StringComparer.OrdinalIgnoreCase);
        var scrolls = scope == SpacemiTFlashScope.BootChain;
        _overallScrolls = scrolls;
        if (scope == SpacemiTFlashScope.Storage)
        {
            SetRowsQueued(targetRows);
            foreach (var row in targetRows)
            {
                rowsByName[row.Name] = row;
            }
        }
        else
        {
            _overallRows = [];
            OverallPercent = 0;
            IsOverallIndeterminate = true;
            OverallText = _localizer.GetString("Flash_SpacemiTBootChainRunning");
        }

        var progress = new Progress<SpacemiTFlashProgress>(update =>
            ApplySpacemiTProgress(update, rowsByName, scrolls));
        AppendLog(_localizer.GetString(scope == SpacemiTFlashScope.BootChain
            ? "Flash_SpacemiTBootChainRunning"
            : "Flash_SpacemiTStorageRunning"));

        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        _deviceViewModel?.BeginSelectionHold();
        try
        {
            await App.DeviceMonitor.RunSpacemiTFlashAsync(device, package, options, progress,
                cancellation.Token).ConfigureAwait(true);
            if (scope == SpacemiTFlashScope.Storage)
            {
                foreach (var row in targetRows.Where(row =>
                             row.State is FlashRowState.Queued or FlashRowState.Running))
                {
                    SetRowFinished(row, success: true);
                }

                UpdateOverall();
            }
            else
            {
                OverallPercent = 100;
                IsOverallIndeterminate = false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            MarkUnfinishedSkipped();
            UpdateOverall();
            AppendLog(_localizer.GetString("Flash_Canceled"));
            return false;
        }
        catch (Exception ex)
        {
            foreach (var row in targetRows.Where(row => row.State is FlashRowState.Running))
            {
                SetRowFinished(row, success: false);
            }

            MarkUnfinishedSkipped();
            UpdateOverall();
            AppendError(ex);
            return false;
        }
        finally
        {
            _overallScrolls = false;
            if (IsOverallIndeterminate && scope == SpacemiTFlashScope.BootChain)
            {
                IsOverallIndeterminate = false;
                OverallPercent = 0;
                OverallText = string.Empty;
            }

            SetBusy(false);
            _flashCancellation = null;
            UpdateEdlButton();
        }
    }

    /// <summary>
    /// Applies one flasher report to the page: a report that names a partition moves that row, and a byte report
    /// without one (the boot-chain staging) moves the overall bar. Reports are the only place progress comes from —
    /// nothing here writes to the log, so a long gzip transfer cannot flood it.
    /// <para>把一条烧录器报告应用到页面：点名分区的报告推动该行，不带分区的字节报告（引导链暂存）推动总进度条。报告是进度的唯一
    /// 来源——这里不写日志，因此长时间 gzip 传输不会灌满日志。</para>
    /// </summary>
    /// <param name="update">The report. <para>进度报告。</para></param>
    /// <param name="rowsByName">Rows of the partitions the run targets. <para>本次运行目标分区的行。</para></param>
    /// <param name="scrolls">Whether this section has no overall fraction and keeps the bars scrolling.
    /// <para>该段落是否没有整体比例、总进度条保持滚动。</para></param>
    internal void ApplySpacemiTProgress(SpacemiTFlashProgress update,
        IReadOnlyDictionary<string, FlashPartitionEntry> rowsByName, bool scrolls)
    {
        var percent = update is { BytesCompleted: { } done, BytesTotal: { } total } && total > 0
            ? (int)Math.Clamp(done * 100d / total, 0d, 100d)
            : -1;

        if (update.Partition is { Length: > 0 } name && rowsByName.TryGetValue(name, out var row))
        {
            if (row.State is not FlashRowState.Running)
            {
                SetRowRunning(row, "Flash_Status_Flashing");
            }

            if (update.BytesCompleted is null)
            {
                return;
            }

            var indeterminate = percent < 0;
            if (row.IsIndeterminate == indeterminate && (indeterminate || percent == row.Percent))
            {
                return;
            }

            row.IsIndeterminate = indeterminate;
            if (!indeterminate)
            {
                row.Percent = percent;
                if (update.BytesTotal is { } transferTotal)
                {
                    row.TransferBytes = transferTotal;
                }
            }

            UpdateOverall();
            return;
        }

        if (update.BytesCompleted is not null)
        {
            if (scrolls)
            {
                return;
            }

            if (percent >= 0)
            {
                OverallPercent = percent;
            }

            IsOverallIndeterminate = percent < 0;
            OverallText = update.Message;
            UpdateBusyProgress();
            return;
        }

        if (update.Phase == "flash-table")
        {
            OverallText = _localizer.GetString("Flash_SpacemiTWritingTable");
        }
    }
}