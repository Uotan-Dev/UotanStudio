using UotanStudio.Comm.Protocols.Fastboot;

namespace UotanStudio.Comm.Protocols.SpacemiT;

/// <summary>
/// Establishes the <see cref="SpacemiTBootStage"/> of a fastboot gadget from its USB identity or from a
/// live <c>getvar</c> probe.
/// <para>依据 USB 身份或实时 <c>getvar</c> 探测，确定 fastboot gadget 的 <see cref="SpacemiTBootStage"/>。</para>
/// </summary>
/// <remarks>
/// The USB descriptors do not separate the two stages: both expose the vid/pid pair and the fastboot interface
/// triple <c>0xFF/0x42/0x03</c>, so the vendor's own flasher asks <c>getvar:version-brom</c> instead. That probe is
/// the authoritative answer and is what <see cref="Probe"/> reproduces. The descriptor serial is the only
/// enumeration-time hint: the BootROM reports the fixed <see cref="FdlSerial"/> marker while U-Boot reports the
/// board serial, which is why <see cref="FromIdentity"/> can label an un-opened device without claiming the
/// interface — it never reads the wire, so it stays a hint and the probe overrides it.
/// <para>USB 描述符无法区分这两个阶段：两者都暴露同一 vid/pid 对与 fastboot 接口三元组 <c>0xFF/0x42/0x03</c>，
/// 因此厂商烧录器改用 <c>getvar:version-brom</c> 询问。该探测是权威答案，也是 <see cref="Probe"/> 所复现的路径。
/// 描述符序列号是枚举阶段唯一的线索：BootROM 上报固定的 <see cref="FdlSerial"/> 标记，而 U-Boot 上报板卡序列号，
/// 因此 <see cref="FromIdentity"/> 能在不认领接口的前提下标注尚未打开的设备——它从不读写线路，所以只是线索，
/// 由探测结果覆盖。</para>
/// </remarks>
public static class SpacemiTBootStageClassifier
{
    /// <summary>
    /// Serial the BootROM download mode reports; U-Boot replaces it with the board serial.
    /// <para>BootROM 下载模式上报的序列号；U-Boot 会把它替换为板卡序列号。</para>
    /// </summary>
    public const string FdlSerial = "DFU-DEVICE";

    /// <summary>
    /// Classifies a device from its USB identity and reported serial without touching the wire.
    /// <para>依据 USB 身份与上报的序列号分类设备，不读写线路。</para>
    /// </summary>
    /// <param name="vendorId">
    /// USB vendor id of the enumerated device.
    /// <para>已枚举设备的 USB 厂商 ID。</para>
    /// </param>
    /// <param name="productId">
    /// USB product id of the enumerated device.
    /// <para>已枚举设备的 USB 产品 ID。</para>
    /// </param>
    /// <param name="serial">
    /// Serial reported by the descriptor or by a discovery-time probe, or <see langword="null"/> when unknown.
    /// <para>描述符或发现阶段探测上报的序列号；未知时为 <see langword="null"/>。</para>
    /// </param>
    /// <returns>
    /// <see cref="SpacemiTBootStage.BromFdl"/> for the <see cref="FdlSerial"/> marker,
    /// <see cref="SpacemiTBootStage.Uboot"/> for the vendor pair with any other usable serial, and
    /// <see cref="SpacemiTBootStage.Unknown"/> for anything else.
    /// <para><see cref="FdlSerial"/> 标记返回 <see cref="SpacemiTBootStage.BromFdl"/>；厂商 ID 对配合其他可用
    /// 序列号返回 <see cref="SpacemiTBootStage.Uboot"/>；其余返回 <see cref="SpacemiTBootStage.Unknown"/>。</para>
    /// </returns>
    public static SpacemiTBootStage FromIdentity(ushort vendorId, ushort productId, string? serial)
    {
        if (vendorId != SpacemiTConstants.UsbVendorId || productId != SpacemiTConstants.UsbProductId)
        {
            return SpacemiTBootStage.Unknown;
        }

        if (!IsUsableSerial(serial))
        {
            return SpacemiTBootStage.Unknown;
        }

        return string.Equals(serial!.Trim(), FdlSerial, StringComparison.OrdinalIgnoreCase)
            ? SpacemiTBootStage.BromFdl
            : SpacemiTBootStage.Uboot;
    }

    /// <summary>
    /// Asks the device itself which stage it runs: <c>getvar:version-brom</c> only answers in the BootROM
    /// download mode, which is the same discriminator the vendor flasher's <c>skip_when</c> guard relies on.
    /// <para>直接询问设备所处阶段：<c>getvar:version-brom</c> 只在 BootROM 下载模式下应答，这正是厂商烧录器
    /// <c>skip_when</c> 判据所依赖的区分点。</para>
    /// </summary>
    /// <param name="session">
    /// The leased fastboot session; it must not be in the middle of a transfer.
    /// <para>已租用的 fastboot 会话；不得处于传输过程之中。</para>
    /// </param>
    /// <returns>
    /// <see cref="SpacemiTBootStage.BromFdl"/> when <c>version-brom</c> answers, <see cref="SpacemiTBootStage.Uboot"/>
    /// when only the U-Boot variables answer, and <see cref="SpacemiTBootStage.Unknown"/> when neither does or the
    /// probe failed.
    /// <para><c>version-brom</c> 有应答时为 <see cref="SpacemiTBootStage.BromFdl"/>；只有 U-Boot 变量有应答时为
    /// <see cref="SpacemiTBootStage.Uboot"/>；两者都无应答或探测失败时为 <see cref="SpacemiTBootStage.Unknown"/>。</para>
    /// </returns>
    /// <remarks>
    /// Failures are folded into <see cref="SpacemiTBootStage.Unknown"/> on purpose: the stage only selects
    /// diagnostics, so an unreadable device must not abort the run that the package script drives.
    /// <para>失败被折叠为 <see cref="SpacemiTBootStage.Unknown"/> 是有意为之：该阶段只影响诊断，不可读的设备不应
    /// 中断由包脚本驱动的运行。</para>
    /// </remarks>
    public static SpacemiTBootStage Probe(FastbootSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            var brom = session.GetVarWithResult(SpacemiTConstants.VarBromVersion, useCache: false);
            if (brom.Success && !string.IsNullOrWhiteSpace(brom.Value))
            {
                return SpacemiTBootStage.BromFdl;
            }

            return session.GetVarWithResult("version", useCache: false).Success
                || session.GetVarWithResult("product", useCache: false).Success
                ? SpacemiTBootStage.Uboot
                : SpacemiTBootStage.Unknown;
        }
        catch (Exception exception) when (exception is FastbootException or IOException or TimeoutException
            or InvalidOperationException or NotSupportedException)
        {
            return SpacemiTBootStage.Unknown;
        }
    }

    /// <summary>
    /// Whether a reported serial can carry stage evidence: the BootROM marker or a board serial, never a blank or
    /// placeholder value such as the unknown marker some backends emit.
    /// <para>上报的序列号能否承载阶段证据：即 BootROM 标记或板卡序列号，不接受空白或某些后端发出的未知占位值。</para>
    /// </summary>
    /// <param name="serial">The reported serial, or <see langword="null"/>. <para>上报的序列号，或 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when the serial is a usable identifier.
    /// <para>序列号是可用标识时为 <see langword="true"/>。</para></returns>
    private static bool IsUsableSerial(string? serial)
    {
        if (serial is null)
        {
            return false;
        }

        var trimmed = serial.Trim();
        return trimmed.Length is > 0 and <= 128 &&
            !string.Equals(trimmed, "UNKNOWN", StringComparison.OrdinalIgnoreCase) &&
            trimmed.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
    }
}