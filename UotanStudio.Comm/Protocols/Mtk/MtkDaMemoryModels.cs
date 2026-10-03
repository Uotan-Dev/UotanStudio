namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Bounds, cell widths and address windows shared by the download-agent memory access paths,
/// transcribed from the reference client's peek, poke and dump dispatchers and the per-family
/// extensions they select.
/// <para>下载代理内存访问路径共用的边界、单元宽度与地址窗口，转录自参考客户端的 peek、poke 与
/// dump 分发器及其所选的各协议族扩展。</para>
/// </summary>
/// <remarks>
/// Every ceiling below is enforced before any byte leaves the host and before any receive buffer is
/// allocated. Single-frame transfers are bounded by the widest frame the family channel accepts, while
/// word-looped transfers are bounded far lower because each doubleword costs one complete command
/// exchange (Library/DA/mtk_daloader.py:322-336 and :364-372 with
/// Library/DA/legacy/extension/legacy.py:96-115 and :364,
/// Library/DA/xflash/extension/xflash.py:315-352 and :652-659,
/// Library/DA/xml/extension/v6.py:498-536).
/// <para>以下每个上限都在任何字节离开主机之前、以及分配任何接收缓冲区之前完成校验。单帧传输以该家族通道
/// 可接受的最宽帧为限，而按字循环的传输上限远小于它，因为每个双字都需要一次完整命令交互
/// （Library/DA/mtk_daloader.py:322-336 与 :364-372，配合
/// Library/DA/legacy/extension/legacy.py:96-115 与 :364、
/// Library/DA/xflash/extension/xflash.py:315-352 与 :652-659、
/// Library/DA/xml/extension/v6.py:498-536）。</para>
/// </remarks>
public static class MtkDaMemoryAccess
{
    /// <summary>The width of one doubleword register cell used by every peek and poke step.
    /// <para>每个 peek 与 poke 步骤所使用的双字寄存器单元宽度。</para></summary>
    /// <value>
    /// The constant <c>4</c>, matching the four-byte payloads of the reference register commands
    /// (Library/DA/legacy/dalegacy_param.py:31-32 with Library/DA/legacy/dalegacy_lib.py:92-108).
    /// <para>常量 <c>4</c>，与参考寄存器命令的四字节载荷一致
    /// （Library/DA/legacy/dalegacy_param.py:31-32，配合
    /// Library/DA/legacy/dalegacy_lib.py:92-108）。</para>
    /// </value>
    public const int RegisterCellBytes = 4;

    /// <summary>The largest single-frame download-agent memory read.
    /// <para>单次帧内下载代理内存读取的最大字节数。</para></summary>
    /// <value>
    /// <c>0x10000</c> bytes, below the one-mebibyte frame ceiling shared by the XFlash and XML channels.
    /// <para><c>0x10000</c> 字节，低于 XFlash 与 XML 通道共用的一 MiB 帧上限。</para>
    /// </value>
    public const int MaximumBulkReadBytes = 64 * 1024;

    /// <summary>The largest memory or register read assembled one doubleword at a time.
    /// <para>逐双字拼装的最大内存或寄存器读取字节数。</para></summary>
    /// <value>
    /// <c>0x1000</c> bytes, because the word-looped framings issue one full command exchange per
    /// doubleword (Library/DA/legacy/extension/legacy.py:107-115,
    /// Library/DA/xflash/extension/xflash.py:652-659, Library/DA/xml/extension/v6.py:516-523).
    /// <para><c>0x1000</c> 字节：按字循环的帧结构每个双字都要一次完整命令交互
    /// （Library/DA/legacy/extension/legacy.py:107-115、
    /// Library/DA/xflash/extension/xflash.py:652-659、Library/DA/xml/extension/v6.py:516-523）。</para>
    /// </value>
    public const int MaximumWordLoopReadBytes = 4 * 1024;

    /// <summary>The largest single-frame download-agent memory write.
    /// <para>单次帧内下载代理内存写入的最大字节数。</para></summary>
    /// <value>
    /// <c>0x10000</c> bytes, the same ceiling as <see cref="MaximumBulkReadBytes"/>.
    /// <para><c>0x10000</c> 字节，与 <see cref="MaximumBulkReadBytes"/> 同一上限。</para>
    /// </value>
    public const int MaximumBulkWriteBytes = 64 * 1024;

    /// <summary>The largest memory write assembled one doubleword at a time.
    /// <para>逐双字拼装的内存写入最大字节数。</para></summary>
    /// <value>
    /// <c>0x1000</c> bytes, the same ceiling as <see cref="MaximumWordLoopReadBytes"/>.
    /// <para><c>0x1000</c> 字节，与 <see cref="MaximumWordLoopReadBytes"/> 同一上限。</para>
    /// </value>
    public const int MaximumWordLoopWriteBytes = 4 * 1024;

    /// <summary>The first address of the reference boot-ROM capture window.
    /// <para>参考实现 boot-ROM 采集窗口的起始地址。</para></summary>
    /// <value>
    /// <c>0x00000000</c>, the loop start of <c>dump_brom</c> (Library/DA/mtk_daloader.py:338-354).
    /// <para><c>0x00000000</c>，即 <c>dump_brom</c> 循环的起点（Library/DA/mtk_daloader.py:338-354）。</para>
    /// </value>
    public const uint BromDumpStartAddress = 0x0000_0000;

    /// <summary>The byte length of the reference boot-ROM capture window.
    /// <para>参考实现 boot-ROM 采集窗口的字节长度。</para></summary>
    /// <value>
    /// <c>0x200000</c> bytes, the exclusive end of the reference loop.
    /// <para><c>0x200000</c> 字节，即参考循环的开区间末端。</para>
    /// </value>
    public const int BromDumpLengthBytes = 0x200000;

    /// <summary>The address step one capture command covers.
    /// <para>单条采集命令所覆盖的地址步长。</para></summary>
    /// <value>
    /// <c>0x40</c> bytes, matching the reference loop step, which is sixteen doublewords
    /// (Library/DA/mtk_daloader.py:344-352 with Library/DA/legacy/extension/legacy.py:96-105,
    /// Library/DA/xflash/extension/xflash.py:352-366 and Library/DA/xml/extension/v6.py:577-590).
    /// <para><c>0x40</c> 字节，与参考循环步长一致，即十六个双字
    /// （Library/DA/mtk_daloader.py:344-352，配合
    /// Library/DA/legacy/extension/legacy.py:96-105、
    /// Library/DA/xflash/extension/xflash.py:352-366 与
    /// Library/DA/xml/extension/v6.py:577-590）。</para>
    /// </value>
    public const int DumpStepBytes = 0x40;

    /// <summary>The largest capture range any family will walk.
    /// <para>任何家族允许遍历的最大采集范围。</para></summary>
    /// <value>
    /// <see cref="BromDumpLengthBytes"/>, because no wider window has a documented loop.
    /// <para>等于 <see cref="BromDumpLengthBytes"/>，因为更宽的窗口没有已记载的循环。</para>
    /// </value>
    public const int MaximumDumpBytes = BromDumpLengthBytes;

    /// <summary>The number of doublewords one capture step covers.
    /// <para>单个采集步长覆盖的双字数量。</para></summary>
    /// <value>
    /// Sixteen, the <c>readmem(addr, 0x10)</c> count issued per step (Library/DA/mtk_daloader.py:345).
    /// <para>十六，即每步发出的 <c>readmem(addr, 0x10)</c> 数量
    /// （Library/DA/mtk_daloader.py:345）。</para>
    /// </value>
    public const int DumpStepWords = DumpStepBytes / RegisterCellBytes;

    /// <summary>Validates a length destined for a single-frame read exchange.
    /// <para>校验用于单帧读取交换的长度。</para></summary>
    /// <param name="lengthBytes">The requested byte count. 请求的字节数。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lengthBytes"/> is not positive or
    /// exceeds <see cref="MaximumBulkReadBytes"/>.
    /// <para><paramref name="lengthBytes"/> 非正，或超过 <see cref="MaximumBulkReadBytes"/>。</para></exception>
    public static void ValidateBulkReadLength(int lengthBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lengthBytes, MaximumBulkReadBytes);
    }

    /// <summary>Validates a length destined for a read issued one doubleword at a time.
    /// <para>校验用于逐双字读取的长度。</para></summary>
    /// <param name="lengthBytes">The requested byte count. 请求的字节数。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lengthBytes"/> is not positive or
    /// exceeds <see cref="MaximumWordLoopReadBytes"/>.
    /// <para><paramref name="lengthBytes"/> 非正，或超过 <see cref="MaximumWordLoopReadBytes"/>。</para></exception>
    public static void ValidateWordLoopReadLength(int lengthBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lengthBytes, MaximumWordLoopReadBytes);
    }

    /// <summary>Validates a payload destined for a single-frame write exchange.
    /// <para>校验用于单帧写入交换的载荷。</para></summary>
    /// <param name="lengthBytes">The payload byte count. 载荷字节数。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lengthBytes"/> is not positive or
    /// exceeds <see cref="MaximumBulkWriteBytes"/>.
    /// <para><paramref name="lengthBytes"/> 非正，或超过 <see cref="MaximumBulkWriteBytes"/>。</para></exception>
    public static void ValidateBulkWriteLength(int lengthBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lengthBytes, MaximumBulkWriteBytes);
    }

    /// <summary>Validates a payload destined for a write issued one doubleword at a time.
    /// <para>校验用于逐双字写入的载荷。</para></summary>
    /// <param name="lengthBytes">The payload byte count. 载荷字节数。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lengthBytes"/> is not positive or
    /// exceeds <see cref="MaximumWordLoopWriteBytes"/>.
    /// <para><paramref name="lengthBytes"/> 非正，或超过 <see cref="MaximumWordLoopWriteBytes"/>。</para></exception>
    public static void ValidateWordLoopWriteLength(int lengthBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lengthBytes, MaximumWordLoopWriteBytes);
    }

    /// <summary>Counts the doublewords needed to cover a byte length, rounding the tail up.
    /// <para>计算覆盖某字节长度所需的双字数量，尾数向上取整。</para></summary>
    /// <param name="lengthBytes">A positive byte count. 正的字节数。</param>
    /// <returns>The number of register accesses the reference reader would issue.
    /// <para>参考读取器将发出的寄存器访问次数。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lengthBytes"/> is not positive.
    /// <para><paramref name="lengthBytes"/> 不是正数。</para></exception>
    /// <exception cref="OverflowException">The rounded word count would leave the 32-bit range.
    /// <para>取整后的字数超出 32 位范围。</para></exception>
    public static int ComputeWordCount(int lengthBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthBytes);
        return checked((lengthBytes + RegisterCellBytes - 1) / RegisterCellBytes);
    }

    /// <summary>Names the boot-ROM capture file the reference handler writes.
    /// <para>为参考处理程序写出的 boot-ROM 采集文件命名。</para></summary>
    /// <remarks>
    /// The reference builds <c>brom_{hex(hwcode)[2:]}.bin</c>, so the hardware code is rendered in
    /// lower case without padding zeros or a leading <c>0x</c>
    /// (Library/DA/mtk_da_handler.py:822-824).
    /// 参考实现构造 <c>brom_{hex(hwcode)[2:]}.bin</c>，因此硬件码以小写、无前导零填充、无 <c>0x</c>
    /// 前缀的形式呈现（Library/DA/mtk_da_handler.py:822-824）。
    /// </remarks>
    /// <param name="hardwareCode">The hardware code returned by the boot handshake.
    /// <para>boot 握手返回的硬件码。</para></param>
    /// <returns>The documented file name. 已记载的文件名。</returns>
    public static string GetBromDumpFileName(ushort hardwareCode) => $"brom_{hardwareCode:x}.bin";
}

/// <summary>
/// One bounded, step-aligned download-agent capture window.
/// <para>一个有界且按步长对齐的下载代理采集窗口。</para>
/// </summary>
/// <param name="StartAddress">The inclusive first address of the window.
/// <para>窗口的起始（含）地址。</para></param>
/// <param name="LengthBytes">The positive, step-aligned byte count.
/// <para>正数且按步长对齐的字节数。</para></param>
public readonly record struct MtkDaMemoryRange(uint StartAddress, int LengthBytes)
{
    /// <summary>The whole reference boot-ROM window, the default capture target.
    /// <para>完整的参考 boot-ROM 窗口，即默认采集目标。</para></summary>
    /// <value>
    /// The range <c>0x00000000</c> plus <c>0x200000</c> bytes used by the reference dump loop
    /// (Library/DA/mtk_daloader.py:338-354).
    /// <para>范围 <c>0x00000000</c> 加 <c>0x200000</c> 字节，即参考采集循环所用
    /// （Library/DA/mtk_daloader.py:338-354）。</para>
    /// </value>
    public static MtkDaMemoryRange BromDump => new(MtkDaMemoryAccess.BromDumpStartAddress,
        MtkDaMemoryAccess.BromDumpLengthBytes);

    /// <summary>The exclusive end address of the window.
    /// <para>窗口的结束（不含）地址。</para></summary>
    /// <value>
    /// <see cref="StartAddress"/> widened to <see cref="long"/> plus <see cref="LengthBytes"/>.
    /// <para>以 <see cref="long"/> 扩展后的 <see cref="StartAddress"/> 加上
    /// <see cref="LengthBytes"/>。</para>
    /// </value>
    public long EndAddress => (long)StartAddress + LengthBytes;

    /// <summary>Checks the bounds a capture loop needs before the first device byte is requested.
    /// <para>在请求首个设备字节之前校验采集循环所需的边界。</para></summary>
    /// <remarks>
    /// The step alignment is required because the reference loop emits exactly
    /// <see cref="MtkDaMemoryAccess.DumpStepWords"/> doublewords per command and never a short tail
    /// (Library/DA/mtk_daloader.py:344-352).
    /// 之所以要求步长对齐，是因为参考循环每条命令精确发出
    /// <see cref="MtkDaMemoryAccess.DumpStepWords"/> 个双字，从不处理较短的尾部
    /// （Library/DA/mtk_daloader.py:344-352）。
    /// </remarks>
    /// <param name="parameterName">The caller parameter to name in a thrown argument error.
    /// <para>抛出参数错误时要指名的调用方参数。</para></param>
    /// <exception cref="ArgumentOutOfRangeException">The length is not positive, exceeds
    /// <see cref="MtkDaMemoryAccess.MaximumDumpBytes"/>, or the window leaves the 32-bit address space.
    /// <para>长度非正、超过 <see cref="MtkDaMemoryAccess.MaximumDumpBytes"/>，或窗口越出 32
    /// 位地址空间。</para></exception>
    /// <exception cref="ArgumentException">The length is not a whole multiple of
    /// <see cref="MtkDaMemoryAccess.DumpStepBytes"/>.
    /// <para>长度不是 <see cref="MtkDaMemoryAccess.DumpStepBytes"/> 的整数倍。</para></exception>
    public void Validate(string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(LengthBytes);
        if (LengthBytes > MtkDaMemoryAccess.MaximumDumpBytes)
        {
            throw new ArgumentOutOfRangeException(parameterName, LengthBytes,
                $"A capture range must not exceed {MtkDaMemoryAccess.MaximumDumpBytes} bytes.");
        }

        if (LengthBytes % MtkDaMemoryAccess.DumpStepBytes != 0)
        {
            throw new ArgumentException(
                $"A capture range must be a whole multiple of {MtkDaMemoryAccess.DumpStepBytes} bytes.",
                parameterName);
        }

        if (EndAddress > 0x1_0000_0000L)
        {
            throw new ArgumentOutOfRangeException(parameterName, StartAddress,
                "The capture range leaves the 32-bit download-agent address space.");
        }
    }
}