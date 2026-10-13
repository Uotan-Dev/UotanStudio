using UotanStudio.Comm.Protocols.Mtk.Security;

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
/// <para>The reference walks a capture window in two distinct shapes, and the ceilings below are keyed to
/// them. <c>dump_brom</c> steps <c>0x0</c> through <c>0x200000</c> in <c>0x40</c> increments, issuing one
/// <c>readmem(addr, 0x10)</c> per step (Library/DA/mtk_daloader.py:338-356), so the register-step walk is
/// capped at <see cref="MaximumDumpBytes"/>. <c>da memdump</c> instead hands a whole region to
/// <c>da_peek</c>, which pages the bulk read primitive at the negotiated packet length
/// (Library/DA/mtk_da_handler.py:784-813 with :597-624), so the paged walk is capped at
/// <see cref="MaximumRegionCaptureBytes"/>, the widest span that command names for any region
/// (Library/DA/mtk_da_handler.py:792-793).</para>
/// <para>参考实现以两种不同形态遍历采集窗口，下列上限与它们一一对应。<c>dump_brom</c> 从 <c>0x0</c> 走到
/// <c>0x200000</c>，步长 <c>0x40</c>，每步发出一次 <c>readmem(addr, 0x10)</c>
/// （Library/DA/mtk_daloader.py:338-356），因此寄存器步进遍历以 <see cref="MaximumDumpBytes"/> 为上限。
/// 而 <c>da memdump</c> 把整块区域交给 <c>da_peek</c>，后者按协商的分组长度分页调用批量读原语
/// （Library/DA/mtk_da_handler.py:784-813，配合 :597-624），因此分页遍历的上限取该命令为任何区域给出的最宽
/// 跨度 <see cref="MaximumRegionCaptureBytes"/>（Library/DA/mtk_da_handler.py:792-793）。</para>
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
    /// <c>0x00000000</c>, the loop start of <c>dump_brom</c> (Library/DA/mtk_daloader.py:338-356).
    /// <para><c>0x00000000</c>，即 <c>dump_brom</c> 循环的起点（Library/DA/mtk_daloader.py:338-356）。</para>
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
    /// (Library/DA/mtk_daloader.py:351-352 with Library/DA/legacy/extension/legacy.py:96-105,
    /// Library/DA/xflash/extension/xflash.py:353-370 and Library/DA/xml/extension/v6.py:577-593).
    /// <para><c>0x40</c> 字节，与参考循环步长一致，即十六个双字
    /// （Library/DA/mtk_daloader.py:351-352，配合
    /// Library/DA/legacy/extension/legacy.py:96-105、
    /// Library/DA/xflash/extension/xflash.py:353-370 与
    /// Library/DA/xml/extension/v6.py:577-593）。</para>
    /// </value>
    public const int DumpStepBytes = 0x40;

    /// <summary>The largest window the register-step capture walk may cover.
    /// <para>寄存器步进采集遍历允许覆盖的最大窗口。</para></summary>
    /// <value>
    /// <see cref="BromDumpLengthBytes"/>: that walk exists only because the reference <c>dump_brom</c> loop
    /// is exactly that wide, and every doubleword of a wider window would cost one full command exchange, so
    /// wider regions use the paged walk bounded by <see cref="MaximumRegionCaptureBytes"/> instead
    /// (Library/DA/mtk_daloader.py:338-356 with Library/DA/mtk_da_handler.py:784-813).
    /// <para>等于 <see cref="BromDumpLengthBytes"/>：该遍历之所以存在，只是因为参考的 <c>dump_brom</c> 循环
    /// 恰好就是这个宽度，而更宽窗口的每个双字都要一次完整命令交互，因此更宽的区域改用受
    /// <see cref="MaximumRegionCaptureBytes"/> 约束的分页遍历
    /// （Library/DA/mtk_daloader.py:338-356，配合 Library/DA/mtk_da_handler.py:784-813）。</para>
    /// </value>
    public const int MaximumDumpBytes = BromDumpLengthBytes;

    /// <summary>The number of doublewords one capture step covers.
    /// <para>单个采集步长覆盖的双字数量。</para></summary>
    /// <value>
    /// Sixteen, the <c>readmem(addr, 0x10)</c> count issued per step (Library/DA/mtk_daloader.py:352).
    /// <para>十六，即每步发出的 <c>readmem(addr, 0x10)</c> 数量
    /// （Library/DA/mtk_daloader.py:352）。</para>
    /// </value>
    public const int DumpStepWords = DumpStepBytes / RegisterCellBytes;

    /// <summary>The first address of the reference static-RAM capture window.
    /// <para>参考实现静态 RAM 采集窗口的起始地址。</para></summary>
    /// <value>
    /// <c>0x200000</c>, the <c>sramaddr</c> default of <c>da memdump</c>
    /// (Library/DA/mtk_da_handler.py:792).
    /// <para><c>0x200000</c>，即 <c>da memdump</c> 的 <c>sramaddr</c> 默认值
    /// （Library/DA/mtk_da_handler.py:792）。</para>
    /// </value>
    public const uint SramDumpStartAddress = 0x20_0000;

    /// <summary>The byte length the reference assigns to that static-RAM window when the agent reports none.
    /// <para>代理未上报时，参考实现为该静态 RAM 窗口指定的字节长度。</para></summary>
    /// <value>
    /// <c>0x11200000</c> bytes, the <c>sramsize</c> default
    /// (Library/DA/mtk_da_handler.py:793), which the reference overrides with the RAM-info reply whenever
    /// the family supplies one (Library/DA/mtk_da_handler.py:799-801 with
    /// Library/DA/xflash/xflash_lib.py:473-492 and :1073).
    /// <para><c>0x11200000</c> 字节，即 <c>sramsize</c> 默认值（Library/DA/mtk_da_handler.py:793）；
    /// 只要家族会给出内存信息回复，参考就用它覆盖该默认值
    /// （Library/DA/mtk_da_handler.py:799-801，配合
    /// Library/DA/xflash/xflash_lib.py:473-492 与 :1073）。</para>
    /// </value>
    public const int SramDumpLengthBytes = 0x1120_0000;

    /// <summary>The first address of the reference eFuse capture window.
    /// <para>参考实现 eFuse 采集窗口的起始地址。</para></summary>
    /// <value>
    /// <c>0x11C10000</c>, the <c>efuseaddr</c> constant
    /// (Library/DA/mtk_da_handler.py:794), transcribed from the upstream <c>efuse_addr</c> column.
    /// <para><c>0x11C10000</c>，即 <c>efuseaddr</c> 常量（Library/DA/mtk_da_handler.py:794），与上游
    /// <c>efuse_addr</c> 列记录的外设基址一致。</para>
    /// </value>
    public const uint EfuseWindowStartAddress = 0x11C1_0000;

    /// <summary>The byte length of the reference eFuse capture window.
    /// <para>参考实现 eFuse 采集窗口的字节长度。</para></summary>
    /// <value>
    /// <c>0x10000</c> bytes, the <c>efusesize</c> constant, exclusive at the top
    /// (Library/DA/mtk_da_handler.py:795 and :808-809).
    /// <para><c>0x10000</c> 字节，即 <c>efusesize</c> 常量，上界为开区间
    /// （Library/DA/mtk_da_handler.py:795 与 :808-809）。</para>
    /// </value>
    public const int EfuseWindowLengthBytes = 0x1_0000;

    /// <summary>The last address of the reference eFuse capture window, exclusive.
    /// <para>参考实现 eFuse 采集窗口的结束（不含）地址。</para></summary>
    /// <value>
    /// <see cref="EfuseWindowStartAddress"/> plus <see cref="EfuseWindowLengthBytes"/>.
    /// <para>等于 <see cref="EfuseWindowStartAddress"/> 加上 <see cref="EfuseWindowLengthBytes"/>。</para>
    /// </value>
    public static uint EfuseWindowEndAddress => checked(EfuseWindowStartAddress + (uint)EfuseWindowLengthBytes);

    /// <summary>The first address of the reference dynamic-RAM window.
    /// <para>参考实现动态 RAM 窗口的起始地址。</para></summary>
    /// <value>
    /// <c>0x40000000</c>, the <c>dramaddr</c> default
    /// (Library/DA/mtk_da_handler.py:788), replaced by the agent's RAM-info reply when the family reports
    /// one (Library/DA/mtk_da_handler.py:796-798).
    /// <para><c>0x40000000</c>，即 <c>dramaddr</c> 默认值（Library/DA/mtk_da_handler.py:788）；家族会上报
    /// 内存信息时由该回复替换（Library/DA/mtk_da_handler.py:796-798）。</para>
    /// </value>
    public const uint DramWindowStartAddress = 0x4000_0000;

    /// <summary>The exclusive top of the address space the reference dynamic-RAM window is measured against.
    /// <para>参考动态 RAM 窗口所度量的地址空间上界（不含）。</para></summary>
    /// <value>
    /// <c>0x100000000</c>: the reference computes the default dynamic-RAM length as
    /// <c>0x100000000 - dramaddr</c>, so the fallback span is <c>0xC0000000</c> bytes, wider than any
    /// single capture this layer will walk (Library/DA/mtk_da_handler.py:789 and :806).
    /// <para><c>0x100000000</c>：参考以 <c>0x100000000 - dramaddr</c> 计算默认动态 RAM 长度，因此回退跨度为
    /// <c>0xC0000000</c> 字节，比本层愿意遍历的任何单次采集都宽
    /// （Library/DA/mtk_da_handler.py:789 与 :806）。</para>
    /// </value>
    public const long DramWindowEndAddress = 0x1_0000_0000;

    /// <summary>The largest window a paged bulk capture may walk.
    /// <para>分页批量采集允许遍历的最大窗口。</para></summary>
    /// <value>
    /// <c>0x11200000</c> bytes, the widest region span <c>da memdump</c> names for a single paged read
    /// (Library/DA/mtk_da_handler.py:793 and :812-813). The reference's fallback dynamic-RAM span is wider
    /// still, but it exceeds this ceiling and a whole-memory image is not what this layer publishes, so a
    /// caller who wants it names a bounded sub-window instead.
    /// <para><c>0x11200000</c> 字节，即 <c>da memdump</c> 为单次分页读取所指出的最宽区域跨度
    /// （Library/DA/mtk_da_handler.py:793 与 :812-813）。参考的动态 RAM 回退跨度更宽，但它超过本上限，
    /// 而整存镜像也不是本层的发布目标，因此需要更宽范围的调用方应改为指定有界子窗口。</para>
    /// </value>
    public const int MaximumRegionCaptureBytes = SramDumpLengthBytes;

    /// <summary>Validates a window length destined for a paged bulk capture.
    /// <para>校验用于分页批量采集的窗口长度。</para></summary>
    /// <param name="lengthBytes">The requested byte count. 请求的字节数。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lengthBytes"/> is not positive or
    /// exceeds <see cref="MaximumRegionCaptureBytes"/>.
    /// <para><paramref name="lengthBytes"/> 非正，或超过 <see cref="MaximumRegionCaptureBytes"/>。</para></exception>
    public static void ValidateRegionCaptureLength(long lengthBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lengthBytes, (long)MaximumRegionCaptureBytes);
    }

    /// <summary>Reports whether a window reaches into the documented eFuse capture window.
    /// <para>报告某个窗口是否伸入已记载的 eFuse 采集窗口。</para></summary>
    /// <remarks>
    /// The eFuse window is the only OTP-adjacent span the reference addresses with ordinary download-agent
    /// reads, and it does so only from <c>da memdump</c> (Library/DA/mtk_da_handler.py:794-795 and
    /// :808-809). Every other path must treat it as off limits, which is the same discipline the RPMB
    /// commands keep by addressing the replay-protected area through its own commands rather than through
    /// ordinary storage (MtkRpmbAccess.cs).
    /// 该 eFuse 窗口是参考用普通下载代理读取寻址的唯一 OTP 相邻范围，且仅在 <c>da memdump</c> 中如此
    /// （Library/DA/mtk_da_handler.py:794-795 与 :808-809）。其他所有路径都必须把它视为禁区，这与 RPMB
    /// 命令所持的纪律相同：重放保护区域只经其专用命令寻址，而不走普通存储路径。
    /// </remarks>
    /// <param name="startAddress">The inclusive first address of the window. 起始（含）地址。</param>
    /// <param name="endAddress">The exclusive last address of the window. 结束（不含）地址。</param>
    /// <returns><see langword="true"/> when the window shares any byte with the eFuse span.
    /// <para>窗口与 eFuse 范围有任何字节重叠时为 <see langword="true"/>。</para></returns>
    public static bool IntersectsEfuseWindow(long startAddress, long endAddress) =>
        endAddress > EfuseWindowStartAddress && startAddress < EfuseWindowEndAddress;

    /// <summary>Reports whether a window lies wholly inside the documented eFuse capture window.
    /// <para>报告某个窗口是否完全位于已记载的 eFuse 采集窗口之内。</para></summary>
    /// <param name="startAddress">The inclusive first address of the window. 起始（含）地址。</param>
    /// <param name="endAddress">The exclusive last address of the window. 结束（不含）地址。</param>
    /// <returns><see langword="true"/> when the window is contained in the eFuse span.
    /// <para>窗口被 eFuse 范围完全包含时为 <see langword="true"/>。</para></returns>
    public static bool IsInsideEfuseWindow(long startAddress, long endAddress) =>
        startAddress >= EfuseWindowStartAddress && endAddress <= EfuseWindowEndAddress;

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

/// <summary>Names which documented download-agent address span a capture window belongs to.
/// <para>指明采集窗口所属的、已记载的下载代理地址范围。</para></summary>
/// <remarks>
/// <para>The region is a policy label with teeth, not a description: the ordinary boot-ROM walk and the
/// ordinary peek and poke paths refuse any window that reaches into the eFuse span, so reading that span
/// requires a window explicitly labelled <see cref="Efuse"/>, exactly as <c>da memdump</c> names it apart
/// from the other three regions (Library/DA/mtk_da_handler.py:788-813).</para>
/// <para>该区域是一个真正生效的策略标签，而不是描述：常规的 boot-ROM 步进遍历与普通 peek、poke 路径都会拒绝
/// 伸入 eFuse 范围的窗口，因此读取该范围必须使用显式标记为 <see cref="Efuse"/> 的窗口，正如
/// <c>da memdump</c> 把它与另外三个区域分开命名（Library/DA/mtk_da_handler.py:788-813）。</para>
/// <para>No region here authorises a write to one-time-programmable cells. The reference declares the
/// Legacy OTP command bytes (Library/DA/legacy/dalegacy_param.py:105-110 with the interface-specific
/// variants at :18-23 and :38-43) but never sends any of them, so no request or reply shape is provable
/// and this layer offers no OTP programming path at all.</para>
/// <para>这里的任何区域都不授权向一次性可编程单元写入。参考实现声明了 Legacy 的 OTP 命令字节
/// （Library/DA/legacy/dalegacy_param.py:105-110，另有按接口变体 :18-23 与 :38-43），但从未发送其中任何一条，
/// 因此请求与回复形态都无法证实，本层完全不提供 OTP 编程路径。</para>
/// </remarks>
public enum MtkDaMemoryRegion
{
    /// <summary>The boot-ROM window the reference dump loop walks, <c>0x0</c> through <c>0x200000</c>.
    /// <para>参考采集循环遍历的 boot-ROM 窗口，即 <c>0x0</c> 到 <c>0x200000</c>。</para></summary>
    Brom,
    /// <summary>The static-RAM region, reported by the agent or defaulted by the reference.
    /// <para>静态 RAM 区域，由代理上报，或按参考实现的默认值取值。</para></summary>
    Sram,
    /// <summary>The dynamic-RAM region, reported by the agent or defaulted by the reference.
    /// <para>动态 RAM 区域，由代理上报，或按参考实现的默认值取值。</para></summary>
    Dram,
    /// <summary>The eFuse window the reference reads only through its named memory-dump region.
    /// <para>参考实现仅通过其显式命名的内存转储区域读取的 eFuse 窗口。</para></summary>
    Efuse
}

/// <summary>
/// One bounded download-agent capture window: step-aligned for the boot-ROM walk, page-aligned for the
/// paged region walk.
/// <para>一个有界的下载代理采集窗口：boot-ROM 步进遍历要求按步长对齐，分页区域遍历则按页对齐。</para>
/// </summary>
/// <param name="StartAddress">The inclusive first address of the window.
/// <para>窗口的起始（含）地址。</para></param>
/// <param name="LengthBytes">The positive byte count; a whole multiple of the capture step for the
/// boot-ROM walk. boot-ROM 步进遍历要求正数且为采集步长整数倍的字节数。</param>
/// <param name="Region">The documented span the window belongs to; the boot-ROM region is the default so
/// an unlabelled window keeps the meaning it had before regions existed.
/// <para>窗口所属的已记载范围；未标记时默认为 boot-ROM 区域，使无标签窗口保持其原有含义。</para></param>
public readonly record struct MtkDaMemoryRange(uint StartAddress, int LengthBytes,
    MtkDaMemoryRegion Region = MtkDaMemoryRegion.Brom)
{
    /// <summary>The whole reference boot-ROM window, the default capture target.
    /// <para>完整的参考 boot-ROM 窗口，即默认采集目标。</para></summary>
    /// <value>
    /// The range <c>0x00000000</c> plus <c>0x200000</c> bytes used by the reference dump loop
    /// (Library/DA/mtk_daloader.py:338-356).
    /// <para>范围 <c>0x00000000</c> 加 <c>0x200000</c> 字节，即参考采集循环所用
    /// （Library/DA/mtk_daloader.py:338-356）。</para>
    /// </value>
    public static MtkDaMemoryRange BromDump => new(MtkDaMemoryAccess.BromDumpStartAddress,
        MtkDaMemoryAccess.BromDumpLengthBytes, MtkDaMemoryRegion.Brom);

    /// <summary>The static-RAM window the reference memory dump reads when the agent reports no geometry.
    /// <para>代理未上报几何信息时，参考内存转储读取的静态 RAM 窗口。</para></summary>
    /// <value>
    /// The range <c>0x200000</c> plus <c>0x11200000</c> bytes
    /// (Library/DA/mtk_da_handler.py:792-793 and :812-813). A family that reports RAM geometry should use
    /// <see cref="ForReportedRegion"/> instead, because the reference itself prefers the report over this
    /// default (Library/DA/mtk_da_handler.py:799-801).
    /// <para>范围 <c>0x200000</c> 加 <c>0x11200000</c> 字节（Library/DA/mtk_da_handler.py:792-793 与
    /// :812-813）。会上报内存几何信息的家族应改用 <see cref="ForReportedRegion"/>，因为参考本身优先采用
    /// 上报值而不是该默认值（Library/DA/mtk_da_handler.py:799-801）。</para>
    /// </value>
    public static MtkDaMemoryRange SramDump => new(MtkDaMemoryAccess.SramDumpStartAddress,
        MtkDaMemoryAccess.SramDumpLengthBytes, MtkDaMemoryRegion.Sram);

    /// <summary>The eFuse window the reference memory dump reads under its own region name.
    /// <para>参考内存转储以其独立区域名称读取的 eFuse 窗口。</para></summary>
    /// <value>
    /// The range <c>0x11C10000</c> plus <c>0x10000</c> bytes
    /// (Library/DA/mtk_da_handler.py:794-795 and :808-809). This is the only window label that may address
    /// the eFuse span; peek, poke and the boot-ROM walk all refuse it.
    /// <para>范围 <c>0x11C10000</c> 加 <c>0x10000</c> 字节
    /// （Library/DA/mtk_da_handler.py:794-795 与 :808-809）。只有该窗口标签可以寻址 eFuse 范围；peek、
    /// poke 与 boot-ROM 步进遍历都会拒绝它。</para>
    /// </value>
    public static MtkDaMemoryRange EfuseDump => new(MtkDaMemoryAccess.EfuseWindowStartAddress,
        MtkDaMemoryAccess.EfuseWindowLengthBytes, MtkDaMemoryRegion.Efuse);

    /// <summary>Builds a labelled window inside the dynamic-RAM region.
    /// <para>构造动态 RAM 区域内一个带标签的窗口。</para></summary>
    /// <remarks>
    /// The reference gives no whole-memory default that fits this window model: its fallback dynamic-RAM
    /// length is <c>0x100000000 - dramaddr</c>, that is <c>0xC0000000</c> bytes, which exceeds both the
    /// ceiling of a paged capture and the width of a 32-bit signed length
    /// (Library/DA/mtk_da_handler.py:788-789 and :806-807). A caller who wants dynamic RAM therefore names
    /// a bounded sub-window, or takes the agent-reported geometry through <see cref="ForReportedRegion"/>.
    /// <para>参考实现没有能适配本窗口模型的整存默认值：其动态 RAM 回退长度为 <c>0x100000000 - dramaddr</c>，
    /// 即 <c>0xC0000000</c> 字节，既超过分页采集上限，也超过 32 位有符号长度宽度
    /// （Library/DA/mtk_da_handler.py:788-789 与 :806-807）。因此需要读取动态 RAM 的调用方必须指定有界子窗口，
    /// 或通过 <see cref="ForReportedRegion"/> 采用代理上报的几何信息。</para>
    /// </remarks>
    /// <param name="startAddress">The inclusive first address. 起始（含）地址。</param>
    /// <param name="lengthBytes">The positive byte count. 正数字节数。</param>
    /// <returns>A window labelled with the dynamic-RAM region. 标记为动态 RAM 区域的窗口。</returns>
    public static MtkDaMemoryRange ForDramWindow(uint startAddress, int lengthBytes) =>
        new(startAddress, lengthBytes, MtkDaMemoryRegion.Dram);

    /// <summary>Builds a window from the RAM geometry the download agent reported for itself.
    /// <para>依据下载代理自报的内存几何信息构造窗口。</para></summary>
    /// <remarks>
    /// Only the XFlash family answers the RAM-info device-control query, and it carries the static and
    /// dynamic region type, base and size as six little-endian doublewords or quadwords
    /// (Library/DA/xflash/xflash_param.py:52 with Library/DA/xflash/xflash_lib.py:473-492, stored into the
    /// configuration at :1073 and preferred by the memory dump at
    /// Library/DA/mtk_da_handler.py:796-801). The XML client's equivalent call sits inside the commented
    /// block of <c>reinit</c> (Library/DA/xml/xml_lib.py:984-990) and the Legacy client has none, so both
    /// of those families keep the documented defaults and this factory is unavailable to them.
    /// 只有 XFlash 家族会应答内存信息器件控制查询，其以六个小端双字或四字携带静态与动态区域的类型、基址与大小
    /// （Library/DA/xflash/xflash_param.py:52，配合 Library/DA/xflash/xflash_lib.py:473-492，在 :1073 写入配置，
    /// 并在 Library/DA/mtk_da_handler.py:796-801 被内存转储优先采用）。XML 客户端的对应调用位于
    /// <c>reinit</c> 的注释块之内（Library/DA/xml/xml_lib.py:984-990），Legacy 客户端则完全没有，因此这两个家族
    /// 沿用已记载的默认值，也无法使用本工厂方法。
    /// </remarks>
    /// <param name="reported">One reported region, as decoded by
    /// <see cref="MtkXFlashRamInfo"/>. 已解码的单个上报区域。</param>
    /// <param name="region">The region label to apply; only <see cref="MtkDaMemoryRegion.Sram"/> and
    /// <see cref="MtkDaMemoryRegion.Dram"/> are reported. 要应用的区域标签；仅
    /// <see cref="MtkDaMemoryRegion.Sram"/> 与 <see cref="MtkDaMemoryRegion.Dram"/> 会被上报。</param>
    /// <returns>The window the agent itself declared. 代理自行声明的窗口。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="region"/> is not a reported region,
    /// the reported base does not fit the 32-bit agent address space, or the reported size is zero or wider
    /// than <see cref="MtkDaMemoryAccess.MaximumRegionCaptureBytes"/>.
    /// <para><paramref name="region"/> 不是会上报的区域、上报基址无法容纳于 32 位代理地址空间，或上报大小
    /// 为零或超过 <see cref="MtkDaMemoryAccess.MaximumRegionCaptureBytes"/>。</para></exception>
    public static MtkDaMemoryRange ForReportedRegion(MtkXFlashRamRegion reported, MtkDaMemoryRegion region)
    {
        if (region is not (MtkDaMemoryRegion.Sram or MtkDaMemoryRegion.Dram))
        {
            throw new ArgumentOutOfRangeException(nameof(region), region,
                "Only the static and dynamic RAM regions are reported by the download agent.");
        }

        if (reported.BaseAddress > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(reported), reported.BaseAddress,
                "The reported region base leaves the 32-bit download-agent address space.");
        }

        if (reported.Size == 0 || reported.Size > (ulong)MtkDaMemoryAccess.MaximumRegionCaptureBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(reported), reported.Size,
                $"A reported region must be between 1 and " +
                $"{MtkDaMemoryAccess.MaximumRegionCaptureBytes} bytes.");
        }

        return new MtkDaMemoryRange((uint)reported.BaseAddress, (int)reported.Size, region);
    }

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
    /// (Library/DA/mtk_daloader.py:351-352). This is the boot-ROM walk, so a window labelled for any other
    /// region is refused rather than walked one doubleword at a time, and a window that reaches the eFuse
    /// span is refused because only <see cref="ValidateCapture"/> admits that region
    /// (Library/DA/mtk_da_handler.py:794-795 and :808-809).
    /// 之所以要求步长对齐，是因为参考循环每条命令精确发出
    /// <see cref="MtkDaMemoryAccess.DumpStepWords"/> 个双字，从不处理较短的尾部
    /// （Library/DA/mtk_daloader.py:351-352）。本校验属于 boot-ROM 遍历，因此标记为其他区域的窗口会被拒绝，
    /// 而不会逐双字遍历；伸入 eFuse 范围的窗口同样被拒绝，因为只有 <see cref="ValidateCapture"/> 接受该区域
    /// （Library/DA/mtk_da_handler.py:794-795 与 :808-809）。
    /// </remarks>
    /// <param name="parameterName">The caller parameter to name in a thrown argument error.
    /// <para>抛出参数错误时要指名的调用方参数。</para></param>
    /// <exception cref="ArgumentOutOfRangeException">The length is not positive, exceeds
    /// <see cref="MtkDaMemoryAccess.MaximumDumpBytes"/>, the window leaves the 32-bit address space, the
    /// window is labelled for a region other than the boot ROM, or it reaches the eFuse span.
    /// <para>长度非正、超过 <see cref="MtkDaMemoryAccess.MaximumDumpBytes"/>、窗口越出 32 位地址空间、窗口标记
    /// 为 boot-ROM 以外的区域，或伸入 eFuse 范围。</para></exception>
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

        if (Region != MtkDaMemoryRegion.Brom)
        {
            throw new ArgumentOutOfRangeException(parameterName, Region,
                $"A range labelled {Region} must be captured with the paged region walk, not the " +
                $"{MtkDaMemoryAccess.DumpStepBytes}-byte register-step walk.");
        }

        if (MtkDaMemoryAccess.IntersectsEfuseWindow(StartAddress, EndAddress))
        {
            throw new ArgumentOutOfRangeException(parameterName, StartAddress,
                "The boot-ROM walk does not reach the eFuse window; capture it as an eFuse region instead.");
        }
    }

    /// <summary>Checks the bounds a paged region capture needs before the first device byte is requested.
    /// <para>在请求首个设备字节之前校验分页区域采集所需的边界。</para></summary>
    /// <remarks>
    /// The reference pages a whole region through the bulk read primitive and never requires step alignment,
    /// so only the span, the address space and the region policy are checked here
    /// (Library/DA/mtk_da_handler.py:784-813 with :597-624).
    /// 参考通过批量读取原语为整块区域分页，从不要求步长对齐，因此此处只校验范围、地址空间与区域策略
    /// （Library/DA/mtk_da_handler.py:784-813，配合 :597-624）。
    /// </remarks>
    /// <param name="parameterName">The caller parameter to name in a thrown argument error.
    /// <para>抛出参数错误时要指名的调用方参数。</para></param>
    /// <exception cref="ArgumentOutOfRangeException">The length is not positive, exceeds
    /// <see cref="MtkDaMemoryAccess.MaximumRegionCaptureBytes"/>, the window leaves the 32-bit address space,
    /// <paramref name="parameterName"/> names an undefined region, an eFuse-labelled window leaves the
    /// documented eFuse span, or a window of another region reaches into it.
    /// <para>长度非正、超过 <see cref="MtkDaMemoryAccess.MaximumRegionCaptureBytes"/>、窗口越出 32 位地址空间、
    /// 区域未定义、标记为 eFuse 的窗口越出已记载的 eFuse 范围，或其他区域的窗口伸入该范围。</para></exception>
    public void ValidateCapture(string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(LengthBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(LengthBytes,
            MtkDaMemoryAccess.MaximumRegionCaptureBytes);
        if (Region is not (MtkDaMemoryRegion.Brom or MtkDaMemoryRegion.Sram or MtkDaMemoryRegion.Dram
            or MtkDaMemoryRegion.Efuse))
        {
            throw new ArgumentOutOfRangeException(parameterName, Region,
                "The capture range names a download-agent region that is not documented.");
        }

        if (EndAddress > 0x1_0000_0000L)
        {
            throw new ArgumentOutOfRangeException(parameterName, StartAddress,
                "The capture range leaves the 32-bit download-agent address space.");
        }

        if (Region == MtkDaMemoryRegion.Efuse &&
            !MtkDaMemoryAccess.IsInsideEfuseWindow(StartAddress, EndAddress))
        {
            throw new ArgumentOutOfRangeException(parameterName, StartAddress,
                $"An eFuse capture must stay inside 0x{MtkDaMemoryAccess.EfuseWindowStartAddress:X8}.." +
                $"0x{MtkDaMemoryAccess.EfuseWindowEndAddress - 1:X8}.");
        }

        if (Region != MtkDaMemoryRegion.Efuse &&
            MtkDaMemoryAccess.IntersectsEfuseWindow(StartAddress, EndAddress))
        {
            throw new ArgumentOutOfRangeException(parameterName, StartAddress,
                "Only a range labelled as the eFuse region may reach the eFuse window.");
        }
    }
}