using System.Globalization;
using Avalonia.Controls;
using UotanStudio.Firmware.Executable.Qualcomm;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// The <c>loader</c> row says which boot image the selected device runs before it can be written, so the row is
/// parsed against that device rather than against one vendor's image format.
/// <para><c>loader</c> 行声明所选设备在可写入之前要运行的引导镜像，因此该行按设备解析，而不是只按某一家厂商的镜像格式解析。</para>
/// </summary>
/// <remarks>
/// A Rockchip configuration names the loader explicitly, so the row has to keep working for the vendor tool's own
/// files while the same row also carries a Sahara programmer for an EDL device or a download agent for an MTK device.
/// The device decides which grammar is applied; with no device attached every grammar is tried, so an imported
/// configuration states what its loader row actually holds instead of claiming it is invalid.
/// <para>瑞芯微配置会显式写出 loader，因此该行既要继续接受厂商工具自身的文件，也要能在同一行里承载 EDL 设备的
/// Sahara programmer 或 MTK 设备的下载代理。由设备决定套用哪种语法；未连接设备时逐种尝试，使导入的配置直接说明其
/// loader 行到底是什么，而不是一律判为无效。</para>
/// </remarks>
public sealed partial class FlashViewModel
{
    /// <summary>
    /// Which boot-image grammar the <c>loader</c> row is parsed with.
    /// <para><c>loader</c> 行使用哪种引导镜像语法解析。</para>
    /// </summary>
    internal enum LoaderTarget
    {
        /// <summary>Nothing bound the row to one grammar: every known grammar is tried, and the verdict that
        /// recognized the file wins.
        /// <para>没有任何语法被绑定：逐一尝试所有已知语法，由识别出该文件的那个结论胜出。</para></summary>
        Any,
        /// <summary>Rockchip loader image, booted by the RKDL download path.
        /// <para>瑞芯微 loader 镜像，由 RKDL 下载路径启动。</para></summary>
        Rockchip,
        /// <summary>Sahara programmer image (ELF, MBN or MELF), uploaded by an EDL session.
        /// <para>Sahara programmer 镜像（ELF、MBN 或 MELF），由 EDL 会话上传。</para></summary>
        EdlProgrammer,
        /// <summary>MediaTek download agent, which the MTK path matches against the device's hardware codes.
        /// <para>联发科下载代理，由 MTK 路径与设备的硬件码匹配。</para></summary>
        MtkPreloader
    }

    /// <summary>
    /// Maps one protocol to the grammar its boot image must follow.
    /// <para>把一种协议映射为其引导镜像必须遵循的语法。</para>
    /// </summary>
    /// <param name="protocol">Protocol of the selected device, or <see langword="null"/> when none is selected.
    /// <para>所选设备的协议；未选中设备时为 <see langword="null"/>。</para></param>
    /// <returns>The grammar to parse the row with.<para>解析该行所用的语法。</para></returns>
    internal static LoaderTarget ResolveLoaderTarget(DeviceProtocolKind? protocol) => protocol switch
    {
        DeviceProtocolKind.Rkdl => LoaderTarget.Rockchip,
        DeviceProtocolKind.Edl => LoaderTarget.EdlProgrammer,
        DeviceProtocolKind.Mtk => LoaderTarget.MtkPreloader,
        _ => LoaderTarget.Any
    };

    /// <summary>
    /// The grammar the selected device imposes on the <c>loader</c> row right now.
    /// <para>所选设备此刻对 <c>loader</c> 行施加的语法。</para>
    /// </summary>
    private LoaderTarget CurrentLoaderTarget => ResolveLoaderTarget(SelectedDevice?.Protocol);

    /// <summary>
    /// The file the <c>loader</c> row carries, falling back to the Rockchip loader an earlier action applied, so
    /// exporting a configuration keeps the row intact across a load-then-save round trip.
    /// <para><c>loader</c> 行携带的文件；该行没有文件时回退为先前动作套用的瑞芯微 loader，使配置在“载入再保存”后
    /// 仍保留该行。</para>
    /// </summary>
    private string? LoaderRowPath
    {
        get
        {
            var row = Partitions.FirstOrDefault(entry =>
                entry.Command == LoaderCommand && entry.FilePath.Length > 0);
            return row?.FilePath ?? (_rkLoaderPath is { Length: > 0 } path ? path : null);
        }
    }

    /// <summary>
    /// What one candidate loader file turned out to be: which grammar it matched, whether that grammar accepted it,
    /// and the complaint to log when it did not.
    /// <para>一个候选 loader 文件的实际结论：匹配了哪种语法、该语法是否接受它，以及不接受时要记录的说明。</para>
    /// </summary>
    /// <param name="Target">Grammar that recognized the file, or <see cref="LoaderTarget.Any"/> when none did.
    /// <para>识别出该文件的语法；一种都没有识别出来时为 <see cref="LoaderTarget.Any"/>。</para></param>
    /// <param name="Valid">Whether the grammar accepted the whole file.
    /// <para>该语法是否接受了整个文件。</para></param>
    /// <param name="Recognized">Whether the grammar identified the file as its own format and rejected it on a
    /// deeper check, which keeps a damaged Rockchip loader from being reported as an unknown file.
    /// <para>该语法是否已把文件识别为自己的格式、而在更深一层检查上拒绝它；这使损坏的瑞芯微 loader 不会被报告为
    /// 未知文件。</para></param>
    /// <param name="ProblemKey">Localization key of the failure to log, or <see langword="null"/> when accepted.
    /// <para>失败时记录的本地化键；接受时为 <see langword="null"/>。</para></param>
    /// <param name="Summary">What the grammar reported about the file, empty when it reported nothing.
    /// <para>语法对该文件的说明；没有说明时为空。</para></param>
    internal readonly record struct LoaderFileVerdict(
        LoaderTarget Target, bool Valid, bool Recognized, string? ProblemKey, string Summary);

    /// <summary>
    /// Re-checks the file of the <c>loader</c> row against the selected device and publishes the verdict on the row.
    /// <para>按所选设备重新检查 <c>loader</c> 行的文件，并把结论发布到该行。</para>
    /// </summary>
    /// <param name="row">Loader row whose file is checked; its text is replaced by the verdict.
    /// <para>待检查文件的 loader 行；其文本会被结论替换。</para></param>
    /// <param name="knownValid">A verdict the caller already computed for the very same file in the very same
    /// grammar, or <see langword="null"/> to check here.
    /// <para>调用方对同一文件、同一语法已算出的结论；<see langword="null"/> 表示在此检查。</para></param>
    /// <returns>A task that completes when the row carries the verdict.
    /// <para>行已携带结论时完成的任务。</para></returns>
    private async Task UpdateLoaderRowStatusAsync(FlashPartitionEntry row, bool? knownValid = null)
    {
        if (row.FilePath.Length == 0)
        {
            row.StatusText = string.Empty;
            return;
        }

        if (knownValid is { } valid)
        {
            PublishLoaderStatus(row, CurrentLoaderTarget, valid);
            RefreshRockchipLoaderGate();
            return;
        }

        row.StatusText = _localizer.GetString("Flash_LoaderChecking");
        try
        {
            var verdict = await CheckLoaderFileAsync(row.FilePath, CurrentLoaderTarget, CancellationToken.None)
                .ConfigureAwait(true);
            PublishLoaderStatus(row, verdict.Target, verdict.Valid);
            await ApplyLoaderFileAsync(row.FilePath, verdict).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppendError(ex);
            PublishLoaderStatus(row, CurrentLoaderTarget, valid: false);
        }

        RefreshRockchipLoaderGate();
    }

    /// <summary>
    /// Checks one loader file with the grammar the selected device imposes and reports the outcome once.
    /// <para>用所选设备施加的语法检查一个 loader 文件，并只报告一次结论。</para>
    /// </summary>
    /// <remarks>
    /// With no device bound, the verdict that identified the file wins over the generic one.
    /// <para>设备未定时优先采用“已识别格式但内容不合法”的那个结论，其余情况才退回通用措辞。</para>
    /// When no grammar recognized the file, the row wording follows the binding: a Rockchip device reports a
    /// rejected Rockchip loader, while an unbound row only reports a rejected boot image.
    /// <para>没有任何语法认出这个文件时，行上的措辞跟随设备的绑定：绑定瑞芯微时说瑞芯微 loader 无效，
    /// 未绑定时只说引导文件无效，因为此时连它属于哪种格式都无从判断。</para>
    /// </remarks>
    /// <param name="path">Candidate loader file; it is opened read-only.
    /// <para>候选 loader 文件；以只读方式打开。</para></param>
    /// <param name="target">Grammar imposed by the selected device.
    /// <para>所选设备施加的语法。</para></param>
    /// <param name="cancellationToken">Cancels the read.<para>取消读取。</para></param>
    /// <returns>The verdict, already logged.<para>已记录日志的结论。</para></returns>
    internal async Task<LoaderFileVerdict> CheckLoaderFileAsync(string path, LoaderTarget target,
        CancellationToken cancellationToken)
    {
        var display = Path.GetFileName(path);
        if (!File.Exists(path))
        {
            AppendLog(string.Format(CultureInfo.InvariantCulture, _localizer.GetString("Flash_LoaderMissing"), path));
            return new(target, false, false, "Flash_LoaderMissing", string.Empty);
        }

        var verdicts = new List<LoaderFileVerdict>();
        LoaderTarget[] grammars = target == LoaderTarget.Any
            ? [LoaderTarget.Rockchip, LoaderTarget.EdlProgrammer, LoaderTarget.MtkPreloader]
            : [target];
        foreach (var candidate in grammars)
        {
            var verdict = await InspectLoaderAsync(path, candidate, cancellationToken).ConfigureAwait(true);
            if (verdict.Valid)
            {
                LogLoaderAccepted(display, verdict);
                return verdict;
            }

            verdicts.Add(verdict);
        }

        var closest = verdicts[0];
        foreach (var verdict in verdicts)
        {
            if (verdict.Recognized)
            {
                closest = verdict;
                break;
            }
        }

        if (!closest.Recognized)
        {
            closest = closest with { Target = target };
        }

        LogLoaderRejected(display, closest);
        return closest;
    }

    /// <summary>
    /// Reads one file with exactly one grammar and returns what that grammar made of it, without logging.
    /// <para>用且仅用一种语法读取一个文件，返回该语法的结论，不写日志。</para>
    /// </summary>
    /// <param name="path">Existing candidate file.<para>存在的候选文件。</para></param>
    /// <param name="target">Grammar to apply; <see cref="LoaderTarget.Any"/> never reaches this method.
    /// <para>要套用的语法；<see cref="LoaderTarget.Any"/> 不会进入本方法。</para></param>
    /// <param name="cancellationToken">Cancels the read.<para>取消读取。</para></param>
    /// <returns>The verdict for that one grammar.<para>该单一语法的结论。</para></returns>
    private async Task<LoaderFileVerdict> InspectLoaderAsync(string path, LoaderTarget target,
        CancellationToken cancellationToken)
    {
        switch (target)
        {
            case LoaderTarget.Rockchip:
                var rockchip = await Task.Run(() => InspectRkLoader(path), cancellationToken).ConfigureAwait(true);
                return new(LoaderTarget.Rockchip, rockchip.ProblemKey is null,
                    !string.Equals(rockchip.ProblemKey, "Flash_RkLoaderNotLoader", StringComparison.Ordinal),
                    rockchip.ProblemKey, rockchip.Summary);
            case LoaderTarget.EdlProgrammer:
                return await Task.Run(() => InspectEdlProgrammer(path), cancellationToken).ConfigureAwait(true);
            default:
                return await InspectMtkLoaderAsync(path, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Recognizes one file as a Sahara programmer and names the shape it follows.
    /// <para>把一个文件识别为 Sahara programmer 并说明它遵循的形态。</para>
    /// </summary>
    /// <param name="path">Existing candidate file, opened read-only.
    /// <para>存在的候选文件，以只读方式打开。</para></param>
    /// <returns>An accepted verdict naming the shape when a programmer was found, otherwise a rejection that is
    /// never marked as recognized, because a programmer has no deeper check to fail after its structure matched.
    /// <para>找到 programmer 时返回带形态的接受结论；否则返回永不被标记为“已识别”的拒绝结论，因为 programmer 在结构
    /// 匹配之后没有更深一层的检查会失败。</para></returns>
    private LoaderFileVerdict InspectEdlProgrammer(string path)
    {
        if (new FileInfo(path).Length > MaximumProgrammerFileBytes)
        {
            return new(LoaderTarget.Any, false, false, "Flash_EdlProgrammerTooLarge", string.Empty);
        }

        QualcommProgrammerShape shape;
        try
        {
            using var stream = File.OpenRead(path);
            shape = QualcommProgrammerProbe.Detect(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(LoaderTarget.Any, false, false, "Flash_EdlLoaderNotProgrammer", exception.Message);
        }

        return shape.Kind == QualcommProgrammerKind.Unknown
            ? new(LoaderTarget.Any, false, false, "Flash_EdlLoaderNotProgrammer", string.Empty)
            : new(LoaderTarget.EdlProgrammer, true, true, null, DescribeEdlProgrammer(shape));
    }

    /// <summary>
    /// Names a detected programmer shape for the log and the row, because "valid" alone does not tell a single ELF
    /// from a container of twenty.
    /// <para>为日志与行说明检测到的 programmer 形态，因为只说“有效”无法区分单个 ELF 与含二十个组件的容器。</para>
    /// </summary>
    /// <param name="shape">Detected shape with its component count.
    /// <para>检测到的形态及其组件数。</para></param>
    /// <returns>The shape name, with the component count appended for a container.
    /// <para>形态名称；容器会附上组件数。</para></returns>
    private string DescribeEdlProgrammer(QualcommProgrammerShape shape) => shape.Kind switch
    {
        QualcommProgrammerKind.Elf => "ELF",
        QualcommProgrammerKind.Mbn => "MBN",
        _ => string.Format(CultureInfo.InvariantCulture, _localizer.GetString("Flash_EdlLoaderMelf"),
            shape.ComponentCount)
    };

    /// <summary>
    /// Reads one MediaTek download agent and counts the loader entries the MTK path could boot.
    /// <para>读取一个联发科下载代理，并统计 MTK 路径可启动的 loader 条目。</para>
    /// </summary>
    /// <param name="path">Existing candidate file.<para>存在的候选文件。</para></param>
    /// <param name="cancellationToken">Cancels the parse.<para>取消解析。</para></param>
    /// <returns>An accepted verdict carrying the entry count when the file holds at least one usable loader entry;
    /// otherwise a rejection, which is never marked as recognized because the MTK header is not proof of a DA.
    /// <para>文件至少含一个可用 loader 条目时返回带条目数的接受结论；否则返回拒绝结论，且从不标记为“已识别”，因为
    /// MTK 头部并不能证明它就是下载代理。</para></returns>
    private async Task<LoaderFileVerdict> InspectMtkLoaderAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var entries = await MtkScatterPackage.ListUsableLoaderEntriesAsync(path, cancellationToken)
                .ConfigureAwait(true);
            return entries.Count > 0
                ? new(LoaderTarget.MtkPreloader, true, true, null,
                    string.Format(CultureInfo.InvariantCulture, _localizer.GetString("Flash_MtkLoaderEntries"),
                        entries.Count))
                : new(LoaderTarget.Any, false, false, "Flash_MtkLoaderNoEntries", string.Empty);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            return new(LoaderTarget.Any, false, false, "Flash_MtkLoaderInvalid", exception.Message);
        }
    }

    /// <summary>
    /// Writes the acceptance line, which names the grammar that matched and what that grammar saw.
    /// <para>写入接受日志，说明匹配的语法与该语法的观察结果。</para>
    /// </summary>
    /// <param name="display">Bare file name shown to the user.<para>展示给用户的纯文件名。</para></param>
    /// <param name="verdict">Accepted verdict.<para>被接受的结论。</para></param>
    private void LogLoaderAccepted(string display, LoaderFileVerdict verdict)
        => AppendLog(string.Format(CultureInfo.InvariantCulture, _localizer.GetString(verdict.Target switch
        {
            LoaderTarget.Rockchip => "Flash_RkLoaderChecked",
            LoaderTarget.MtkPreloader => "Flash_MtkLoaderChecked",
            _ => "Flash_EdlLoaderChecked"
        }), display, verdict.Summary));

    /// <summary>
    /// Writes the rejection line, which names the grammar that rejected the file and why it did.
    /// <para>写入拒绝日志，说明拒绝该文件的语法以及原因。</para>
    /// </summary>
    /// <param name="display">Bare file name shown to the user.<para>展示给用户的纯文件名。</para></param>
    /// <param name="verdict">Closest failing verdict.<para>最接近的失败结论。</para></param>
    private void LogLoaderRejected(string display, LoaderFileVerdict verdict)
    {
        var key = verdict.ProblemKey ?? verdict.Target switch
        {
            LoaderTarget.Rockchip => "Flash_RkLoaderNotLoader",
            LoaderTarget.MtkPreloader => "Flash_MtkLoaderNoEntries",
            _ => "Flash_EdlLoaderNotProgrammer"
        };
        AppendLog(string.Format(CultureInfo.InvariantCulture, _localizer.GetString(key), display, verdict.Summary));
    }

    /// <summary>
    /// Publishes the verdict on the row, whose wording follows the grammar that produced it.
    /// <para>把结论发布到该行，其措辞跟随产生该结论的语法。</para>
    /// </summary>
    /// <param name="row">Row carrying the verdict.<para>承载结论的行。</para></param>
    /// <param name="target">Grammar that produced it.<para>产生该结论的语法。</para></param>
    /// <param name="valid">Whether the file was accepted.<para>文件是否被接受。</para></param>
    private void PublishLoaderStatus(FlashPartitionEntry row, LoaderTarget target, bool valid)
        => row.StatusText = _localizer.GetString(target == LoaderTarget.Rockchip
            ? valid ? "Flash_RkLoaderValid" : "Flash_RkLoaderInvalidStatus"
            : valid ? "Flash_LoaderValid" : "Flash_LoaderInvalidStatus");

    /// <summary>
    /// Hands an accepted loader file to the backend that consumes it, so checking the row also configures the flash.
    /// <para>把被接受的 loader 文件交给消费它的后端，使检查该行的同时完成刷写配置。</para>
    /// </summary>
    /// <param name="path">Accepted file.<para>被接受的文件。</para></param>
    /// <param name="verdict">Verdict naming the grammar that accepted it.
    /// <para>说明由哪种语法接受的结论。</para></param>
    /// <returns>A task that completes when the backend holds the path.
    /// <para>后端已持有该路径时完成的任务。</para></returns>
    private async Task ApplyLoaderFileAsync(string path, LoaderFileVerdict verdict)
    {
        if (!verdict.Valid)
        {
            return;
        }

        switch (verdict.Target)
        {
            case LoaderTarget.Rockchip:
                ApplyRkLoaderPath(path);
                break;
            case LoaderTarget.EdlProgrammer:
                ApplyEdlLoaderPath(path);
                break;
            case LoaderTarget.MtkPreloader:
                await ApplyMtkLoaderFileAsync(path, CancellationToken.None).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>
    /// Points the EDL session at the loader row's file, which is what the programmer picker shows as the custom row.
    /// <para>把 EDL 会话指向 loader 行的文件，programmer 选择框以自选档显示它。</para>
    /// </summary>
    /// <param name="path">Accepted Sahara programmer.<para>被接受的 Sahara programmer。</para></param>
    private void ApplyEdlLoaderPath(string path)
    {
        _edlProgrammerOverride = path;
        _isCustomProgrammer = true;
        _edlProgrammer = null;
        UpdateEdlProgrammerPickOptions();
        UpdateEdlButton();
    }

    /// <summary>
    /// Points the MTK path at the loader row's file, publishing the loader entries the user picks from.
    /// <para>把 MTK 路径指向 loader 行的文件，并发布供用户选择的 loader 条目。</para>
    /// </summary>
    /// <param name="path">Accepted download agent.<para>被接受的下载代理。</para></param>
    /// <param name="cancellationToken">Cancels the entry listing.<para>取消条目列举。</para></param>
    /// <returns><see langword="true"/> when the file carries at least one usable entry, which is the same condition
    /// the DA picker enforces.<para>文件至少含一个可用条目时返回 <see langword="true"/>，与 DA 选择器的条件一致。</para></returns>
    private async Task<bool> ApplyMtkLoaderFileAsync(string path, CancellationToken cancellationToken)
    {
        var entries = await MtkScatterPackage.ListUsableLoaderEntriesAsync(path, cancellationToken)
            .ConfigureAwait(true);
        if (entries.Count == 0)
        {
            return false;
        }

        _mtkLoaderPath = path;
        _mtkLoaderEntries = entries;
        var indexBox = Field<ComboBox>("MtkEntryIndexBox");
        indexBox.ItemsSource = entries.Select(entry => string.Format(CultureInfo.InvariantCulture,
            _localizer.GetString("Flash_MtkLoaderEntry"), entry.Index, entry.HardwareCode.ToString("X4"),
            entry.HardwareSubCode.ToString("X4"),
            $"{entry.HardwareVersion}{(entry.SoftwareVersion is { } software ? $".{software}" : string.Empty)}"))
            .ToArray();
        indexBox.SelectedIndex = 0;
        var pathText = Field<TextBlock>("MtkDaPathText");
        pathText.Text = Path.GetFileName(path);
        ToolTip.SetTip(pathText, path);
        return true;
    }

    /// <summary>
    /// Loads the <c>loader</c> row as the boot image of the selected device when it has one, so the load-boot button
    /// honours the row instead of asking for a file the row already names.
    /// <para>所选设备的 <c>loader</c> 行有文件时把它加载为引导镜像，使「加载引导」按钮遵循该行，而不是再索要该行
    /// 已经指定的文件。</para>
    /// </summary>
    /// <returns><see langword="true"/> when the row supplied an accepted boot image, <see langword="false"/> when the
    /// caller has to ask for one.<para>该行提供了被接受的引导镜像时为 <see langword="true"/>；调用方仍需索要文件时为
    /// <see langword="false"/>。</para></returns>
    internal async Task<bool> TryLoadBootFromLoaderRowAsync()
    {
        if (_busy || _confirming || CurrentLoaderTarget == LoaderTarget.Any)
        {
            return false;
        }

        var row = Partitions.FirstOrDefault(entry =>
            entry.Command == LoaderCommand && entry.FilePath.Length > 0);
        if (row is not { } loaderRow)
        {
            return false;
        }

        using var cancellation = new CancellationTokenSource();
        _flashCancellation = cancellation;
        SetBusy(true);
        try
        {
            var verdict = await CheckLoaderFileAsync(loaderRow.FilePath, CurrentLoaderTarget, cancellation.Token)
                .ConfigureAwait(true);
            PublishLoaderStatus(loaderRow, verdict.Target, verdict.Valid);
            await ApplyLoaderFileAsync(loaderRow.FilePath, verdict).ConfigureAwait(true);
            return verdict.Valid;
        }
        catch (OperationCanceledException)
        {
            AppendLog(_localizer.GetString("Flash_Canceled"));
            return false;
        }
        finally
        {
            SetBusy(false);
            _flashCancellation = null;
            UpdateEdlButton();
        }
    }
}