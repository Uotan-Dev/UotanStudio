using UotanStudio.Comm.Protocols.Mtk.Exploit;

namespace UotanStudio.Comm.Protocols.Mtk.HwCrypto;

/// <summary>
/// The download-agent register primitives every MediaTek hardware-crypto engine is built on: register reads,
/// register writes, and the cache-control extended command.
/// <para>所有联发科硬件加密引擎共同依赖的下载代理寄存器原语：寄存器读取、寄存器写入，以及缓存控制扩展命令。</para>
/// </summary>
/// <remarks>
/// <para>The reference engines never touch a transport directly: <c>mtkclient/Library/Hardware/hwcrypto.py</c>,
/// lines 20-22 and 41-42, wires every <c>read32</c>, <c>write32</c> and cache refresh to the preloader command
/// layer, whose framing is <c>mtkclient/Library/mtk_preloader.py</c> lines 264-293 (<c>write</c>), lines 236-256
/// (<c>read</c>) and lines 355-361 (<c>run_ext_cmd</c>, command <c>0xC8</c> with the <c>0xB1</c> cache-control
/// operand, opcodes declared at lines 87-91). This class keeps that single seam so an engine can be driven
/// against a scripted device exactly like the exploit drivers are.</para>
/// <para>参考引擎从不直接访问传输层：<c>mtkclient/Library/Hardware/hwcrypto.py</c> 第 20-22 与 41-42 行把每个
/// <c>read32</c>、<c>write32</c> 与缓存刷新都接到 preloader 命令层，其帧格式见
/// <c>mtkclient/Library/mtk_preloader.py</c> 第 264-293 行（<c>write</c>）、第 236-256 行（<c>read</c>）与第 355-361 行
/// （<c>run_ext_cmd</c>：命令 <c>0xC8</c> 加上缓存控制操作数 <c>0xB1</c>，操作码声明见第 87-91 行）。本类保留这唯一接缝，
/// 因此引擎可以像漏洞驱动一样用脚本化的设备来驱动。</para>
/// </remarks>
internal sealed class MtkDaRegisterAccess(IMtkExploitByteChannel channel, int timeoutMs)
{
    /// <summary>The cache-control extended command opcode. <para>缓存控制扩展命令的操作码。</para></summary>
    /// <value>0xC8 (<c>mtkclient/Library/mtk_preloader.py</c>, line 87).
    /// <para>即 0xC8（<c>mtkclient/Library/mtk_preloader.py</c> 第 87 行）。</para></value>
    internal const byte CacheCommand = 0xC8;

    /// <summary>The refresh operand the crypto engines ask the download agent to run.
    /// <para>加密引擎要求下载代理执行的刷新操作数。</para></summary>
    /// <value>0xB1 (<c>mtkclient/Library/Hardware/hwcrypto.py</c>, lines 133 and 138, and
    /// <c>mtkclient/Library/mtk_preloader.py</c>, line 355). <para>即 0xB1（
    /// <c>mtkclient/Library/Hardware/hwcrypto.py</c> 第 133 与 138 行，以及
    /// <c>mtkclient/Library/mtk_preloader.py</c> 第 355 行）。</para></value>
    internal const byte RefreshCachesOperand = 0xB1;

    /// <summary>The widest register read any engine issues in one download-agent command.
    /// <para>任何引擎在单条下载代理命令中发起的最宽寄存器读取。</para></summary>
    /// <value>4 words, the width of the GCPU secure-slot read in
    /// <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c>, line 419; a wider request is refused rather than
    /// framed. <para>4 个字，即 GCPU 安全槽读取的宽度（
    /// <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c> 第 419 行）；更宽的请求会被拒绝而不是发出。</para></value>
    internal const int MaximumRegisterReadWords = 4;

    /// <summary>The widest register write any engine issues in one download-agent command.
    /// <para>任何引擎在单条下载代理命令中发起的最宽寄存器写入。</para></summary>
    /// <value>8 words, the width of the GCPU initialization vector slot clear in
    /// <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c>, line 232.
    /// <para>8 个字，即 GCPU 初始化向量槽清零的宽度（
    /// <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c> 第 232 行）。</para></value>
    internal const int MaximumRegisterWriteWords = 8;

    /// <summary>The largest byte range any engine walks in one dump or chunked transfer.
    /// <para>任何引擎在单次读取或分块传输中跨越的最大字节范围。</para></summary>
    /// <value>0x40000, the widest reference range (<c>mtkclient/Library/Exploit/hashimoto.py</c>, line 70 walks
    /// <c>0x200000</c> to <c>0x240000</c>). <para>0x40000，即参考实现中最宽的范围
    /// （<c>mtkclient/Library/Exploit/hashimoto.py</c> 第 70 行从 <c>0x200000</c> 走到 <c>0x240000</c>）。</para></value>
    internal const int MaximumTransferBytes = 0x40000;

    /// <summary>Reads one register word. <para>读取一个寄存器字。</para></summary>
    /// <param name="address">The register address. <para>寄存器地址。</para></param>
    /// <param name="cancellationToken">Cancellation for the exchange. <para>该次交换的取消令牌。</para></param>
    /// <exception cref="MtkProtocolException">The download agent refused the read, which leaves a
    /// read-modify-write step without the value it needs. <para>下载代理拒绝读取；这会让读改写步骤拿不到所需
    /// 数值。</para></exception>
    /// <returns>The register value. <para>寄存器数值。</para></returns>
    internal async Task<uint> ReadRegisterAsync(uint address, CancellationToken cancellationToken)
    {
        var values = await ReadRegisterWordsAsync(address, 1, cancellationToken).ConfigureAwait(false);
        return values[0];
    }

    /// <summary>Reads up to <see cref="MaximumRegisterReadWords"/> consecutive register words.
    /// <para>读取至多 <see cref="MaximumRegisterReadWords"/> 个连续寄存器字。</para></summary>
    /// <param name="address">The first register address. <para>起始寄存器地址。</para></param>
    /// <param name="words">The word count, from one through <see cref="MaximumRegisterReadWords"/>.
    /// <para>字数，范围从 1 到 <see cref="MaximumRegisterReadWords"/>。</para></param>
    /// <param name="cancellationToken">Cancellation for the exchange. <para>该次交换的取消令牌。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="words"/> is outside the width any engine
    /// issues. <para><paramref name="words"/> 超出引擎会发出的宽度。</para></exception>
    /// <exception cref="MtkProtocolException">The download agent refused the read.
    /// <para>下载代理拒绝读取。</para></exception>
    /// <returns>The register words. <para>读取到的寄存器字。</para></returns>
    internal async Task<uint[]> ReadRegisterWordsAsync(uint address, int words, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(words, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(words, MaximumRegisterReadWords);
        var values = await MtkBromCommandWire.Read32Async(channel, address, words, timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (values.Length != words)
        {
            throw new MtkProtocolException(
                $"The download agent refused the register read at 0x{address:X8} for {words} word(s); the crypto engine cannot continue with an unreadable register.");
        }

        return values;
    }

    /// <summary>Writes one register word. <para>写入一个寄存器字。</para></summary>
    /// <param name="address">The register address. <para>寄存器地址。</para></param>
    /// <param name="value">The word to store. <para>要写入的字。</para></param>
    /// <param name="cancellationToken">Cancellation for the exchange. <para>该次交换的取消令牌。</para></param>
    /// <exception cref="MtkProtocolException">The download agent refused the write.
    /// <para>下载代理拒绝写入。</para></exception>
    internal Task WriteRegisterAsync(uint address, uint value, CancellationToken cancellationToken) =>
        WriteRegisterWordsAsync(address, [value], cancellationToken);

    /// <summary>Writes consecutive register words in one download-agent command.
    /// <para>用一条下载代理命令写入连续寄存器字。</para></summary>
    /// <param name="address">The first register address. <para>起始寄存器地址。</para></param>
    /// <param name="values">The words to store, at most <see cref="MaximumRegisterWriteWords"/> of them, which is
    /// the widest slot write the reference engines perform.
    /// <para>要写入的字，最多 <see cref="MaximumRegisterWriteWords"/> 个，即参考引擎执行的最宽槽位写入。</para></param>
    /// <param name="cancellationToken">Cancellation for the exchange. <para>该次交换的取消令牌。</para></param>
    /// <exception cref="ArgumentException"><paramref name="values"/> is empty.
    /// <para><paramref name="values"/> 为空。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="values"/> is wider than any reference engine
    /// frames. <para><paramref name="values"/> 超过参考引擎会发出的宽度。</para></exception>
    /// <exception cref="MtkProtocolException">The download agent refused the write. The reference drivers ignore
    /// the status of a register write and keep going; this seam aborts instead, because after a refused write the
    /// engine's next read returns a value that was never programmed.
    /// <para>下载代理拒绝写入。参考驱动会忽略寄存器写入的状态继续执行；本接缝会中止，因为被拒绝的写入之后，引擎下一次
    /// 读到的值从未被真正编程。</para></exception>
    internal async Task WriteRegisterWordsAsync(uint address, uint[] values, CancellationToken cancellationToken)
    {
        if (values.Length == 0)
        {
            throw new ArgumentException("A register write needs at least one word.", nameof(values));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(values.Length, MaximumRegisterWriteWords);
        if (!await MtkBromCommandWire.Write32Async(channel, address, values, timeoutMs, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new MtkProtocolException(
                $"The download agent refused the register write at 0x{address:X8} for {values.Length} word(s); the crypto engine stopped before relying on an unprogrammed register.");
        }
    }

    /// <summary>Runs the cache-control extended command the engines issue before lifting a blacklist.
    /// <para>执行引擎在解除黑名单之前发出的缓存控制扩展命令。</para></summary>
    /// <param name="cancellationToken">Cancellation for the exchange. <para>该次交换的取消令牌。</para></param>
    /// <exception cref="MtkProtocolException">The download agent did not echo the command or the operand.
    /// <para>下载代理没有回显命令或操作数。</para></exception>
    /// <remarks>
    /// <para>Transcription of <c>mtkclient/Library/mtk_preloader.py</c>, lines 355-361: the opcode and the operand
    /// are echoed, then the two trailing status words are consumed without being interpreted. The reference
    /// engines always pass <c>b"\xB1"</c> here (<c>mtkclient/Library/Hardware/hwcrypto.py</c>, lines 133
    /// and 138).</para>
    /// <para>转录 <c>mtkclient/Library/mtk_preloader.py</c> 第 355-361 行：先回显操作码与操作数，然后读取并丢弃尾部两个
    /// 状态字，不做解释。参考引擎在此处总是传入 <c>b"\xB1"</c>
    /// （<c>mtkclient/Library/Hardware/hwcrypto.py</c> 第 133 与 138 行）。</para>
    /// </remarks>
    internal async Task RefreshCachesAsync(CancellationToken cancellationToken)
    {
        await MtkBromCommandWire.EchoAsync(channel, [CacheCommand], timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        await MtkBromCommandWire.EchoAsync(channel, [RefreshCachesOperand], timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        await channel.ReadAsync(1, timeoutMs, cancellationToken).ConfigureAwait(false);
        await channel.ReadAsync(2, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates a byte range before any command is framed for it.
    /// <para>在为某个字节范围发出命令之前先校验该范围。</para></summary>
    /// <param name="length">The requested byte count. <para>请求的字节数。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is zero, negative, or wider than
    /// <see cref="MaximumTransferBytes"/>. <para><paramref name="length"/> 为零、负数或超过
    /// <see cref="MaximumTransferBytes"/>。</para></exception>
    /// <remarks>
    /// <para>The reference drivers only ever walk a fixed range
    /// (<c>mtkclient/Library/Exploit/amonet.py</c>, lines 26-32 and 129-135), so a request outside that envelope is
    /// treated as a caller mistake rather than a transfer the download agent should attempt.</para>
    /// <para>参考驱动只会跨越固定范围（<c>mtkclient/Library/Exploit/amonet.py</c> 第 26-32 与 129-135 行），因此超出该
    /// 范围的请求被当作调用方错误，而不是应当让下载代理尝试的传输。</para>
    /// </remarks>
    internal static void ThrowUnlessTransferInBounds(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaximumTransferBytes, nameof(length));
    }

    /// <summary>Validates that a byte range stays inside the 32-bit device address space.
    /// <para>校验某个字节范围始终处于 32 位设备地址空间之内。</para></summary>
    /// <param name="address">The first byte address. <para>起始字节地址。</para></param>
    /// <param name="length">The byte count that follows it. <para>其后的字节数。</para></param>
    /// <exception cref="ArgumentOutOfRangeException">The range wraps past <code>uint.MaxValue</code>.
    /// <para>该范围越过 <code>uint.MaxValue</code> 回绕。</para></exception>
    internal static void ThrowUnlessRangeInAddressSpace(uint address, int length)
    {
        if (address > uint.MaxValue - (uint)length)
        {
            throw new ArgumentOutOfRangeException(nameof(address),
                $"The range at 0x{address:X8} plus {length} byte(s) wraps past the 32-bit device address space.");
        }
    }
}