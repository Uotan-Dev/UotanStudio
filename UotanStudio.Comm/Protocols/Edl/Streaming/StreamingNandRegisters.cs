namespace UotanStudio.Comm.Protocols.Edl.Streaming;

/// <summary>
/// Register bit-math for the Qualcomm NAND controller used by the streaming loader: user-data, spare and ECC size fields, BCH reset, address setup, erase configuration, and bad-block and ECC status.
/// <para>流式 loader 所用 Qualcomm NAND 控制器的寄存器位运算：用户数据、spare 与 ECC 尺寸域、BCH 复位、地址设置、擦除配置，以及坏块与 ECC 状态。</para>
/// </summary>
public static class StreamingNandRegisters
{
    /// <summary>
    /// Applies the user-data size field of NAND_DEV0_CFG0: bits 9..18 are replaced with the new
    /// size while every other bit is preserved (set_udsize, streaming.py:567-570).
    /// <para>应用 NAND_DEV0_CFG0 的用户数据域：替换 9..18 位为新值，其余位保持不变
    /// （set_udsize，streaming.py:567-570）。</para>
    /// </summary>
    /// <param name="cfg0">The current NAND_DEV0_CFG0 value. <para>当前 NAND_DEV0_CFG0 值。</para></param>
    /// <param name="size">The user-data size in bytes. <para>用户数据大小（字节）。</para></param>
    /// <returns>The updated CFG0 value. <para>更新后的 CFG0 值。</para></returns>
    public static uint ApplyUdSize(uint cfg0, uint size)
        => (cfg0 & ~(0x3FFu << 9)) | (size << 9);

    /// <summary>
    /// Applies the user-data size to the ECC config, but only while BCH ECC is enabled.
    /// <para>仅在 BCH ECC 使能时把用户数据大小应用到 ECC 配置。</para>
    /// When NAND_DEV0_CFG1 bit 27 is set, the same size also replaces bits 16..25 of
    /// NAND_DEV0_ECC_CFG; otherwise the ECC config is returned untouched.
    /// <para>当 NAND_DEV0_CFG1 的位 27 为 1 时，同一大小还会替换 NAND_DEV0_ECC_CFG 的 16..25 位；
    /// 否则 ECC 配置原样返回。</para>
    /// </summary>
    /// <param name="cfg1">The current NAND_DEV0_CFG1 value. <para>当前 NAND_DEV0_CFG1 值。</para></param>
    /// <param name="eccCfg">The current NAND_DEV0_ECC_CFG value. <para>当前 NAND_DEV0_ECC_CFG 值。</para></param>
    /// <param name="size">The user-data size in bytes. <para>用户数据大小（字节）。</para></param>
    /// <returns>The updated ECC config. <para>更新后的 ECC 配置。</para></returns>
    public static uint ApplyUdSizeToEccCfg(uint cfg1, uint eccCfg, uint size)
        => UsesBchEcc(cfg1) ? (eccCfg & ~(0x3FFu << 16)) | (size << 16) : eccCfg;

    /// <summary>
    /// Reads the user-data size field back out of NAND_DEV0_CFG0 (get_udsize, streaming.py:606-607).
    /// <para>从 NAND_DEV0_CFG0 读回用户数据域（get_udsize，streaming.py:606-607）。</para>
    /// </summary>
    /// <param name="cfg0">The NAND_DEV0_CFG0 value. <para>NAND_DEV0_CFG0 值。</para></param>
    /// <returns>The user-data size in bytes. <para>用户数据大小（字节）。</para></returns>
    public static uint GetUdSize(uint cfg0) => (cfg0 & (0x3FFu << 9)) >> 9;

    /// <summary>
    /// Applies the spare-size field of NAND_DEV0_CFG0: bits 23..26 (set_sparesize,
    /// streaming.py:576-579).
    /// <para>应用 NAND_DEV0_CFG0 的 spare 域：23..26 位（set_sparesize，streaming.py:576-579）。</para>
    /// </summary>
    /// <param name="cfg0">The current NAND_DEV0_CFG0 value. <para>当前 NAND_DEV0_CFG0 值。</para></param>
    /// <param name="size">The spare size code. <para>spare 大小编码。</para></param>
    /// <returns>The updated CFG0 value. <para>更新后的 CFG0 值。</para></returns>
    public static uint ApplySpareSize(uint cfg0, uint size)
        => (cfg0 & ~(0xFu << 23)) | (size << 23);

    /// <summary>
    /// Tests NAND_DEV0_CFG1 bit 27, the BCH ECC enable flag.
    /// <para>检测 NAND_DEV0_CFG1 的位 27，即 BCH ECC 使能标志。</para>
    /// </summary>
    /// <param name="cfg1">The NAND_DEV0_CFG1 value. <para>NAND_DEV0_CFG1 值。</para></param>
    /// <returns><see langword="true"/> when BCH ECC is enabled.
    /// <para>BCH ECC 使能时为 <see langword="true"/>。</para></returns>
    public static bool UsesBchEcc(uint cfg1) => ((cfg1 >> 27) & 1) != 0;

    /// <summary>
    /// Applies the BCH branch of set_eccsize: bits 8..12 of NAND_DEV0_ECC_CFG carry the parity
    /// size.
    /// <para>应用 set_eccsize 的 BCH 分支：NAND_DEV0_ECC_CFG 的 8..12 位携带校验大小
    /// 。</para>
    /// </summary>
    /// <param name="eccCfg">The current NAND_DEV0_ECC_CFG value. <para>当前 NAND_DEV0_ECC_CFG 值。</para></param>
    /// <param name="size">The ECC parity size code. <para>ECC 校验大小编码。</para></param>
    /// <returns>The updated ECC config. <para>更新后的 ECC 配置。</para></returns>
    public static uint ApplyBchEccSize(uint eccCfg, uint size)
        => (eccCfg & ~(0x1Fu << 8)) | (size << 8);

    /// <summary>
    /// Applies the Reed-Solomon branch of set_eccsize: bits 19..22 of NAND_DEV0_CFG0 carry the
    /// parity size.
    /// <para>应用 set_eccsize 的 Reed-Solomon 分支：NAND_DEV0_CFG0 的 19..22 位携带校验大小
    /// 。</para>
    /// </summary>
    /// <param name="cfg0">The current NAND_DEV0_CFG0 value. <para>当前 NAND_DEV0_CFG0 值。</para></param>
    /// <param name="size">The ECC parity size code. <para>ECC 校验大小编码。</para></param>
    /// <returns>The updated CFG0 value. <para>更新后的 CFG0 值。</para></returns>
    public static uint ApplyRsEccSize(uint cfg0, uint size)
        => (cfg0 & ~(0xFu << 19)) | (size << 19);

    /// <summary>
    /// Produces the NAND_DEV0_ECC_CFG value of the bch_reset pulse: bit 1 (software ECC reset) is
    /// set for one write, after which the caller restores the original value
    ///.
    /// <para>生成 bch_reset 脉冲写入的 NAND_DEV0_ECC_CFG 值：位 1（软件 ECC 复位）置位一次写，
    /// 之后由调用方恢复原值。</para>
    /// </summary>
    /// <param name="eccCfg">The current NAND_DEV0_ECC_CFG value. <para>当前 NAND_DEV0_ECC_CFG 值。</para></param>
    /// <returns>The pulsed ECC config. <para>脉冲后的 ECC 配置。</para></returns>
    public static uint BchResetPulseValue(uint eccCfg) => eccCfg | 2u;

    /// <summary>
    /// Sets the ECC disable bit of one register value.
    /// <para>设置单个寄存器值的 ECC 禁用位。</para>
    /// Bit 0 becomes the disable flag (0 = ECC on, 1 = ECC off).
    /// <para>位 0 变为禁用标志（0 = ECC 开启，1 = ECC 关闭）。</para>
    /// </summary>
    /// <param name="register">The CFG1 or ECC_CFG value. <para>CFG1 或 ECC_CFG 值。</para></param>
    /// <param name="disable">1 to disable ECC, 0 to enable it. <para>1 禁用 ECC，0 启用。</para></param>
    /// <returns>The updated register value. <para>更新后的寄存器值。</para></returns>
    public static uint ApplyEccDisable(uint register, uint disable)
        => (register & 0xFFFFFFFEu) | disable;

    /// <summary>
    /// Applies the hardware bad-block disable mask to NAND_DEV0_CFG1: bits 6..15 are cleared
    /// (hardware_bad_off, streaming.py:550-553).
    /// <para>对 NAND_DEV0_CFG1 应用硬件坏块禁用掩码：清除 6..15 位（hardware_bad_off，
    /// streaming.py:550-553）。</para>
    /// </summary>
    /// <param name="cfg1">The current NAND_DEV0_CFG1 value. <para>当前 NAND_DEV0_CFG1 值。</para></param>
    /// <returns>The masked CFG1 value. <para>掩码后的 CFG1 值。</para></returns>
    public static uint DisableHardwareBadBlock(uint cfg1) => cfg1 & ~(0x3FFu << 6);

    /// <summary>
    /// Restores the hardware bad-block detection fields of NAND_DEV0_CFG1.
    /// <para>恢复 NAND_DEV0_CFG1 的硬件坏块检测域。</para>
    /// Bits 6..16 are cleared, the bad-block byte number is written into bits 6..15, and the
    /// in-spare-area flag into bit 16.
    /// <para>先清 6..16 位，把坏块字节号写入 6..15 位，spare 区内标志写入位 16。</para>
    /// </summary>
    /// <param name="cfg1">The current NAND_DEV0_CFG1 value. <para>当前 NAND_DEV0_CFG1 值。</para></param>
    /// <param name="badBlockByteNum">The bad-block marker byte position. <para>坏块标记字节位置。</para></param>
    /// <param name="badBlockInSpareArea">1 when the marker lives in the spare area, else 0.
    /// <para>标记位于 spare 区时为 1，否则为 0。</para></param>
    /// <returns>The updated CFG1 value. <para>更新后的 CFG1 值。</para></returns>
    public static uint EnableHardwareBadBlock(uint cfg1, uint badBlockByteNum, uint badBlockInSpareArea)
        => (cfg1 & ~(0x7FFu << 6)) | ((badBlockByteNum & 0x3FFu) << 6) | (badBlockInSpareArea << 16);

    /// <summary>
    /// Flattens a block and page pair into the controller's linear page address
    /// (set_address, streaming.py:377-378).
    /// <para>把块与页折叠为控制器的线性页地址（set_address，streaming.py:377-378）。</para>
    /// </summary>
    /// <param name="block">The block number. <para>块号。</para></param>
    /// <param name="page">The page number inside the block. <para>块内页号。</para></param>
    /// <param name="pagesPerBlock">The number of pages per block. <para>每块页数。</para></param>
    /// <returns>The linear page address. <para>线性页地址。</para></returns>
    /// <exception cref="OverflowException">Thrown when the linear address leaves the uint32
    /// space. <para>线性地址超出 uint32 空间时抛出。</para></exception>
    public static uint BuildPageAddress(uint block, uint page, uint pagesPerBlock)
        => checked((uint)((block * (ulong)pagesPerBlock) + page));

    /// <summary>
    /// Builds the NAND_ADDR0 value for a page: the linear address shifted left by 16
    ///.
    /// <para>构建某页的 NAND_ADDR0 值：线性地址左移 16 位。</para>
    /// </summary>
    /// <param name="block">The block number. <para>块号。</para></param>
    /// <param name="page">The page number inside the block. <para>块内页号。</para></param>
    /// <param name="pagesPerBlock">The number of pages per block. <para>每块页数。</para></param>
    /// <returns>The NAND_ADDR0 value. <para>NAND_ADDR0 值。</para></returns>
    public static uint PageAddress0(uint block, uint page, uint pagesPerBlock)
        => BuildPageAddress(block, page, pagesPerBlock) << 16;

    /// <summary>
    /// Builds the NAND_ADDR1 value for a page: bits 16..23 of the linear address
    ///.
    /// <para>构建某页的 NAND_ADDR1 值：线性地址的 16..23 位。</para>
    /// </summary>
    /// <param name="block">The block number. <para>块号。</para></param>
    /// <param name="page">The page number inside the block. <para>块内页号。</para></param>
    /// <param name="pagesPerBlock">The number of pages per block. <para>每块页数。</para></param>
    /// <returns>The NAND_ADDR1 value. <para>NAND_ADDR1 值。</para></returns>
    public static uint PageAddress1(uint block, uint page, uint pagesPerBlock)
        => (BuildPageAddress(block, page, pagesPerBlock) >> 16) & 0xFF;

    /// <summary>
    /// Builds the erase-phase NAND_ADDR0: the plain linear block start with
    /// no 16-bit shift.
    /// <para>构建擦除阶段的 NAND_ADDR0：写入不加 16 位移位的线性块起点。</para>
    /// </summary>
    /// <param name="block">The block number. <para>块号。</para></param>
    /// <param name="pagesPerBlock">The number of pages per block. <para>每块页数。</para></param>
    /// <returns>The erase NAND_ADDR0 value. <para>擦除用 NAND_ADDR0 值。</para></returns>
    public static uint EraseAddress0(uint block, uint pagesPerBlock)
        => checked((uint)(block * (ulong)pagesPerBlock));

    /// <summary>
    /// Clears the read-config bits the block-erase path masks out of NAND_DEV0_CFG0: bits 6..8
    ///.
    /// <para>清除块擦除路径从 NAND_DEV0_CFG0 掩掉的读配置位：6..8 位。</para>
    /// </summary>
    /// <param name="cfg0">The current NAND_DEV0_CFG0 value. <para>当前 NAND_DEV0_CFG0 值。</para></param>
    /// <returns>The erase-phase CFG0 value. <para>擦除阶段的 CFG0 值。</para></returns>
    public static uint BlockEraseCfg0(uint cfg0) => cfg0 & ~0x1C0u;

    /// <summary>
    /// Judges a block bad from NAND_BUFFER_STATUS.
    /// <para>依据 NAND_BUFFER_STATUS 判定坏块。</para>
    /// The upper 16 status bits must all read 0xFF (0xFF in the byte pair 0xFF0000) for a narrow
    /// flash, or 0xFFFF0000 for a wide flash.
    /// <para>窄闪存的低两字节状态必须全为 0xFF（即 0xFF0000 掩码相等），宽闪存则为 0xFFFF0000。</para>
    /// </summary>
    /// <param name="bufferStatus">The raw NAND_BUFFER_STATUS value. <para>原始 NAND_BUFFER_STATUS 值。</para></param>
    /// <param name="isWideFlash"><see langword="true"/> for x16 bus flash.
    /// <para>x16 总线闪存时为 <see langword="true"/>。</para></param>
    /// <returns><see langword="true"/> when the block is bad.
    /// <para>该块为坏块时为 <see langword="true"/>。</para></returns>
    public static bool IsBadBlock(uint bufferStatus, bool isWideFlash)
    {
        var status = bufferStatus & 0xFFFF0000;
        return isWideFlash ? status != 0xFFFF0000 : status != 0xFF0000;
    }

    /// <summary>
    /// Evaluates the ECC verdict after a codeword read.
    /// <para>评估一次码字读后的 ECC 结论。</para>
    /// When the buffer-error bit is set and the ECC-corrected bit of the command-register window
    /// reads clear, the read is an uncorrectable error (-1); otherwise the corrected-bit count is the
    /// low five bits. The window test <c>(NAND_FLASH_CMD + 0xEC) &amp; 0x40</c> feeds the word read
    /// back from <c>NAND_FLASH_CMD</c>, because the <c>nandregs</c> attribute is itself a mempeek; the
    /// operand is therefore that peeked value, not an address.
    /// <para>当缓冲区错误位为 1 且命令寄存器窗口中 ECC 已纠正位读到 0 时为不可纠错（-1），否则低 5 位
    /// 是纠正位数。窗口测试 <c>(NAND_FLASH_CMD + 0xEC) &amp; 0x40</c> 用的是从 <c>NAND_FLASH_CMD</c>
    /// 读回的那个字，因为 <c>nandregs</c> 属性本身就是一次 mempeek，所以这里的入参是该读回值而不是地址。</para>
    /// </summary>
    /// <param name="bufferStatus">The raw NAND_BUFFER_STATUS value. <para>原始 NAND_BUFFER_STATUS 值。</para></param>
    /// <param name="flashCommandRegisterValue">The word read back from NAND_FLASH_CMD.
    /// <para>从 NAND_FLASH_CMD 读回的字。</para></param>
    /// <returns>-1 for an uncorrectable error, else the corrected-bit count.
    /// <para>不可纠错为 -1，否则为纠正位数。</para></returns>
    public static int EvaluateEccStatus(uint bufferStatus, uint flashCommandRegisterValue)
    {
        if ((bufferStatus & 0x100u) != 0 && unchecked(((flashCommandRegisterValue + 0xECu) & 0x40u)) == 0)
        {
            return -1;
        }

        return (int)(bufferStatus & 0x1Fu);
    }
}