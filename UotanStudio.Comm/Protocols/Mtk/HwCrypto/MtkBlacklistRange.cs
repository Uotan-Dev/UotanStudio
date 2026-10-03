namespace UotanStudio.Comm.Protocols.Mtk.HwCrypto;

/// <summary>
/// One protected-range blacklist register recorded for a MediaTek chip: the register address and the single word
/// the reference table stores for it.
/// <para>联发科芯片记录的一条受保护范围黑名单寄存器：寄存器地址，以及参考表为该地址存储的单个字。</para>
/// </summary>
/// <remarks>
/// <para>Every entry of the reference <c>blacklist</c> list is the pair <c>(address, 0)</c> except the MT6781
/// entry, which stores a second register address as its word
/// (<c>mtkclient/config/brom_config.py</c>, the <c>0x1066</c> entry at lines 1108-1109:
/// <c>blacklist=[(0x10284C, 0x106B54)]</c>). Both hardware-crypto engines consume exactly this scalar; the GCPU
/// engine widens it to four words (<c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c>, lines 636-644) and the
/// CQDMA engine writes it as a single word (<c>mtkclient/Library/Hardware/cqdma.py</c>, lines 124-131).</para>
/// <para>参考实现 <c>blacklist</c> 列表的每一条都是 <c>(address, 0)</c>，只有 MT6781 条目把第二个寄存器地址当作字存储
/// （<c>mtkclient/config/brom_config.py</c> 第 1108-1109 行的 <c>0x1066</c> 条目：
/// <c>blacklist=[(0x10284C, 0x106B54)]</c>）。两个硬件加密引擎都只消费这个标量：GCPU 引擎把它扩展成四个字
/// （<c>mtkclient/Library/Hardware/hwcrypto_gcpu.py</c> 第 636-644 行），CQDMA 引擎把它作为单个字写入
/// （<c>mtkclient/Library/Hardware/cqdma.py</c> 第 124-131 行）。</para>
/// </remarks>
/// <param name="Address">The blacklist register address, copied from the reference chip entry.
/// <para>黑名单寄存器地址，直接复制自参考芯片条目。</para></param>
/// <param name="Value">The recorded word for that register. <para>该寄存器记录的字。</para></param>
public readonly record struct MtkBlacklistRange(uint Address, uint Value);