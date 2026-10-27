using System.Collections.Frozen;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// The kind of single register access one preloader watchdog-disable rule performs.
/// <para>preloader 看门狗禁用规则中单次寄存器访问的种类。</para>
/// </summary>
public enum MtkPreloaderWatchdogOperation
{
    /// <summary>Issue one sixteen-bit write command with <see cref="MtkPreloaderWatchdogRule.Value"/>.
    /// <para>以 <see cref="MtkPreloaderWatchdogRule.Value"/> 发出一条十六位写命令。</para></summary>
    Write16,
    /// <summary>Issue one thirty-two-bit write command with <see cref="MtkPreloaderWatchdogRule.Value"/>.
    /// <para>以 <see cref="MtkPreloaderWatchdogRule.Value"/> 发出一条三十二位写命令。</para></summary>
    Write32,
    /// <summary>Read one doubleword at <see cref="MtkPreloaderWatchdogRule.Address"/>, or it with
    /// <see cref="MtkPreloaderWatchdogRule.Value"/>, and write the result back.
    /// <para>在 <see cref="MtkPreloaderWatchdogRule.Address"/> 读取一个双字，与
    /// <see cref="MtkPreloaderWatchdogRule.Value"/> 按位或后写回。</para></summary>
    ReadOrWrite32
}

/// <summary>
/// One ordered register step of a preloader watchdog-disable sequence.
/// <para>preloader 看门狗禁用序列中的一个有序寄存器步骤。</para>
/// </summary>
/// <param name="Operation">The access kind the step performs. 该步骤执行的访问种类。</param>
/// <param name="Address">The register address the step reads or writes. 该步骤读或写的寄存器地址。</param>
/// <param name="Value">The word written, or the OR mask for a read-modify-write step.
/// <para>写入的字，或读-改-写步骤的按位或掩码。</para></param>
public sealed record MtkPreloaderWatchdogRule(
    MtkPreloaderWatchdogOperation Operation,
    uint Address,
    uint Value);

/// <summary>
/// Watchdog-disable branches and watchdog-reset (<c>misc_lock</c>) registers for the preloader stage;
/// the class performs no I/O.
/// <para>preloader 阶段的看门狗禁用分支与看门狗复位（<c>misc_lock</c>）寄存器表；
/// 本类不执行任何 I/O。</para>
/// </summary>
/// <remarks>
/// The sequences are fixed for hardware codes
/// 0x2625/0x2523/0x7682/0x7686/0x5932 and for the MT6261 branch (with the hardware-version-dependent
/// MT2503 sub-branch); every other chip combines the global watchdog address with its per-chip
/// <c>watchdog=</c> base. The MT6261 RTC/PMU long path and the MT6575/MT6577 GPIO extras are not
/// modelled, and chips without a recorded base return an empty rule list so the session can
/// refuse instead of inventing a register sequence.
/// <para>硬件码 0x2625/0x2523/0x7682/0x7686/0x5932 以及 MT6261 分支（含按硬件版本区分的 MT2503 子分支）
/// 使用固定序列；其它芯片由全局看门狗地址与每芯片的 <c>watchdog=</c> 基址组合而成。
/// MT6261 RTC/PMU 长路径与 MT6575/MT6577 GPIO 额外步骤不在此建模，没有记录基址的芯片返回空规则列表，
/// 让会话可以拒绝而不是凭空编造寄存器序列。</para>
/// </remarks>
public static class MtkPreloaderWatchdogTable
{
    private static readonly FrozenSet<ushort> SixtyFourBitHardwareCodes = new HashSet<ushort>
    {
        0x0907, 0x1229, 0x1236, 0x1296, 0x1375, 0x6899, 0x1471
    }.ToFrozenSet();

    private static readonly FrozenSet<ushort> FixedAddressCodes = new HashSet<ushort>
    {
        0x2625, 0x2523, 0x7682, 0x7686, 0x5932
    }.ToFrozenSet();

    private static readonly FrozenDictionary<ushort, uint> WatchdogBases = new Dictionary<ushort, uint>
    {
        [0x0571] = 0x10007000,
        [0x0598] = 0x10211000,
        [0x0992] = 0x10007000,
        [0x2601] = 0x10007000,
        [0x0633] = 0x10007000,
        [0x6516] = 0x10003000,
        [0x6571] = 0x10007400,
        [0x6572] = 0x10007000,
        [0x6573] = 0x70025000,
        [0x6580] = 0x10007000,
        [0x6582] = 0x10007000,
        [0x6583] = 0x10000000,
        [0x6592] = 0x10007000,
        [0x6595] = 0x10007000,
        [0x0321] = 0x10212000,
        [0x0326] = 0x10007000,
        [0x0335] = 0x10212000,
        [0x0337] = 0x10212000,
        [0x0507] = 0x10210000,
        [0x0551] = 0x10007000,
        [0x0562] = 0x10211000,
        [0x0601] = 0x10007000,
        [0x0688] = 0x10211000,
        [0x0690] = 0x10007000,
        [0x0699] = 0x10007000,
        [0x0707] = 0x10007000,
        [0x0717] = 0x10007000,
        [0x0725] = 0x10007000,
        [0x0766] = 0x10007000,
        [0x0788] = 0x10007000,
        [0x0813] = 0x10007000,
        [0x0816] = 0x10007000,
        [0x0886] = 0x10007000,
        [0x0907] = 0x1C007000,
        [0x0908] = 0x10007000,
        [0x0930] = 0x10007000,
        [0x0950] = 0x10007000,
        [0x0959] = 0x10007000,
        [0x0989] = 0x10007000,
        [0x0996] = 0x10007000,
        [0x1066] = 0x10007000,
        [0x1129] = 0x1C007000,
        [0x1172] = 0x1C007000,
        [0x1203] = 0x1C007000,
        [0x1208] = 0x10007000,
        [0x1209] = 0x1C007000,
        [0x1229] = 0x1C007000,
        [0x1236] = 0x1C00B000,
        [0x1296] = 0x1C007000,
        [0x1357] = 0x1C010000,
        [0x1375] = 0x1C00A000,
        [0x1471] = 0x1C010000,
        [0x0279] = 0x10007000,
        [0x6752] = 0x10007000,
        [0x6795] = 0x10007000,
        [0x6899] = 0x1C00B000,
        [0x8127] = 0x10007000,
        [0x8135] = 0x10000000,
        [0x8163] = 0x10007000,
        [0x8167] = 0x10007000,
        [0x8168] = 0x10007000,
        [0x8172] = 0x10007000,
        [0x8176] = 0x10007000,
        [0x8512] = 0x10007000,
        [0x8590] = 0x10007000,
        [0x8695] = 0x10007000
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<ushort, uint> MiscLockRegisters = new Dictionary<ushort, uint>
    {
        [0x6571] = 0x1000141C,
        [0x6572] = 0x1000141C,
        [0x6580] = 0x10001838,
        [0x6582] = 0x10002050,
        [0x6583] = 0x10002050,
        [0x6592] = 0x10002050,
        [0x0279] = 0x10002050,
        [0x0321] = 0x10001838,
        [0x0326] = 0x10001838,
        [0x0337] = 0x10001838,
        [0x0551] = 0x10001838,
        [0x0601] = 0x10001838,
        [0x0690] = 0x1001A100,
        [0x0699] = 0x1001A100,
        [0x0707] = 0x1001A100,
        [0x0717] = 0x1001A100,
        [0x0725] = 0x1001A100,
        [0x0766] = 0x1001A100,
        [0x0788] = 0x1001A100,
        [0x0813] = 0x1001A100,
        [0x0816] = 0x1001A100,
        [0x0886] = 0x1001A100,
        [0x0930] = 0x1001A100,
        [0x0996] = 0x1001A100,
        [0x6752] = 0x10001838,
        [0x8127] = 0x10002050,
        [0x8163] = 0x10002050,
        [0x8172] = 0x01202050,
        [0x8176] = 0x01202050
    }.ToFrozenDictionary();

    /// <summary>Reports whether the chip table marks a hardware code as 64-bit.
    /// <para>报告芯片表是否将某个硬件码标记为 64 位。</para></summary>
    /// <remarks>The preloader initialization skips the watchdog entirely for chips whose
    /// <c>Chipconfig.has64bit</c> is true, so this set lists those hardware codes rather than inferring
    /// 64-bit support from the storage family.
    /// <para>preloader 初始化对 <c>Chipconfig.has64bit</c> 为 true 的芯片完全跳过看门狗，因此本集合列出
    /// 这些硬件码，而不是根据存储家族推断 64 位支持。</para>
    /// </remarks>
    /// <param name="hardwareCode">The hardware code reported by command FD. 命令 FD 报告的硬件码。</param>
    /// <returns><see langword="true"/> for a hardware code the table marks as 64-bit.
    /// <para>芯片表标记为 64 位的硬件码返回 <see langword="true"/>。</para></returns>
    public static bool Is64BitHardware(ushort hardwareCode) => SixtyFourBitHardwareCodes.Contains(hardwareCode);

    /// <summary>Resolves the ordered watchdog-disable steps defined for one chip.
    /// <para>为单芯片解析已定义的有序看门狗禁用步骤。</para></summary>
    /// <remarks>Hardware codes 0x2625, 0x2523, 0x7682, 0x7686 and 0x5932 issue the single
    /// <c>write16(0xA2050000, 0x2200)</c>; MT6261 with hardware version 0xCA02 or 0xCB01 walks the
    /// MT2503 sub-branch (RGU clear, the two CTIRQ1/2 avoids, and the <c>EMI_REMAP |= 2</c>
    /// read-modify-write), while other MT6261 revisions keep only the RGU clear; MT6575/MT6577 use
    /// sixteen-bit I/O; MT6592 gains the additional <c>0x10000500</c> register. Every other recorded chip
    /// falls through to the thirty-two-bit default using the per-chip base and the value table of
    /// <c>MtkConfig.get_watchdog_addr</c>.
    /// <para>硬件码 0x2625、0x2523、0x7682、0x7686 与 0x5932 发出单一的
    /// <c>write16(0xA2050000, 0x2200)</c>；硬件版本为 0xCA02 或 0xCB01 的 MT6261 走 MT2503 子分支
    /// （RGU 清零、两条 CTIRQ1/2 规避，以及 <c>EMI_REMAP |= 2</c> 的读-改-写），其它 MT6261 修订只保留
    /// RGU 清零；MT6575/MT6577 使用十六位 I/O；MT6592 追加 <c>0x10000500</c> 寄存器。
    /// 其余已记录芯片走三十二位默认分支，使用每芯片基址与 <c>MtkConfig.get_watchdog_addr</c> 的取值表。</para>
    /// </remarks>
    /// <param name="hardwareCode">The hardware code reported by command FD. 命令 FD 报告的硬件码。</param>
    /// <param name="hardwareVersion">The hardware revision reported by the boot identity query.
    /// <para>boot 身份查询报告的硬件修订。</para></param>
    /// <returns>The steps in send order, or an empty list when the table records no sequence for the chip.
    /// <para>按发送顺序排列的步骤；芯片表没有记录该芯片序列时为空列表。</para></returns>
    public static IReadOnlyList<MtkPreloaderWatchdogRule> Resolve(ushort hardwareCode, uint hardwareVersion)
    {
        if (FixedAddressCodes.Contains(hardwareCode))
        {
            return [new MtkPreloaderWatchdogRule(MtkPreloaderWatchdogOperation.Write16, 0xA205_0000, 0x2200)];
        }

        if (hardwareCode == 0x6261)
        {
            return hardwareVersion is 0xCA02 or 0xCB01
                ?
                [
                    new MtkPreloaderWatchdogRule(MtkPreloaderWatchdogOperation.Write16, 0xA003_0000, 0x2200),
                    new MtkPreloaderWatchdogRule(MtkPreloaderWatchdogOperation.Write16, 0x8307_0008, 0xABCD),
                    new MtkPreloaderWatchdogRule(MtkPreloaderWatchdogOperation.Write16, 0x8307_0010, 0x0003),
                    new MtkPreloaderWatchdogRule(MtkPreloaderWatchdogOperation.ReadOrWrite32, 0xA051_0000, 0x2)
                ]
                : [new MtkPreloaderWatchdogRule(MtkPreloaderWatchdogOperation.Write16, 0xA003_0000, 0x2200)];
        }

        if (hardwareCode is 0x6575 or 0x6577)
        {
            return
            [
                new MtkPreloaderWatchdogRule(MtkPreloaderWatchdogOperation.Write16, 0xC000_0000,
                    WatchdogValue(0xC000_0000))
            ];
        }

        if (!WatchdogBases.TryGetValue(hardwareCode, out var baseAddress))
        {
            return [];
        }

        var rules = new List<MtkPreloaderWatchdogRule>
        {
            new(MtkPreloaderWatchdogOperation.Write32, baseAddress, WatchdogValue(baseAddress))
        };
        if (hardwareCode == 0x6592)
        {
            rules.Add(new MtkPreloaderWatchdogRule(MtkPreloaderWatchdogOperation.Write32, 0x1000_0500,
                0x2200_0000));
        }

        return rules;
    }

    /// <summary>Resolves the watchdog-reset <c>misc_lock</c> register for one chip.
    /// <para>解析单芯片的看门狗复位 <c>misc_lock</c> 寄存器。</para></summary>
    /// <remarks>Every other address of the unlock sequence derives from <c>chipconfig.misc_lock</c>; the
    /// values here are the <c>misc_lock=</c> entries of the chip table. A chip without the field returns
    /// <see langword="false"/> so the caller can refuse rather than guess an address.
    /// <para>解锁序列的其它地址均由 <c>chipconfig.misc_lock</c> 推导；这里的取值来自芯片表中的
    /// <c>misc_lock=</c> 条目。未提供该字段的芯片返回 <see langword="false"/>，让调用方可以拒绝而不是猜测地址。</para></remarks>
    /// <param name="hardwareCode">The hardware code reported by command FD. 命令 FD 报告的硬件码。</param>
    /// <param name="miscLockAddress">The recorded register base, or <c>0</c> when unknown.
    /// <para>记录的寄存器基址，未知时为 <c>0</c>。</para></param>
    /// <returns><see langword="true"/> when the chip table records the register.
    /// <para>芯片表记录了该寄存器时返回 <see langword="true"/>。</para></returns>
    public static bool TryResolveResetRegisters(ushort hardwareCode, out uint miscLockAddress) =>
        MiscLockRegisters.TryGetValue(hardwareCode, out miscLockAddress);

    private static uint WatchdogValue(uint baseAddress) => baseAddress switch
    {
        0x1000_7000 => 0x2200_0064,
        0x1000_7400 => 0x2200_0000,
        0x1021_2000 => 0x2200_0000,
        0x1021_1000 => 0x2200_0064,
        0xC000_0000 => 0x2264,
        0xA003_0000 => 0x2200,
        0x1C00_A000 => 0x2200_0064,
        _ => 0x2200_0064
    };
}