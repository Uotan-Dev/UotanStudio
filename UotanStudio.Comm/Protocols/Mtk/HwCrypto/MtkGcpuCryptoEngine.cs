using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk.HwCrypto;

/// <summary>
/// The MediaTek GCPU hardware-crypto engine: its register interface, the AES-CBC block sequence the Amonet family
/// drives, and the range-blacklist lift that family asks for first.
/// <para>联发科 GCPU 硬件加密引擎：寄存器接口、Amonet 家族驱动的 AES-CBC 分组序列，以及该家族首先要求的范围黑名单解除。</para>
/// </summary>
/// <remarks>
/// <para>Every register offset is the <c>regval</c> dictionary of
/// <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c>, lines 99-140; the AES command words are lines 50-51 of the
/// same file (<c>AESPK_ECBC</c> = 0x7D, <c>AESPK_EK_DCBC</c> = 0x7E). Register names in the reference resolve to a
/// single download-agent access (its <c>GCpuReg.__setattr__</c> and <c>__getattribute__</c> at lines 151-167 wrap
/// <c>write32</c> and <c>read32</c>), and the reference hands those accessors Python lists where a scalar is
/// needed, so a register read here resolves to the one word the reference asks for and a register write of an
/// <c>[x]</c> literal to the same single word.</para>
/// <para>所有寄存器偏移取自 <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c> 第 99-140 行的 <c>regval</c> 字典；AES
/// 命令字取自同文件第 50-51 行（<c>AESPK_ECBC</c> = 0x7D、<c>AESPK_EK_DCBC</c> = 0x7E）。参考实现的寄存器名会解析为单次
/// 下载代理访问（第 151-167 行的 <c>GCpuReg.__setattr__</c> 与 <c>__getattribute__</c> 分别包装 <c>write32</c> 与
/// <c>read32</c>），并且它把列表传给了需要标量的访问器；因此这里的寄存器读取解析为参考实现索取的那一个字，写入
/// <c>[x]</c> 字面量也解析为同一个字。</para>
/// </remarks>
internal sealed class MtkGcpuCryptoEngine(ushort hardwareCode, uint gcpuBase,
    IReadOnlyList<MtkBlacklistRange> blacklist, MtkDaRegisterAccess access)
{
    /// <summary>The AES block width the engine moves per command. <para>引擎每条命令搬运的 AES 分组宽度。</para></summary>
    /// <value>16 bytes (<c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c>, line 418 and line 442, where every
    /// <c>aes_cbc</c> call passes <c>length=16</c>). <para>16 字节（
    /// <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c> 第 418 与 442 行，每个 <c>aes_cbc</c> 调用都传入
    /// <c>length=16</c>）。</para></value>
    internal const int BlockBytes = 16;

    private const uint ControlRegister = 0x000;
    private const uint ModeSelectRegister = 0x004;
    private const uint AxciRegister = 0x020;
    private const uint AuxiliaryRegister = 0x024;
    private const uint ProgramCounterRegister = 0x400;
    private const uint MemoryAddressRegister = 0x404;
    private const uint InterruptStatusRegister = 0x800;
    private const uint InterruptClearRegister = 0x804;
    private const uint InterruptEnableRegister = 0x808;
    private const uint CommandRegister = 0xC00;
    private const uint ParameterZeroRegister = 0xC04;
    private const uint ParameterOneRegister = 0xC08;
    private const uint ParameterTwoRegister = 0xC0C;
    private const uint ParameterFourRegister = 0xC14;
    private const uint ParameterFiveRegister = 0xC18;
    private const uint ParameterSixRegister = 0xC1C;
    private const uint ParameterTenRegister = 0xC2C;
    private const uint DramMonitorRegister = 0x418;
    private const uint ClockGatingControlTwo = 0x1000009C;
    private const uint GcpuClockEnableBit = 0x08000000;
    private const uint AesCbcEncryptCommand = 0x7D;
    private const uint AesCbcDecryptCommand = 0x7E;
    private const int KeySlot = 0x12;
    private const int SeedSlot = 0x16;
    private const int InitializationVectorSlot = 0x1A;
    private const int SlotClearWords = 4;
    private const int InitializationVectorClearWords = 8;
    private const uint BlacklistControlWord = 0x80;
    private const uint ZeroSourceAddress = 0xD848;
    private const uint InterruptPollMask = 0x1;
    private const uint InterruptErrorMask = 0x2;
    private const uint DoneMask = 0x1;
    private const int MaximumCommandPolls = 0x400;

    /// <summary>The pattern the reference XORs the block with before it writes an initialization-vector slot.
    /// <para>参考实现在写入初始化向量槽之前与分组异或的固定模式。</para></summary>
    /// <value>The bytes of the reference default <c>iv</c> hex string
    /// <c>4dd12bdf0ec7d26c482490b3482a1b1f</c>
    /// (<c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c>, lines 430-431).
    /// <para>即参考实现默认 <c>iv</c> 十六进制串 <c>4dd12bdf0ec7d26c482490b3482a1b1f</c> 的字节（
    /// <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c> 第 430-431 行）。</para></value>
    internal static ReadOnlySpan<byte> DefaultInitializationVector =>
        [0x4D, 0xD1, 0x2B, 0xDF, 0x0E, 0xC7, 0xD2, 0x6C, 0x48, 0x24, 0x90, 0xB3, 0x48, 0x2A, 0x1B, 0x1F];

    /// <summary>Clears the AES parameter registers and the secure-slot contents.
    /// <para>清空 AES 参数寄存器与安全槽内容。</para></summary>
    /// <param name="cancellationToken">Cancellation for the sequence. <para>序列的取消令牌。</para></param>
    /// <exception cref="MtkProtocolException">The download agent refused one of the register writes.
    /// <para>下载代理拒绝其中某次寄存器写入。</para></exception>
    /// <remarks>
    /// <para>Transcription of <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c>, lines 218-232: parameter
    /// registers two through ten go to zero, then the key slot at <c>0x12</c>, the seed slot at word 22 and the
    /// initialization-vector slot at <c>0x1A</c> are cleared in the secure-slot window.</para>
    /// <para>转录 <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c> 第 218-232 行：把第二到第十号参数寄存器清零，随后在
    /// 安全槽窗口内清除偏移 <c>0x12</c> 的密钥槽、第 22 个字的种子槽与偏移 <c>0x1A</c> 的初始化向量槽。</para>
    /// </remarks>
    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        for (var offset = ParameterTwoRegister; offset <= ParameterTenRegister; offset += 4)
        {
            await access.WriteRegisterAsync(RegisterAddress(offset), 0, cancellationToken).ConfigureAwait(false);
        }

        await access.WriteRegisterWordsAsync(SlotAddress(KeySlot), new uint[SlotClearWords], cancellationToken)
            .ConfigureAwait(false);
        await access.WriteRegisterWordsAsync(SlotAddress(SeedSlot), new uint[SlotClearWords], cancellationToken)
            .ConfigureAwait(false);
        await access.WriteRegisterWordsAsync(SlotAddress(InitializationVectorSlot),
            new uint[InitializationVectorClearWords], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes ownership of the engine for the chip's recorded bring-up sequence.
    /// <para>按该芯片记录的启动序列取得引擎所有权。</para></summary>
    /// <param name="cancellationToken">Cancellation for the sequence. <para>序列的取消令牌。</para></param>
    /// <exception cref="MtkProtocolException">The download agent refused one of the register accesses.
    /// <para>下载代理拒绝其中某次寄存器访问。</para></exception>
    /// <remarks>
    /// <para>Transcription of <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c>, lines 248-317. The reference's
    /// fourth branch compares <c>self.hwcode</c> against the list literal <c>[0x8163, 0x8176]</c> (line 260), so it
    /// can never be taken and those chips fall through to the generic sequence; the same fall-through is kept here
    /// and its unreachable instruction array is not transcribed. The MT8167 branch also clears the clock gate at
    /// <c>CKSYS_BASE + 0x9C</c> (lines 141-142 and 249) and the MT8172/MT6572 branch releases first
    /// (lines 261-263).</para>
    /// <para>转录 <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c> 第 248-317 行。参考实现的第四个分支把
    /// <c>self.hwcode</c> 与列表字面量 <c>[0x8163, 0x8176]</c> 比较（第 260 行），因此永远无法命中，这些芯片落到通用序列；
    /// 此处保留同样的落空行为，并且不转录那段不可达的指令数组。MT8167 分支还会清除 <c>CKSYS_BASE + 0x9C</c> 处的时钟门控
    /// （第 141-142 与 249 行），MT8172/MT6572 分支会先释放引擎（第 261-263 行）。</para>
    /// </remarks>
    internal async Task AcquireAsync(CancellationToken cancellationToken)
    {
        switch (hardwareCode)
        {
            case 0x8167:
                var gated = await access.ReadRegisterAsync(ClockGatingControlTwo, cancellationToken)
                    .ConfigureAwait(false);
                await access.WriteRegisterAsync(ClockGatingControlTwo, gated | GcpuClockEnableBit, cancellationToken)
                    .ConfigureAwait(false);
                await OrValueAsync(ControlRegister, 0xF, cancellationToken).ConfigureAwait(false);
                await AndOrValueAsync(ModeSelectRegister, 0x7FF0BF7F, 0x34080, cancellationToken)
                    .ConfigureAwait(false);
                await OrValueAsync(ControlRegister, 0x1F, cancellationToken).ConfigureAwait(false);
                await OrValueAsync(ModeSelectRegister, 0x2000, cancellationToken).ConfigureAwait(false);
                await access.WriteRegisterAsync(RegisterAddress(AxciRegister), 0x885B, cancellationToken)
                    .ConfigureAwait(false);
                await AndValueAsync(AuxiliaryRegister, 0xFFFDFFFD, cancellationToken).ConfigureAwait(false);
                await access.WriteRegisterAsync(RegisterAddress(MemoryAddressRegister), 0x80002000, cancellationToken)
                    .ConfigureAwait(false);
                await access.WriteRegisterAsync(RegisterAddress(InterruptClearRegister), 1, cancellationToken)
                    .ConfigureAwait(false);
                await access.WriteRegisterAsync(RegisterAddress(InterruptEnableRegister), 0, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case 0x8172 or 0x8127:
                await ReleaseAsync(cancellationToken).ConfigureAwait(false);
                await AndValueAsync(ModeSelectRegister, 0xFFFFDFFF, cancellationToken).ConfigureAwait(false);
                break;
            case 0x335:
                var masked = await access.ReadRegisterAsync(RegisterAddress(ModeSelectRegister), cancellationToken)
                    .ConfigureAwait(false);
                await access.WriteRegisterAsync(RegisterAddress(ControlRegister), masked & 0xFFFFDFFF, cancellationToken)
                    .ConfigureAwait(false);
                await OrValueAsync(ControlRegister, 7, cancellationToken).ConfigureAwait(false);
                await access.WriteRegisterAsync(RegisterAddress(ModeSelectRegister), 0x80FF1800, cancellationToken)
                    .ConfigureAwait(false);
                await access.WriteRegisterAsync(RegisterAddress(AxciRegister), 0x887F, cancellationToken)
                    .ConfigureAwait(false);
                await access.WriteRegisterAsync(RegisterAddress(AuxiliaryRegister), 0, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                await AndValueAsync(ControlRegister, 0xFFFFFFF0, cancellationToken).ConfigureAwait(false);
                await OrValueAsync(ControlRegister, 0xF, cancellationToken).ConfigureAwait(false);
                await OrValueAsync(ModeSelectRegister, 0x10000, cancellationToken).ConfigureAwait(false);
                await AndValueAsync(ControlRegister, 0xFFFFFFE0, cancellationToken).ConfigureAwait(false);
                await OrValueAsync(ModeSelectRegister, 0x10000, cancellationToken).ConfigureAwait(false);
                await OrValueAsync(ControlRegister, 0x1F, cancellationToken).ConfigureAwait(false);
                await OrValueAsync(ModeSelectRegister, 0x2000, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Releases the engine back to the boot code for the chips whose reference driver does so.
    /// <para>对参考驱动确实执行释放的芯片，把引擎归还给启动代码。</para></summary>
    /// <param name="cancellationToken">Cancellation for the sequence. <para>序列的取消令牌。</para></param>
    /// <exception cref="MtkProtocolException">The download agent refused one of the register accesses.
    /// <para>下载代理拒绝其中某次寄存器访问。</para></exception>
    /// <remarks>
    /// <para>Transcription of <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c>, lines 319-323: the reference
    /// releases only for hardware codes 0x8172, 0x8127 and the null chip, so every other code leaves the control
    /// register untouched.</para>
    /// <para>转录 <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c> 第 319-323 行：参考实现只在硬件码 0x8172、0x8127
    /// 与空芯片时释放，因此其他硬件码都不会改动控制寄存器。</para>
    /// </remarks>
    internal async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (hardwareCode is not (0x8172 or 0x8127))
        {
            return;
        }

        await AndValueAsync(ControlRegister, 0xFFFFFFF0, cancellationToken).ConfigureAwait(false);
        await OrValueAsync(ControlRegister, 0xF, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one AES-CBC block of device memory through the engine.
    /// <para>通过引擎读取一个 AES-CBC 分组的设备内存。</para></summary>
    /// <param name="address">The source address; the reference reads exactly one block per call.
    /// <para>源地址；参考实现每次调用只读取一个分组。</para></param>
    /// <param name="cancellationToken">Cancellation for the sequence. <para>序列的取消令牌。</para></param>
    /// <exception cref="MtkProtocolException">The engine reported an error interrupt, never signalled the command,
    /// or the download agent refused a register access. <para>引擎上报错误中断、命令始终未发出信号，或下载代理拒绝某次
    /// 寄存器访问。</para></exception>
    /// <returns>The sixteen bytes the engine left in the initialization-vector slot.
    /// <para>引擎留在初始化向量槽中的十六个字节。</para></returns>
    /// <remarks>
    /// <para>Transcription of <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c>, lines 417-424: the block is DMAed
    /// with the destination at zero and the key/IV slots at <c>0x12</c> and <c>0x1A</c>, then the result is read
    /// back from the slot window and stored word by word in little-endian order.
    /// <c>mtkclient/Library/Exploit/amonet.py</c>, lines 26-32 and 129-135, is the caller that walks this
    /// primitive in sixteen-byte steps.</para>
    /// <para>转录 <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c> 第 417-424 行：以目的地址 0、密钥/初始化向量槽
    /// <c>0x12</c> 与 <c>0x1A</c> 执行 DMA，然后从安全槽窗口读回结果，并按小端逐字排列。
    /// <c>mtkclient/Library/Exploit/amonet.py</c> 第 26-32 与 129-135 行就是按 16 字节步进调用该原语的调用方。</para>
    /// </remarks>
    internal async Task<byte[]> ReadBlockAsync(uint address, CancellationToken cancellationToken)
    {
        await AesCbcAsync(false, address, 0, BlockBytes, KeySlot, InitializationVectorSlot, cancellationToken)
            .ConfigureAwait(false);
        var words = await access.ReadRegisterWordsAsync(SlotAddress(InitializationVectorSlot),
            MtkDaRegisterAccess.MaximumRegisterReadWords, cancellationToken).ConfigureAwait(false);
        var block = new byte[MtkDaRegisterAccess.MaximumRegisterReadWords * 4];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(index * 4), words[index]);
        }

        return block;
    }

    /// <summary>Stores one AES-CBC block at a device address through the engine.
    /// <para>通过引擎在某个设备地址存储一个 AES-CBC 分组。</para></summary>
    /// <param name="address">The destination address, which for the blacklist lift is the blacklist register
    /// itself. <para>目的地址；解除黑名单时该地址就是黑名单寄存器本身。</para></param>
    /// <param name="block">Exactly sixteen bytes; the reference raises before framing anything else.
    /// <para>必须正好十六个字节；参考实现在发出其他内容之前就会抛出。</para></param>
    /// <param name="cancellationToken">Cancellation for the sequence. <para>序列的取消令牌。</para></param>
    /// <exception cref="ArgumentException"><paramref name="block"/> is not sixteen bytes long.
    /// <para><paramref name="block"/> 长度不是十六字节。</para></exception>
    /// <exception cref="MtkProtocolException">The engine reported an error interrupt, never signalled the command,
    /// or the download agent refused a register access. <para>引擎上报错误中断、命令始终未发出信号，或下载代理拒绝某次
    /// 寄存器访问。</para></exception>
    /// <remarks>
    /// <para>Transcription of <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c>, lines 426-455: the block is
    /// XORed with the recorded default pattern, the key and seed slots are cleared, the initialization-vector slot
    /// is cleared and then programmed with the masked words, and the engine performs a chained decryption from a
    /// source that the reference keeps at zero except for hardware code 0x8172, where it names the all-zero page
    /// at <c>0xD848</c> (lines 452-454).</para>
    /// <para>转录 <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c> 第 426-455 行：先把分组与记录的默认模式异或，清除密钥
    /// 槽与种子槽，再清除初始化向量槽并写入掩蔽后的字，最后由引擎执行链接解密；参考实现的源地址保持为 0，只有硬件码
    /// 0x8172 例外，改为全零页 <c>0xD848</c>（第 452-454 行）。</para>
    /// </remarks>
    internal async Task WriteBlockAsync(uint address, ReadOnlyMemory<byte> block, CancellationToken cancellationToken)
    {
        if (block.Length != BlockBytes)
        {
            throw new ArgumentException("The AES-CBC block must be exactly sixteen bytes.", nameof(block));
        }

        var masked = new uint[BlockBytes / 4];
        for (var index = 0; index < masked.Length; index++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(block.Span[(index * 4)..]);
            var pattern = BinaryPrimitives.ReadUInt32LittleEndian(
                DefaultInitializationVector[(index * 4)..(index * 4 + 4)]);
            masked[index] = value ^ pattern;
        }

        await access.WriteRegisterWordsAsync(SlotAddress(KeySlot), new uint[SlotClearWords], cancellationToken)
            .ConfigureAwait(false);
        await access.WriteRegisterWordsAsync(SlotAddress(SeedSlot), new uint[SlotClearWords], cancellationToken)
            .ConfigureAwait(false);
        await access.WriteRegisterWordsAsync(SlotAddress(InitializationVectorSlot),
            new uint[InitializationVectorClearWords], cancellationToken).ConfigureAwait(false);
        await access.WriteRegisterWordsAsync(SlotAddress(InitializationVectorSlot), masked, cancellationToken)
            .ConfigureAwait(false);
        var source = hardwareCode == 0x8172 ? ZeroSourceAddress : 0u;
        await AesCbcAsync(false, source, address, BlockBytes, KeySlot, InitializationVectorSlot, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads a device range as consecutive AES-CBC blocks.
    /// <para>以连续 AES-CBC 分组读取一段设备范围。</para></summary>
    /// <param name="address">The first address to read. <para>起始读取地址。</para></param>
    /// <param name="length">The byte count; the last block is full, exactly like the reference loops.
    /// <para>字节数；最后一个分组仍是完整分组，与参考循环一致。</para></param>
    /// <param name="cancellationToken">Cancellation for the sequence. <para>序列的取消令牌。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is empty or wider than
    /// <see cref="MtkDaRegisterAccess.MaximumTransferBytes"/>, or the range leaves the device address space.
    /// <para><paramref name="length"/> 为空或超过
    /// <see cref="MtkDaRegisterAccess.MaximumTransferBytes"/>，或该范围越出设备地址空间。</para></exception>
    /// <exception cref="MtkProtocolException">One of the blocks failed. <para>某个分组执行失败。</para></exception>
    /// <returns>One full block per sixteen-byte step, so a length that is not a multiple of sixteen yields the
    /// trailing block too; the reference appends every block it reads
    /// (<c>mtkclient/Library/Exploit/amonet.py</c>, lines 26-33).
    /// <para>每个 16 字节步进返回一个完整分组，因此不是十六倍数的长度也会带上最后一个分组；参考实现会追加它读到的每个分组
    /// （<c>mtkclient/Library/Exploit/amonet.py</c> 第 26-33 行）。</para></returns>
    internal async Task<byte[]> ReadAsync(uint address, int length, CancellationToken cancellationToken)
    {
        MtkDaRegisterAccess.ThrowUnlessTransferInBounds(length);
        MtkDaRegisterAccess.ThrowUnlessRangeInAddressSpace(address, length);
        var blocks = (length + (BlockBytes - 1)) / BlockBytes;
        var result = new byte[blocks * BlockBytes];
        for (var block = 0; block < blocks; block++)
        {
            var step = address + (uint)(block * BlockBytes);
            var contents = await ReadBlockAsync(step, cancellationToken).ConfigureAwait(false);
            contents.CopyTo(result, block * BlockBytes);
        }

        return result;
    }

    /// <summary>Lifts every blacklist range the chip entry records.
    /// <para>解除芯片条目记录的每一条黑名单范围。</para></summary>
    /// <param name="cancellationToken">Cancellation for the sequence. <para>序列的取消令牌。</para></param>
    /// <exception cref="InvalidOperationException">The chip entry records no ranges.
    /// <para>芯片条目没有记录任何范围。</para></exception>
    /// <exception cref="MtkProtocolException">One of the ranges failed to program.
    /// <para>某条范围编程失败。</para></exception>
    /// <remarks>
    /// <para>Transcription of <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c>, lines 634-644: each recorded
    /// scalar becomes the four little-endian words <c>(value, 0, 0, 0x80)</c>, which the engine then writes through
    /// the AES-CBC block path at the blacklist register itself.</para>
    /// <para>转录 <c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c> 第 634-644 行：每条记录的标量被展开成四个小端字
    /// <c>(value, 0, 0, 0x80)</c>，随后引擎通过 AES-CBC 分组路径把它们写入黑名单寄存器本身。</para>
    /// </remarks>
    internal async Task DisableRangeBlacklistAsync(CancellationToken cancellationToken)
    {
        if (blacklist.Count == 0)
        {
            throw new InvalidOperationException(
                "The GCPU engine has no blacklist range to lift for this chip entry.");
        }

        foreach (var range in blacklist)
        {
            var block = new byte[BlockBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(block, range.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(block.Length - 4), BlacklistControlWord);
            await WriteBlockAsync(range.Address, block, cancellationToken).ConfigureAwait(false);
        }
    }

    private uint RegisterAddress(uint offset) => gcpuBase + offset;

    private uint SlotAddress(int slot) => gcpuBase + CommandRegister + ((uint)slot * 4);

    private async Task OrValueAsync(uint offset, uint mask, CancellationToken cancellationToken)
    {
        var address = RegisterAddress(offset);
        var value = await access.ReadRegisterAsync(address, cancellationToken).ConfigureAwait(false);
        await access.WriteRegisterAsync(address, value | mask, cancellationToken).ConfigureAwait(false);
    }

    private async Task AndValueAsync(uint offset, uint mask, CancellationToken cancellationToken)
    {
        var address = RegisterAddress(offset);
        var value = await access.ReadRegisterAsync(address, cancellationToken).ConfigureAwait(false);
        await access.WriteRegisterAsync(address, value & mask, cancellationToken).ConfigureAwait(false);
    }

    private async Task AndOrValueAsync(uint offset, uint andMask, uint orMask, CancellationToken cancellationToken)
    {
        var address = RegisterAddress(offset);
        var value = await access.ReadRegisterAsync(address, cancellationToken).ConfigureAwait(false);
        await access.WriteRegisterAsync(address, (value & andMask) | orMask, cancellationToken).ConfigureAwait(false);
    }

    private async Task AesCbcAsync(bool encrypt, uint source, uint destination, int length, int keySlot, int ivSlot,
        CancellationToken cancellationToken)
    {
        var blocks = (length + (BlockBytes - 1)) / BlockBytes;
        await access.WriteRegisterAsync(RegisterAddress(ParameterZeroRegister), source, cancellationToken)
            .ConfigureAwait(false);
        await access.WriteRegisterAsync(RegisterAddress(ParameterOneRegister), destination, cancellationToken)
            .ConfigureAwait(false);
        await access.WriteRegisterAsync(RegisterAddress(ParameterTwoRegister), (uint)blocks, cancellationToken)
            .ConfigureAwait(false);
        await access.WriteRegisterAsync(RegisterAddress(ParameterFourRegister), (uint)keySlot, cancellationToken)
            .ConfigureAwait(false);
        await access.WriteRegisterAsync(RegisterAddress(ParameterFiveRegister), (uint)ivSlot, cancellationToken)
            .ConfigureAwait(false);
        await access.WriteRegisterAsync(RegisterAddress(ParameterSixRegister), (uint)ivSlot, cancellationToken)
            .ConfigureAwait(false);
        var command = encrypt ? AesCbcEncryptCommand : AesCbcDecryptCommand;
        if (await CommandAsync(command, 0, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new MtkProtocolException(
                $"The GCPU engine reported an error interrupt for the AES-CBC command 0x{command:X2}; no block was transferred.");
        }
    }

    private async Task<int> CommandAsync(uint command, uint programCounter, CancellationToken cancellationToken)
    {
        if (hardwareCode == 0x8167)
        {
            await access.WriteRegisterAsync(RegisterAddress(InterruptClearRegister), 1, cancellationToken)
                .ConfigureAwait(false);
            await access.WriteRegisterAsync(RegisterAddress(InterruptEnableRegister), 0, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await access.WriteRegisterAsync(RegisterAddress(InterruptClearRegister), 3, cancellationToken)
                .ConfigureAwait(false);
            await access.WriteRegisterAsync(RegisterAddress(InterruptEnableRegister), 3, cancellationToken)
                .ConfigureAwait(false);
        }

        await access.WriteRegisterAsync(RegisterAddress(CommandRegister), command, cancellationToken)
            .ConfigureAwait(false);
        await access.WriteRegisterAsync(RegisterAddress(ProgramCounterRegister), programCounter, cancellationToken)
            .ConfigureAwait(false);
        var status = await PollAsync(InterruptStatusRegister, value => (value & InterruptPollMask) != 0, command,
            cancellationToken).ConfigureAwait(false);
        if ((status & InterruptErrorMask) != 0)
        {
            if ((status & InterruptPollMask) == 0)
            {
                await PollAsync(InterruptStatusRegister, value => (value & InterruptPollMask) != 0, command,
                    cancellationToken).ConfigureAwait(false);
            }

            await access.WriteRegisterAsync(RegisterAddress(InterruptClearRegister), 3, cancellationToken)
                .ConfigureAwait(false);
            return -1;
        }

        await PollAsync(DramMonitorRegister, value => (value & DoneMask) != 0, command, cancellationToken)
            .ConfigureAwait(false);
        await access.WriteRegisterAsync(RegisterAddress(InterruptClearRegister), 3, cancellationToken)
            .ConfigureAwait(false);
        return 0;
    }

    private async Task<uint> PollAsync(uint register, Func<uint, bool> isDone, uint command,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumCommandPolls; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await access.ReadRegisterAsync(RegisterAddress(register), cancellationToken)
                .ConfigureAwait(false);
            if (isDone(value))
            {
                return value;
            }
        }

        throw new MtkProtocolException(
            $"The GCPU engine did not report a finished command 0x{command:X2} within {MaximumCommandPolls} polls of register 0x{RegisterAddress(register):X8}.");
    }
}