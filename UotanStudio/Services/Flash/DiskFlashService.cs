using UotanStudio.DD;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Images;
using UotanStudio.DD.Writing;
using DiskFlashProgress = UotanStudio.DD.FlashProgress;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Flash execution used by the host surfaces: reads the drives, writes the image and verifies the result,
/// asking for a privileged process when this one may not write the selected drives.
/// <para>宿主界面使用的烧录执行：读取驱动器、写入镜像并校验结果；本进程无权写入选中驱动器时请求提权进程。</para>
/// </summary>
public interface IDiskFlashService
{
    /// <summary>
    /// Gets whether the current process may already write raw disks, so no privilege request is needed.
    /// <para>获取当前进程是否已可写入裸磁盘，因而无需请求提权。</para>
    /// </summary>
    bool HasWriteAccess { get; }

    /// <summary>
    /// Enumerates the physical drives that may receive an image.
    /// <para>枚举可接收镜像的物理驱动器。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the scan.<para>取消扫描。</para></param>
    /// <returns>Drives in platform enumeration order.<para>按平台枚举顺序排列的驱动器。</para></returns>
    Task<IReadOnlyList<FlashDrive>> ScanDrivesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates the drives in a process started with administrator rights, which is the only way an ordinary
    /// host token can read every drive's capacity and write state.
    /// <para>在以管理员权限启动的进程中枚举驱动器；普通宿主令牌只有这样才能读到每个驱动器的容量与写入状态。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.<para>取消等待。</para></param>
    /// <returns>What the privileged scan resolved and why it may be incomplete.
    /// <para>提权扫描解析出的内容以及可能不完整的原因。</para></returns>
    Task<DiskScanOutcome> ScanDrivesElevatedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports whether this process may write one scanned drive, or <see langword="null"/> when unknown.
    /// <para>报告本进程能否写入某个已扫描驱动器；无法判定时为 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="drive">Drive to probe.<para>待探测的驱动器。</para></param>
    /// <param name="cancellationToken">Cancels the probe.<para>取消探测。</para></param>
    /// <returns>The verdict, or <see langword="null"/> when the platform could not decide.
    /// <para>探测结论；平台无法判定时为 <see langword="null"/>。</para></returns>
    Task<bool?> CanWriteDriveAsync(FlashDrive drive, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the image to the drives and verifies it, elevating only when the write requires it.
    /// <para>把镜像写入驱动器并校验；仅在写入需要时提权。</para>
    /// </summary>
    /// <param name="imagePath">Absolute path of the image.<para>镜像的绝对路径。</para></param>
    /// <param name="drives">Targets receiving the image.<para>接收镜像的目标。</para></param>
    /// <param name="options">Safety switches for the write.<para>写入的安全开关。</para></param>
    /// <param name="onProgress">Callback receiving progress from whichever process writes.
    /// <para>接收进度回调，无论由哪个进程写入。</para></param>
    /// <param name="cancellationToken">Cancels the write; an elevated writer is terminated with its tree.
    /// <para>取消写入；提权写入进程连同其进程树被终止。</para></param>
    /// <returns>The localized failure text, or <see langword="null"/> on success.
    /// <para>失败时的本地化文本；成功时为 <see langword="null"/>。</para></returns>
    Task<string?> FlashAsync(
        string imagePath,
        IReadOnlyList<FlashDrive> drives,
        FlashJobOptions options,
        IProgress<DiskFlashProgress>? onProgress,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IDiskFlashService"/>: scans with the platform scanner, writes in this process while it holds
/// the privilege, and otherwise asks the user before handing the job to an elevated writer.
/// <para><see cref="IFlashService"/> 的默认实现：使用平台扫描器扫描；本进程持有所需权限时直接写入，否则在征得
/// 用户同意后把任务交给提权写入进程。</para>
/// </summary>
public sealed class DiskFlashService : IDiskFlashService
{
    private const int ErrorCancelled = 1223;

    private readonly IDriveScanner _scanner;
    private readonly IDiskWriteProbe _probe;
    private readonly ImageFlasher _flasher;
    private readonly IFlashElevationPrompt _prompt;
    private readonly Func<string?> _executablePath;

    /// <summary>
    /// Gets the shared service used by the host surfaces.
    /// <para>获取宿主界面使用的共享服务。</para>
    /// </summary>
    public static DiskFlashService Default { get; } = new(DriveScanner.CreateDefault(),
        DiskWriteProbe.CreateDefault(), IFlashElevationPromptProvider.Current);

    /// <summary>
    /// Creates the service from the platform pieces.
    /// <para>由平台组件创建服务。</para>
    /// </summary>
    /// <param name="scanner">Drive scanner.<para>驱动器扫描器。</para></param>
    /// <param name="probe">Write-access probe.<para>写权限探测器。</para></param>
    /// <param name="prompt">Elevation prompt.<para>提权提示。</para></param>
    /// <param name="flasher">Flasher used while this process may write; <see langword="null"/> creates one.
    /// <para>本进程可写入时使用的烧录器；<see langword="null"/> 时新建。</para></param>
    /// <param name="executablePath">Supplies the executable started for a privileged write; <see langword="null"/>
    /// uses <see cref="Environment.ProcessPath"/>.<para>提供为特权写入启动的可执行文件；<see langword="null"/> 时使用 <see cref="Environment.ProcessPath"/>。</para></param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.
    /// <para>必需参数为 <see langword="null"/>。</para></exception>
    public DiskFlashService(IDriveScanner scanner, IDiskWriteProbe probe, IFlashElevationPrompt prompt,
        ImageFlasher? flasher = null, Func<string?>? executablePath = null)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(prompt);
        _scanner = scanner;
        _probe = probe;
        _prompt = prompt;
        _flasher = flasher ?? new ImageFlasher();
        _executablePath = executablePath ?? (() => Environment.ProcessPath);
    }

    /// <inheritdoc />
    public bool HasWriteAccess => _probe.IsElevated;

    /// <inheritdoc />
    public Task<IReadOnlyList<FlashDrive>> ScanDrivesAsync(CancellationToken cancellationToken = default) =>
        _scanner.ScanAsync(cancellationToken);

    /// <inheritdoc />
    public Task<DiskScanOutcome> ScanDrivesElevatedAsync(CancellationToken cancellationToken = default) =>
        DiskScanElevatedRunner.RunAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool?> CanWriteDriveAsync(FlashDrive drive, CancellationToken cancellationToken = default) =>
        _probe.CanWriteAsync(drive, cancellationToken);

    /// <inheritdoc />
    public async Task<string?> FlashAsync(
        string imagePath,
        IReadOnlyList<FlashDrive> drives,
        FlashJobOptions options,
        IProgress<DiskFlashProgress>? onProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(options);
        if (drives.Count == 0)
        {
            throw new ArgumentException("At least one target drive is required.", nameof(drives));
        }

        var detail = FormatDetail(imagePath, drives);
        if (!_probe.IsElevated)
        {
            if (!CanStartPrivilegedWriter())
            {
                await _prompt.ConfirmElevationAsync(FlashElevationReason.ElevationUnavailable, detail)
                    .ConfigureAwait(true);
                return LanguageService.Instance.GetString("DiskFlash_ElevationUnavailableBody");
            }

            if (!await _prompt.ConfirmElevationAsync(FlashElevationReason.PrivilegeMissing, detail)
                    .ConfigureAwait(true))
            {
                return LanguageService.Instance.GetString("DiskFlash_ElevationDeclined");
            }

            return ToFailure(await FlashElevatedJobRunner
                .RunAsync(
                    new FlashJob { ImagePath = imagePath, Drives = drives, Options = options },
                    progress => onProgress?.Report(progress),
                    cancellationToken)
                .ConfigureAwait(true));
        }

        try
        {
            var image = ImageProbe.Probe(imagePath);
            await _flasher.FlashAsync(image, drives, options.ToFlashOptions(), onProgress, cancellationToken)
                .ConfigureAwait(true);
            return null;
        }
        catch (OperationCanceledException)
        {
            return LanguageService.Instance.GetString("DiskFlash_Cancelled");
        }
        catch (FlashAccessDeniedException)
        {
            if (!CanStartPrivilegedWriter())
            {
                await _prompt.ConfirmElevationAsync(FlashElevationReason.ElevationUnavailable, detail)
                    .ConfigureAwait(true);
                return LanguageService.Instance.GetString("DiskFlash_ElevationUnavailableBody");
            }

            if (!await _prompt.ConfirmElevationAsync(FlashElevationReason.AccessDenied, detail)
                    .ConfigureAwait(true))
            {
                return LanguageService.Instance.GetString("DiskFlash_ElevationDeclined");
            }

            return ToFailure(await FlashElevatedJobRunner
                .RunAsync(
                    new FlashJob { ImagePath = imagePath, Drives = drives, Options = options },
                    progress => onProgress?.Report(progress),
                    cancellationToken)
                .ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is FlashException or IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private bool CanStartPrivilegedWriter()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var executablePath = _executablePath();
        return !string.IsNullOrEmpty(executablePath) &&
            !string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string? ToFailure(FlashJobRunResult run) => run.Status switch
    {
        FlashJobRunStatus.Succeeded => null,
        FlashJobRunStatus.Cancelled => LanguageService.Instance.GetString("DiskFlash_Cancelled"),
        FlashJobRunStatus.ElevationCancelled => LanguageService.Instance.GetString("DiskFlash_ElevationCancelled"),
        FlashJobRunStatus.AccessDenied => LanguageService.Instance.GetString("DiskFlash_ElevationDeniedFailure"),
        FlashJobRunStatus.ElevationUnavailable => LanguageService.Instance.GetString("DiskFlash_ElevationUnavailableBody"),
        _ => run.Message,
    };

    private static string FormatDetail(string imagePath, IReadOnlyList<FlashDrive> drives)
    {
        var targets = string.Join(Environment.NewLine, drives.Select(drive =>
            $"{drive.DisplayName} ({drive.DevicePath})"));
        return string.Format(
            LanguageService.Instance.GetString("DiskFlash_ElevationDetail"),
            Path.GetFileName(imagePath),
            targets);
    }
}

/// <summary>
/// Supplies the elevation prompt to the shared <see cref="DiskFlashService"/>; the desktop shell replaces the
/// detached fallback once a window surface exists.
/// <para>为共享的 <see cref="DiskFlashService"/> 提供提权提示；桌面 Shell 在窗口接口就绪后替换掉脱离界面的回退实现。</para>
/// </summary>
public static class IFlashElevationPromptProvider
{
    private static IFlashElevationPrompt _current = NullFlashElevationPrompt.Instance;

    /// <summary>
    /// Gets the prompt currently used for privilege requests.
    /// <para>获取当前用于权限请求的提示实现。</para>
    /// </summary>
    public static IFlashElevationPrompt Current => _current;

    /// <summary>
    /// Replaces the prompt used for privilege requests.
    /// <para>替换用于权限请求的提示实现。</para>
    /// </summary>
    /// <param name="prompt">The prompt to install; <see langword="null"/> restores the detached fallback.
    /// <para>要安装的提示实现；<see langword="null"/> 恢复脱离界面的回退实现。</para></param>
    public static void Set(IFlashElevationPrompt? prompt) =>
        _current = prompt ?? NullFlashElevationPrompt.Instance;
}