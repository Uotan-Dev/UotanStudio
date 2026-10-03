using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk.HwCrypto;

/// <summary>
/// The MediaTek CQDMA copy engine the Hashimoto family uses to lift the Boot ROM range checks and to read back
/// protected memory.
/// <para>联发科 CQDMA 拷贝引擎，Hashimoto 家族用它解除 Boot ROM 范围检查并读回受保护内存。</para>
/// </summary>
/// <remarks>
/// <para>Every register offset is the <c>regval</c> dictionary of <c>mtkclient/Library/Hardware/cqdma.py</c>,
/// lines 9-21, and the register accessors at lines 30-46 wrap one download-agent word per access. The reference
/// reads its staging word from <c>self.chipconfig.ap_dma_mem</c> in <c>cqread32</c> although
/// <c>Cqdma.__init__</c> sets that attribute to <c>None</c> (line 51), while <c>cqwrite32</c> correctly uses
/// <c>self.setup.ap_dma_mem</c> (line 85); both fields name the same recorded chip value, so this engine uses the
/// single recorded application-DMA address for both directions.</para>
/// <para>所有寄存器偏移取自 <c>mtkclient/Library/Hardware/cqdma.py</c> 第 9-21 行的 <c>regval</c> 字典，第 30-46 行的寄存器
/// 访问器每次只包装一个下载代理字。参考实现的 <c>cqread32</c> 从 <c>self.chipconfig.ap_dma_mem</c> 读取暂存字，但
/// <c>Cqdma.__init__</c> 把该属性设为 <c>None</c>（第 51 行），而 <c>cqwrite32</c> 正确使用
/// <c>self.setup.ap_dma_mem</c>（第 85 行）；两个字段指的是同一个记录的芯片取值，因此本引擎在两个方向都使用这唯一记录的
/// 应用 DMA 地址。</para>
/// </remarks>
internal sealed class MtkCqdmaCryptoEngine(uint cqdmaBase, uint apDmaMem, IReadOnlyList<MtkBlacklistRange> blacklist,
    MtkDaRegisterAccess access)
{
    /// <summary>The block width the Hashimoto dumps walk. <para>Hashimoto 读取流程步进的分块宽度。</para></summary>
    /// <value>16 bytes (<c>mtkclient/Library/Exploit/hashimoto.py</c>, lines 70, 83 and 98-104).
    /// <para>16 字节（<c>mtkclient/Library/Exploit/hashimoto.py</c> 第 70、83 与 98-104 行）。</para></value>
    internal const int BlockBytes = 16;

    private const uint EnableRegister = 0x08;
    private const uint SourceRegister = 0x1C;
    private const uint DestinationRegister = 0x20;
    private const uint LengthRegister = 0x24;
    private const uint TransferArmed = 0x1;
    private const uint TransferWordCount = 4;
    private const uint StagingScrubWord = 0xCAFEBABE;
    private const int MaximumTransferPolls = 0x400;

    /// <summary>Copies one word between the staging address and a device address through the engine.
    /// <para>通过引擎在暂存地址与设备地址之间拷贝一个字。</para></summary>
    /// <param name="source">The word address to copy from. <para>要拷贝来源的字地址。</para></param>
    /// <param name="destination">The word address to copy to. <para>要拷贝目标的字地址。</para></param>
    /// <param name="cancellationToken">Cancellation for the sequence. <para>序列的取消令牌。</para></param>
    /// <exception cref="MtkProtocolException">The download agent refused a register access, or the engine never
    /// cleared its busy bit. <para>下载代理拒绝寄存器访问，或引擎始终没有清除忙位。</para></exception>
    private async Task CopyWordAsync(uint source, uint destination, CancellationToken cancellationToken)
    {
        await access.WriteRegisterAsync(RegisterAddress(SourceRegister), source, cancellationToken)
            .ConfigureAwait(false);
        await access.WriteRegisterAsync(RegisterAddress(DestinationRegister), destination, cancellationToken)
            .ConfigureAwait(false);
        await access.WriteRegisterAsync(RegisterAddress(LengthRegister), TransferWordCount, cancellationToken)
            .ConfigureAwait(false);
        await access.WriteRegisterAsync(RegisterAddress(EnableRegister), TransferArmed, cancellationToken)
            .ConfigureAwait(false);
        await WaitUntilIdleAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes consecutive words to a device range by staging each word in application-DMA memory.
    /// <para>通过在应用 DMA 内存中逐字暂存，向设备范围连续写入字。</para></summary>
    /// <param name="address">The first word address. <para>起始字地址。</para></param>
    /// <param name="values">The words to store. <para>要写入的字。</para></param>
    /// <param name="cancellationToken">Cancellation for the sequence. <para>序列的取消令牌。</para></param>
    /// <exception cref="ArgumentException"><paramref name="values"/> is empty.
    /// <para><paramref name="values"/> 为空。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The range leaves the device address space.
    /// <para>该范围越出设备地址空间。</para></exception>
    /// <exception cref="MtkProtocolException">One of the transfers failed. <para>某次传输失败。</para></exception>
    /// <remarks>
    /// <para>Transcription of <c>mtkclient/Library/Hardware/cqdma.py</c>, lines 84-96: every word is programmed
    /// into the staging address, copied out, and the staging address is then overwritten with the recorded
    /// <c>0xcafebabe</c> scrub word.</para>
    /// <para>转录 <c>mtkclient/Library/Hardware/cqdma.py</c> 第 84-96 行：每个字先写入暂存地址，再被拷贝出去，随后暂存地址
    /// 被记录值 <c>0xcafebabe</c> 的清理字覆盖。</para>
    /// </remarks>
    internal async Task WriteWordsAsync(uint address, IReadOnlyList<uint> values,
        CancellationToken cancellationToken)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("A CQDMA write needs at least one word.", nameof(values));
        }

        MtkDaRegisterAccess.ThrowUnlessRangeInAddressSpace(address, values.Count * 4);
        for (var index = 0; index < values.Count; index++)
        {
            await access.WriteRegisterAsync(apDmaMem, values[index], cancellationToken).ConfigureAwait(false);
            await CopyWordAsync(apDmaMem, address + ((uint)index * 4), cancellationToken).ConfigureAwait(false);
            await access.WriteRegisterAsync(apDmaMem, StagingScrubWord, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads consecutive words from a device range through the engine registers.
    /// <para>通过引擎寄存器从设备范围连续读取字。</para></summary>
    /// <param name="address">The first word address. <para>起始字地址。</para></param>
    /// <param name="words">The word count, at most <see cref="MtkDaRegisterAccess.MaximumRegisterReadWords"/>.
    /// <para>字数，最多 <see cref="MtkDaRegisterAccess.MaximumRegisterReadWords"/>。</para></param>
    /// <param name="cancellationToken">Cancellation for the sequence. <para>序列的取消令牌。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="words"/> is outside the width any engine
    /// issues, or the range leaves the device address space. <para><paramref name="words"/> 超出引擎会发出的宽度，
    /// 或该范围越出设备地址空间。</para></exception>
    /// <exception cref="MtkProtocolException">One of the transfers failed. <para>某次传输失败。</para></exception>
    /// <returns>The words little-endian encoded, as the reference packs them.
    /// <para>按参考实现的打包方式以小端编码的字节。</para></returns>
    /// <remarks>
    /// <para>Transcription of <c>mtkclient/Library/Hardware/cqdma.py</c>, lines 69-82, with the staging address
    /// taken from the recorded chip value as described in the type remarks.</para>
    /// <para>转录 <c>mtkclient/Library/Hardware/cqdma.py</c> 第 69-82 行，暂存地址按类型说明所述取自记录的芯片取值。</para>
    /// </remarks>
    internal async Task<byte[]> ReadWordsAsync(uint address, int words, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(words, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(words, MtkDaRegisterAccess.MaximumRegisterReadWords);
        MtkDaRegisterAccess.ThrowUnlessRangeInAddressSpace(address, words * 4);
        var result = new byte[words * 4];
        for (var index = 0; index < words; index++)
        {
            await CopyWordAsync(address + ((uint)index * 4), apDmaMem, cancellationToken).ConfigureAwait(false);
            var value = await access.ReadRegisterAsync(apDmaMem, cancellationToken).ConfigureAwait(false);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(index * 4), value);
        }

        return result;
    }

    /// <summary>Reads a device range, either through the engine registers or through plain download-agent reads.
    /// <para>读取设备范围，可以走引擎寄存器，也可以走普通下载代理读取。</para></summary>
    /// <param name="address">The first byte address. <para>起始字节地址。</para></param>
    /// <param name="length">The byte count, truncated to exactly this many bytes.
    /// <para>字节数，结果被精确截断到该长度。</para></param>
    /// <param name="useEngineRegisters">Whether the reads go through the CQDMA registers; the reference picks this
    /// from the chip entry (<c>mtkclient/Library/Exploit/hashimoto.py</c>, line 71 and line 104, pass
    /// <c>not chipconfig.blacklist</c>). <para>是否让读取走 CQDMA 寄存器；参考实现按芯片条目决定该值
    /// （<c>mtkclient/Library/Exploit/hashimoto.py</c> 第 71 与 104 行传入 <c>not chipconfig.blacklist</c>）。</para></param>
    /// <param name="cancellationToken">Cancellation for the sequence. <para>序列的取消令牌。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is empty or wider than
    /// <see cref="MtkDaRegisterAccess.MaximumTransferBytes"/>, or the range leaves the device address space.
    /// <para><paramref name="length"/> 为空或超过
    /// <see cref="MtkDaRegisterAccess.MaximumTransferBytes"/>，或该范围越出设备地址空间。</para></exception>
    /// <exception cref="MtkProtocolException">One of the steps failed. <para>某个步骤失败。</para></exception>
    /// <returns>The requested bytes. <para>请求的字节。</para></returns>
    /// <remarks>
    /// <para>Transcription of <c>mtkclient/Library/Hardware/cqdma.py</c>, lines 98-110, whose callers in
    /// <c>mtkclient/Library/Exploit/hashimoto.py</c>, lines 70-71, 82-83 and 98-104, walk sixteen bytes per step;
    /// the trailing partial block is dropped exactly as the reference truncates with <c>res[:length]</c>.</para>
    /// <para>转录 <c>mtkclient/Library/Hardware/cqdma.py</c> 第 98-110 行，其调用方
    /// <c>mtkclient/Library/Exploit/hashimoto.py</c> 第 70-71、82-83 与 98-104 行每步读取十六字节；末尾不足一个分块的部分会像
    /// 参考实现用 <c>res[:length]</c> 截断那样丢弃。</para>
    /// </remarks>
    internal async Task<byte[]> ReadAsync(uint address, int length, bool useEngineRegisters,
        CancellationToken cancellationToken)
    {
        MtkDaRegisterAccess.ThrowUnlessTransferInBounds(length);
        MtkDaRegisterAccess.ThrowUnlessRangeInAddressSpace(address, length);
        var steps = (length + (BlockBytes - 1)) / BlockBytes;
        var result = new byte[steps * BlockBytes];
        var position = 0;
        for (var step = 0; step < steps; step++)
        {
            var start = address + (uint)(step * BlockBytes);
            var bytes = useEngineRegisters
                ? await ReadWordsAsync(start, BlockBytes / 4, cancellationToken).ConfigureAwait(false)
                : await PlainReadAsync(start, BlockBytes / 4, cancellationToken).ConfigureAwait(false);
            bytes.CopyTo(result, position);
            position += bytes.Length;
        }

        return result[..length];
    }

    /// <summary>Lifts every blacklist range the chip entry records.
    /// <para>解除芯片条目记录的每一条黑名单范围。</para></summary>
    /// <param name="cancellationToken">Cancellation for the sequence. <para>序列的取消令牌。</para></param>
    /// <exception cref="InvalidOperationException">The chip entry records no ranges.
    /// <para>芯片条目没有记录任何范围。</para></exception>
    /// <exception cref="MtkProtocolException">One of the ranges failed to program.
    /// <para>某条范围编程失败。</para></exception>
    /// <remarks>
    /// <para>Transcription of <c>mtkclient/Library/Hardware/cqdma.py</c>, lines 124-131: a recorded scalar becomes a
    /// one-word list and is written at the blacklist register itself, with no widening and no control word.</para>
    /// <para>转录 <c>mtkclient/Library/Hardware/cqdma.py</c> 第 124-131 行：记录的标量被当作单字列表写入黑名单寄存器本身，
    /// 既不扩展也不附加控制字。</para>
    /// </remarks>
    internal async Task DisableRangeBlacklistAsync(CancellationToken cancellationToken)
    {
        if (blacklist.Count == 0)
        {
            throw new InvalidOperationException(
                "The CQDMA engine has no blacklist range to lift for this chip entry.");
        }

        foreach (var range in blacklist)
        {
            await WriteWordsAsync(range.Address, [range.Value], cancellationToken).ConfigureAwait(false);
        }
    }

    private uint RegisterAddress(uint offset) => cqdmaBase + offset;

    private async Task<byte[]> PlainReadAsync(uint address, int words, CancellationToken cancellationToken)
    {
        var values = await access.ReadRegisterWordsAsync(address, words, cancellationToken).ConfigureAwait(false);
        var result = new byte[values.Length * 4];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(index * 4), values[index]);
        }

        return result;
    }

    private async Task WaitUntilIdleAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumTransferPolls; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await access.ReadRegisterAsync(RegisterAddress(EnableRegister), cancellationToken)
                .ConfigureAwait(false);
            if ((value & TransferArmed) == 0)
            {
                return;
            }
        }

        throw new MtkProtocolException(
            $"The CQDMA engine never cleared its busy bit after {MaximumTransferPolls} polls of register 0x{RegisterAddress(EnableRegister):X8}.");
    }
}