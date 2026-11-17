namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// One stage of a Tegra flash run, in the order the reference flasher performs them.
/// <para>Tegra 烧录运行的一个阶段，顺序与参考烧录器一致。</para>
/// </summary>
public enum TegraFlashStage
{
    /// <summary>
    /// Talk RCM to the boot ROM: query the RCM, boot ROM and boot device versions and synchronise.
    /// <para>与 boot ROM 进行 RCM 交互：查询 RCM、boot ROM 与引导设备版本并同步。</para>
    /// </summary>
    RcmHandshake = 0,

    /// <summary>
    /// Send the miniloader over RCM, which makes the device leave the boot ROM and run the miniloader.
    /// <para>经 RCM 发送 miniloader，使设备离开 boot ROM 并运行 miniloader。</para>
    /// </summary>
    MiniloaderDownload = 1,

    /// <summary>
    /// Read the platform information over nv3p, whose operating mode decides whether a download may follow.
    /// <para>经 nv3p 读取平台信息，其操作模式决定后续是否允许下载。</para>
    /// </summary>
    Nv3pHandshake = 2,

    /// <summary>
    /// Download the boot configuration table over nv3p. <para>经 nv3p 下载引导配置表。</para>
    /// </summary>
    BctDownload = 3,

    /// <summary>
    /// Download the bootloader binary over nv3p. <para>经 nv3p 下载 bootloader 二进制。</para>
    /// </summary>
    BootloaderDownload = 4,

    /// <summary>
    /// Write storage partitions. This layer never performs it, because partition writes run through the bootloader
    /// and not through nv3p; the member exists so a plan can name the stage a caller must handle elsewhere.
    /// <para>写入存储分区。本层从不执行该阶段，因为分区写入经 bootloader 而非 nv3p；该成员存在只是为了让计划能点名调用
    /// 方必须在别处处理的阶段。</para>
    /// </summary>
    PartitionWrite = 5,
}

/// <summary>
/// One step of a Tegra flash plan: the stage it belongs to, the target it names, the file it reads, its payload size
/// and whether the run cannot proceed without it.
/// <para>Tegra 烧录计划的一个步骤：所属阶段、点名的目标、读取的文件、负载大小，以及缺少它时运行是否无法继续。</para>
/// </summary>
/// <param name="Stage">The stage this step belongs to. <para>本步骤所属的阶段。</para></param>
/// <param name="Target">Target the step names, such as the payload name used in progress reports.
/// <para>步骤点名的目标，例如进度报告中使用的负载名。</para></param>
/// <param name="SourcePath">File the step reads, or <see langword="null"/> for a step that moves no file.
/// <para>步骤读取的文件；不移动文件的步骤为 <see langword="null"/>。</para></param>
/// <param name="Bytes">Payload size in bytes, or <see langword="null"/> when the step has no fixed size.
/// <para>负载大小（字节）；步骤没有固定大小时为 <see langword="null"/>。</para></param>
/// <param name="Required">Whether the run cannot proceed without this step.
/// <para>缺少该步骤时运行是否无法继续。</para></param>
public sealed record TegraFlashStep(TegraFlashStage Stage, string Target, string? SourcePath, long? Bytes,
    bool Required);

/// <summary>
/// Ordered plan of a Tegra flash run: the RCM handshake, the miniloader, the nv3p handshake, the BCT and the
/// bootloader, plus the chip the run targets. The plan is built from options and validated before any wire byte
/// moves; it never writes storage partitions, because partition writes go through the bootloader rather than nv3p.
/// <para>Tegra 烧录运行的有序计划：RCM 握手、miniloader、nv3p 握手、BCT 与 bootloader，以及目标芯片。计划由选项构建，
/// 并在任何线上字节移动之前完成校验；它从不写存储分区，因为分区写入经 bootloader 而非 nv3p。</para>
/// </summary>
public sealed class TegraFlashPlan
{
    private TegraFlashPlan(TegraChip chip, IReadOnlyList<TegraFlashStep> steps, long totalBytes)
    {
        Chip = chip;
        Steps = steps;
        TotalBytes = totalBytes;
    }

    /// <summary>Gets the chip generation the plan targets. <para>获取计划的目标芯片代际。</para></summary>
    public TegraChip Chip { get; }

    /// <summary>Gets the steps in execution order. <para>获取按执行顺序排列的步骤。</para></summary>
    public IReadOnlyList<TegraFlashStep> Steps { get; }

    /// <summary>
    /// Gets the sum of every step payload in bytes. <para>获取所有步骤负载的字节总和。</para>
    /// </summary>
    public long TotalBytes { get; }

    /// <summary>
    /// Gets whether the run needs signing key material: the miniloader and bootloader of an ODM secure device must
    /// be signed, and this layer refuses to fake a signature. <para>获取本次运行是否需要签名密钥材料：ODM 安全设备的
    /// miniloader 与 bootloader 必须签名，本层拒绝伪造签名。</para>
    /// </summary>
    /// <remarks>
    /// This is a property of the device answer, not of the plan shape, so it stays <see langword="false"/> here and is
    /// decided by the flasher from the operating mode it reads over nv3p.
    /// <para>该值取决于设备的应答而非计划的形态，因此在此保持 <see langword="false"/>，由烧录器按经 nv3p 读到的操作
    /// 模式决定。</para>
    /// </remarks>
    public bool RequiresSigningMaterial => false;

    /// <summary>
    /// Builds the plan of a run from validated options.
    /// <para>依据已校验的选项构建一次运行的计划。</para>
    /// </summary>
    /// <param name="options">The run options, which are validated first.
    /// <para>运行选项，先对其做校验。</para></param>
    /// <returns>The ordered plan. <para>有序计划。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The options are not confirmed or name files that do not exist.
    /// <para>选项未确认，或点名的文件不存在。</para></exception>
    public static TegraFlashPlan Create(TegraFlashOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var steps = new List<TegraFlashStep>
        {
            new(TegraFlashStage.RcmHandshake, "rcm", null, null, true),
            new(TegraFlashStage.MiniloaderDownload, "miniloader", options.MiniloaderPath,
                Length(options.MiniloaderPath), true),
            new(TegraFlashStage.Nv3pHandshake, "platform-info", null, null, true),
            new(TegraFlashStage.BctDownload, "bct", options.BctPath, Length(options.BctPath), true),
        };

        // The bootloader download is the point of the chain, but a caller may run only the boot configuration part,
        // so the step stays optional instead of making every run carry a bootloader it does not want.
        // bootloader 下载是整条链的目的，但调用方也可能只做引导配置部分，因此该步骤保持可选，而不是强迫每次运行都携带
        // 它并不想要的 bootloader。
        var bootloaderBytes = Length(options.BootloaderBinaryPath);
        if (bootloaderBytes is not null)
        {
            steps.Add(new TegraFlashStep(TegraFlashStage.BootloaderDownload, "bootloader",
                options.BootloaderBinaryPath, bootloaderBytes, false));
        }

        var total = 0L;
        foreach (var step in steps)
        {
            total += step.Bytes ?? 0;
        }

        var plan = new TegraFlashPlan(options.Chip, steps, total);
        plan.Validate();
        return plan;
    }

    /// <summary>
    /// Builds a plan from an already ordered step list without validating it, so the ordering rules of
    /// <see cref="Validate"/> can be exercised against a deliberately wrong plan. Production callers build plans
    /// through <see cref="Create(TegraFlashOptions)"/>, which validates what it builds.
    /// <para>从已排好序的步骤清单构建不做校验的计划，使 <see cref="Validate"/> 的顺序规则可以被故意构造的错误计划检验。
    /// 生产调用方经 <see cref="Create(TegraFlashOptions)"/> 构建计划，该入口会校验自己构建的内容。</para>
    /// </summary>
    /// <param name="chip">The chip generation the steps target. <para>步骤的目标芯片代际。</para></param>
    /// <param name="steps">The steps, in the order the caller wants them checked.
    /// <para>步骤，按调用方希望被检查的顺序。</para></param>
    /// <returns>The unvalidated plan. <para>未校验的计划。</para></returns>
    internal static TegraFlashPlan CreateUnvalidated(TegraChip chip, IReadOnlyList<TegraFlashStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var total = 0L;
        foreach (var step in steps)
        {
            total += step.Bytes ?? 0;
        }

        return new TegraFlashPlan(chip, steps, total);
    }

    /// <summary>
    /// Validates the plan before any device I/O: every step that reads a file has an existing, non-empty file, the
    /// stages never move backwards, the BCT precedes the bootloader, and the plan never claims a storage partition
    /// write this layer cannot perform.
    /// <para>在任何设备 I/O 之前校验计划：读取文件的步骤都有存在且非空的文件，阶段从不倒退，BCT 先于 bootloader，且计划
    /// 从不声称执行本层无法完成的存储分区写入。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">A step names a missing or empty file, the stages are not
    /// monotonic, the BCT is missing or follows the bootloader, the plan names partition writing, or the chip is
    /// undefined. <para>某步骤点名的文件缺失或为空、阶段非单调、BCT 缺失或排在 bootloader 之后、计划点名分区写入，或
    /// 芯片未定义。</para></exception>
    public void Validate()
    {
        if (!TegraChips.IsKnown(Chip))
        {
            throw new InvalidOperationException($"Chip {Chip} is not a defined Tegra generation.");
        }

        if (Steps.Count == 0)
        {
            throw new InvalidOperationException("A Tegra flash plan carries at least the RCM handshake step.");
        }

        var previous = TegraFlashStage.RcmHandshake;
        var bctIndex = -1;
        var bootloaderIndex = -1;
        for (var index = 0; index < Steps.Count; index++)
        {
            var step = Steps[index];
            if (!Enum.IsDefined(step.Stage))
            {
                throw new InvalidOperationException($"Step {index} names stage {step.Stage}, which is not defined.");
            }

            if (step.Stage == TegraFlashStage.PartitionWrite)
            {
                throw new InvalidOperationException(
                    $"Step {index} names the partition-write stage, but partition writes run through the bootloader and "
                    + "never through the nv3p download chain.");
            }

            if (step.Stage < previous)
            {
                throw new InvalidOperationException(
                    $"Step {index} moves the run back from stage {previous} to {step.Stage}; a plan is monotonic.");
            }

            previous = step.Stage;
            if (step.Required && step.SourcePath is null && step.Stage is TegraFlashStage.MiniloaderDownload
                or TegraFlashStage.BctDownload or TegraFlashStage.BootloaderDownload)
            {
                throw new InvalidOperationException($"Step {index} ({step.Target}) moves a payload but names no file.");
            }

            if (step.SourcePath is null)
            {
                continue;
            }

            if (!File.Exists(step.SourcePath))
            {
                throw new InvalidOperationException($"The file '{step.SourcePath}' of step {index} does not exist.");
            }

            var length = new FileInfo(step.SourcePath).Length;
            if (length == 0)
            {
                throw new InvalidOperationException($"The file '{step.SourcePath}' of step {index} is empty.");
            }

            if (step.Bytes is { } declared && declared != length)
            {
                throw new InvalidOperationException(
                    $"Step {index} declares {declared} bytes but '{step.SourcePath}' holds {length}.");
            }

            if (step.Stage == TegraFlashStage.BctDownload)
            {
                bctIndex = index;
            }
            else if (step.Stage == TegraFlashStage.BootloaderDownload)
            {
                bootloaderIndex = index;
            }
        }

        if (bctIndex < 0 && Steps.Any(step => step.Stage == TegraFlashStage.BootloaderDownload))
        {
            throw new InvalidOperationException(
                "A plan that downloads a bootloader must download the BCT first; the bootloader cannot locate its "
                + "configuration without it.");
        }

        if (bootloaderIndex >= 0 && bctIndex > bootloaderIndex)
        {
            throw new InvalidOperationException(
                $"The BCT step {bctIndex} follows the bootloader step {bootloaderIndex}; the BCT must come first.");
        }

        for (var index = 1; index < Steps.Count; index++)
        {
            if (Steps[index].Stage == Steps[index - 1].Stage)
            {
                throw new InvalidOperationException(
                    $"Steps {index - 1} and {index} both name stage {Steps[index].Stage}; each stage appears once.");
            }
        }
    }

    private static long? Length(string? path)
        => string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : new FileInfo(path).Length;
}