namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// What the device does after the download chain completes.
/// <para>下载链完成后设备的行为。</para>
/// </summary>
public enum TegraCompletionMode
{
    /// <summary>
    /// Stop after the last transfer and leave the device holding the image it was given; the host keeps the session
    /// open for a follow-up step. <para>最后一次传输后停止，让设备保留收到的镜像；主机保持会话打开以便后续步骤。</para>
    /// </summary>
    StayInRcm = 0,

    /// <summary>
    /// Let the device start the bootloader that was just downloaded, which is what the reference flasher does at the
    /// end of the chain. <para>让设备启动刚下载的 bootloader，这也是参考烧录器在下载链末尾的行为。</para>
    /// </summary>
    Reboot = 1,
}

/// <summary>
/// Options for one Tegra flash run over RCM and nv3p. The chain downloads a miniloader, a BCT and a bootloader, so
/// every run must carry the exact confirmation phrase before any byte goes on the wire; rewriting the boot
/// configuration table additionally needs the wipe phrase.
/// <para>一次经 RCM 与 nv3p 的 Tegra 烧录运行选项。该下载链会写入 miniloader、BCT 与 bootloader，因此每次运行都必须
/// 在任何字节上线之前携带精确的确认短语；改写引导配置表还需要擦除短语。</para>
/// </summary>
public sealed class TegraFlashOptions
{
    /// <summary>
    /// Confirmation phrase every run must present in <see cref="FlashConfirmation"/>; execution fails before any wire
    /// I/O otherwise. <para>每次运行必须在 <see cref="FlashConfirmation"/> 中提供的确认短语；否则在任何线上 I/O 之前
    /// 即失败。</para>
    /// </summary>
    public const string RequiredConfirmation = "FLASH";

    /// <summary>
    /// Phrase <see cref="WipeConfirmation"/> must carry when <see cref="RewritesBootConfiguration"/> is set; a run that
    /// asks to rewrite the boot configuration table fails before any wire I/O without it.
    /// <para><see cref="RewritesBootConfiguration"/> 为真时 <see cref="WipeConfirmation"/> 必须提供的短语；缺少它的运行
    /// 会在任何线上 I/O 之前失败。</para>
    /// </summary>
    public const string RequiredWipeConfirmation = "WIPE";

    /// <summary>Gets or sets the confirmation phrase; must equal <see cref="RequiredConfirmation"/>.
    /// <para>获取或设置确认短语；必须等于 <see cref="RequiredConfirmation"/>。</para></summary>
    public string FlashConfirmation { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the wipe phrase, required only when <see cref="RewritesBootConfiguration"/> is set; it must then
    /// equal <see cref="RequiredWipeConfirmation"/>. <para>获取或设置擦除短语，仅在
    /// <see cref="RewritesBootConfiguration"/> 为真时必需；此时必须等于
    /// <see cref="RequiredWipeConfirmation"/>。</para>
    /// </summary>
    public string? WipeConfirmation { get; set; }

    /// <summary>
    /// Gets or sets whether the run rewrites the boot configuration table, which redefines where every partition
    /// lives and therefore discards the storage layout the device holds now. It is off by default and gates the wipe
    /// phrase. <para>获取或设置本次运行是否改写引导配置表；该表重新定义每个分区的位置，因此会丢弃设备当前持有的存储
    /// 布局。默认关闭，并由它约束擦除短语。</para>
    /// </summary>
    public bool RewritesBootConfiguration { get; set; }

    /// <summary>Gets or sets the chip generation the run targets; <see cref="TegraChip.Unknown"/> is rejected.
    /// <para>获取或设置本次运行的目标芯片代际；<see cref="TegraChip.Unknown"/> 会被拒绝。</para></summary>
    public TegraChip Chip { get; set; } = TegraChip.Unknown;

    /// <summary>Gets or sets the path of the miniloader binary sent over RCM.
    /// <para>获取或设置经 RCM 发送的 miniloader 二进制路径。</para></summary>
    public string? MiniloaderPath { get; set; }

    /// <summary>Gets or sets the path of the boot configuration table sent over nv3p.
    /// <para>获取或设置经 nv3p 发送的引导配置表路径。</para></summary>
    public string? BctPath { get; set; }

    /// <summary>Gets or sets the path of the bootloader binary sent over nv3p.
    /// <para>获取或设置经 nv3p 发送的 bootloader 二进制路径。</para></summary>
    public string? BootloaderBinaryPath { get; set; }

    /// <summary>
    /// Gets or sets the address the device loads the bootloader at, which <c>NV3P_CMD_DL_BL</c> carries as its
    /// <c>address</c> field. It has no safe default — the value comes from the target board — so it must be set
    /// whenever <see cref="BootloaderBinaryPath"/> is. <para>获取或设置设备加载 bootloader 的地址，
    /// <c>NV3P_CMD_DL_BL</c> 把它作为 <c>address</c> 字段携带。该值来自目标板，没有安全默认值，因此只要设置了
    /// <see cref="BootloaderBinaryPath"/> 就必须设置它。</para>
    /// </summary>
    public uint? BootloaderLoadAddress { get; set; }

    /// <summary>
    /// Gets or sets the bootloader entry point <c>NV3P_CMD_DL_BL</c> carries as its <c>entry</c> field. It defaults to
    /// <see cref="BootloaderLoadAddress"/>, exactly as the reference flasher does when no entry address is given.
    /// <para>获取或设置 <c>NV3P_CMD_DL_BL</c> 作为 <c>entry</c> 字段携带的 bootloader 入口点。未指定时默认取
    /// <see cref="BootloaderLoadAddress"/>，与参考烧录器在未给出入口地址时的行为一致。</para>
    /// </summary>
    public uint? BootloaderEntryAddress { get; set; }

    /// <summary>
    /// Gets or sets whether the run asks the device to describe its stored BCT again after the download and reports
    /// any difference in length. The bootloader has no read-back command in the nv3p command set this layer speaks,
    /// so it is never read back. <para>获取或设置本次运行是否在下载后再次要求设备描述其存储的 BCT 并上报长度差异。本层所讲
    /// 的 nv3p 命令集中没有 bootloader 回读命令，因此它从不被回读。</para>
    /// </summary>
    public bool VerifyAfter { get; set; } = true;

    /// <summary>Gets or sets what the device does once the chain completes.
    /// <para>获取或设置下载链完成后设备的行为。</para></summary>
    public TegraCompletionMode CompletionMode { get; set; } = TegraCompletionMode.Reboot;

    /// <summary>
    /// Validates the options before any device I/O; a rejected run never touches the wire.
    /// <para>在任何设备 I/O 之前校验选项；被拒绝的运行绝不触线。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The confirmation phrase is missing or wrong, the chip generation is
    /// undefined, the miniloader or the BCT is not configured or does not exist, the wipe phrase is missing while
    /// <see cref="RewritesBootConfiguration"/> is set, or the completion mode is not a defined value.
    /// <para>确认短语缺失或错误、芯片代际未定义、miniloader 或 BCT 未配置或不存在、
    /// <see cref="RewritesBootConfiguration"/> 为真时缺少擦除短语，或完成模式不是已定义取值。</para></exception>
    internal void Validate()
    {
        if (!string.Equals(FlashConfirmation, RequiredConfirmation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Tegra flash requires FlashConfirmation to be exactly '{RequiredConfirmation}'.");
        }

        if (!TegraChips.IsKnown(Chip))
        {
            throw new InvalidOperationException(
                $"Chip {Chip} is not a defined Tegra generation; resolve it from the RCM product ID first.");
        }

        if (!Enum.IsDefined(CompletionMode))
        {
            throw new InvalidOperationException($"CompletionMode {CompletionMode} is not a defined completion mode.");
        }

        if (RewritesBootConfiguration
            && !string.Equals(WipeConfirmation, RequiredWipeConfirmation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Rewriting the boot configuration table discards the storage layout, so it requires "
                + $"WipeConfirmation to be exactly '{RequiredWipeConfirmation}'.");
        }

        RequireExistingFile(MiniloaderPath, nameof(MiniloaderPath), "miniloader");
        RequireExistingFile(BctPath, nameof(BctPath), "BCT");

        if (CompletionMode == TegraCompletionMode.Reboot && string.IsNullOrWhiteSpace(BootloaderBinaryPath))
        {
            throw new InvalidOperationException(
                "CompletionMode Reboot hands control to the downloaded bootloader, so BootloaderBinaryPath must be set.");
        }

        if (string.IsNullOrWhiteSpace(BootloaderBinaryPath))
        {
            return;
        }

        if (BootloaderLoadAddress is not { } address || address == 0)
        {
            throw new InvalidOperationException(
                "A bootloader download needs BootloaderLoadAddress because DL_BL carries it; the target board "
                + "determines it and this layer invents no default.");
        }

        if (BootloaderEntryAddress is { } entry && entry == 0)
        {
            throw new InvalidOperationException(
                "BootloaderEntryAddress is either unset, so it defaults to BootloaderLoadAddress, or a non-zero "
                + "entry point.");
        }
    }

    /// <summary>
    /// Gets the entry point the device starts the bootloader at: <see cref="BootloaderEntryAddress"/> when the caller
    /// set one, otherwise <see cref="BootloaderLoadAddress"/>. <para>获取设备启动 bootloader 的入口点：调用方设置了
    /// <see cref="BootloaderEntryAddress"/> 时取该值，否则取 <see cref="BootloaderLoadAddress"/>。</para>
    /// </summary>
    internal uint EffectiveBootloaderEntryAddress
        => BootloaderEntryAddress ?? BootloaderLoadAddress ?? 0;

    /// <summary>
    /// Rejects an unset, missing or empty file option before any wire byte moves.
    /// <para>在任何线上字节移动之前拒绝未设置、不存在或为空的文件选项。</para>
    /// </summary>
    private static void RequireExistingFile(string? path, string parameterName, string what)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"A Tegra flash run requires {parameterName}, but no {what} path is set.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"The {what} file '{path}' does not exist.");
        }

        if (new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException($"The {what} file '{path}' is empty.");
        }
    }
}