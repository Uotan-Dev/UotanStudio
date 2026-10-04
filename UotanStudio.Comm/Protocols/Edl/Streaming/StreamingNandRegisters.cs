namespace UotanStudio.Comm.Protocols.Edl.Streaming;

/// <summary>
/// Pure register bit-math for the Qualcomm NAND controller used by the streaming loader,
/// transcribed from <c>edlclient/Library/streaming.py</c> (hardware_bad_off at streaming.py:550-553,
/// hardware_bad_on at streaming.py:555-560, set_udsize at streaming.py:567-574, set_sparesize at
/// streaming.py:576-579, set_eccsize at streaming.py:581-592, bch_reset at streaming.py:594-599,
/// get_udsize at streaming.py:606-607, set_address at streaming.py:377-380, block_erase address
/// and config at streaming.py:609-619, tst_badblock at streaming.py:391-399, check_ecc_status at
/// streaming.py:442-446) and the register bit positions of
/// <c>edlclient/Library/nand_config.py</c> (NandDevice at nand_config.py:352-451).
/// <para>流式 loader 所用 Qualcomm NAND 控制器的纯寄存器位运算，转写自
/// <c>edlclient/Library/streaming.py</c>（hardware_bad_off 见 streaming.py:550-553，
/// hardware_bad_on 见 streaming.py:555-560，set_udsize 见 streaming.py:567-574，set_sparesize 见
/// streaming.py:576-579，set_eccsize 见 streaming.py:581-592，bch_reset 见 streaming.py:594-599，
/// get_udsize 见 streaming.py:606-607，set_address 见 streaming.py:377-380，block_erase 的地址与
/// 配置见 streaming.py:609-619，tst_badblock 见 streaming.py:391-399，check_ecc_status 见
/// streaming.py:442-446）以及 <c>edlclient/Library/nand_config.py</c> 的寄存器位域
/// （NandDevice 见 nand_config.py:352-451）。</para>
/// </summary>
internal static class StreamingNandRegisters
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
    internal static uint ApplyUdSize(uint cfg0, uint size)
        => (cfg0 & ~(0x3FFu << 9)) | (size << 9);

    /// <summary>
    /// Mirrors the conditional half of set_udsize: when NAND_DEV0_CFG1 bit 27 (BCH ECC enabled)
    /// is set, the same size also replaces bits 16..25 of NAND_DEV0_ECC_CFG; otherwise the ECC
    /// config is returned untouched (streaming.py:571-574).
    /// <para>对应 set_udsize 的条件半边：当 NAND_DEV0_CFG1 的位 27（BCH ECC 使能）为 1 时，同一
    /// 大小还会替换 NAND_DEV0_ECC_CFG 的 16..25 位；否则 ECC 配置原样返回
    /// （streaming.py:571-574）。</para>
    /// </summary>
    /// <param name="cfg1">The current NAND_DEV0_CFG1 value. <para>当前 NAND_DEV0_CFG1 值。</para></param>
    /// <param name="eccCfg">The current NAND_DEV0_ECC_CFG value. <para>当前 NAND_DEV0_ECC_CFG 值。</para></param>
    /// <param name="size">The user-data size in bytes. <para>用户数据大小（字节）。</para></param>
    /// <returns>The updated ECC config. <para>更新后的 ECC 配置。</para></returns>
    internal static uint ApplyUdSizeToEccCfg(uint cfg1, uint eccCfg, uint size)
        => UsesBchEcc(cfg1) ? (eccCfg & ~(0x3FFu << 16)) | (size << 16) : eccCfg;

    /// <summary>
    /// Reads the user-data size field back out of NAND_DEV0_CFG0 (get_udsize, streaming.py:606-607).
    /// <para>从 NAND_DEV0_CFG0 读回用户数据域（get_udsize，streaming.py:606-607）。</para>
    /// </summary>
    /// <param name="cfg0">The NAND_DEV0_CFG0 value. <para>NAND_DEV0_CFG0 值。</para></param>
    /// <returns>The user-data size in bytes. <para>用户数据大小（字节）。</para></returns>
    internal static uint GetUdSize(uint cfg0) => (cfg0 & (0x3FFu << 9)) >> 9;

    /// <summary>
    /// Applies the spare-size field of NAND_DEV0_CFG0: bits 23..26 (set_sparesize,
    /// streaming.py:576-579).
    /// <para>应用 NAND_DEV0_CFG0 的 spare 域：23..26 位（set_sparesize，streaming.py:576-579）。</para>
    /// </summary>
    /// <param name="cfg0">The current NAND_DEV0_CFG0 value. <para>当前 NAND_DEV0_CFG0 值。</para></param>
    /// <param name="size">The spare size code. <para>spare 大小编码。</para></param>
    /// <returns>The updated CFG0 value. <para>更新后的 CFG0 值。</para></returns>
    internal static uint ApplySpareSize(uint cfg0, uint size)
        => (cfg0 & ~(0xFu << 23)) | (size << 23);

    /// <summary>
    /// Tests NAND_DEV0_CFG1 bit 27, the BCH ECC enable flag (streaming.py:571, 583).
    /// <para>检测 NAND_DEV0_CFG1 的位 27，即 BCH ECC 使能标志（streaming.py:571、583）。</para>
    /// </summary>
    /// <param name="cfg1">The NAND_DEV0_CFG1 value. <para>NAND_DEV0_CFG1 值。</para></param>
    /// <returns><see langword="true"/> when BCH ECC is enabled.
    /// <para>BCH ECC 使能时为 <see langword="true"/>。</para></returns>
    internal static bool UsesBchEcc(uint cfg1) => ((cfg1 >> 27) & 1) != 0;

    /// <summary>
    /// Applies the BCH branch of set_eccsize: bits 8..12 of NAND_DEV0_ECC_CFG carry the parity
    /// size (streaming.py:583-588).
    /// <para>应用 set_eccsize 的 BCH 分支：NAND_DEV0_ECC_CFG 的 8..12 位携带校验大小
    /// （streaming.py:583-588）。</para>
    /// </summary>
    /// <param name="eccCfg">The current NAND_DEV0_ECC_CFG value. <para>当前 NAND_DEV0_ECC_CFG 值。</para></param>
    /// <param name="size">The ECC parity size code. <para>ECC 校验大小编码。</para></param>
    /// <returns>The updated ECC config. <para>更新后的 ECC 配置。</para></returns>
    internal static uint ApplyBchEccSize(uint eccCfg, uint size)
        => (eccCfg & ~(0x1Fu << 8)) | (size << 8);

    /// <summary>
    /// Applies the Reed-Solomon branch of set_eccsize: bits 19..22 of NAND_DEV0_CFG0 carry the
    /// parity size (streaming.py:589-592).
    /// <para>应用 set_eccsize 的 Reed-Solomon 分支：NAND_DEV0_CFG0 的 19..22 位携带校验大小
    /// （streaming.py:589-592）。</para>
    /// </summary>
    /// <param name="cfg0">The current NAND_DEV0_CFG0 value. <para>当前 NAND_DEV0_CFG0 值。</para></param>
    /// <param name="size">The ECC parity size code. <para>ECC 校验大小编码。</para></param>
    /// <returns>The updated CFG0 value. <para>更新后的 CFG0 值。</para></returns>
    internal static uint ApplyRsEccSize(uint cfg0, uint size)
        => (cfg0 & ~(0xFu << 19)) | (size << 19);

    /// <summary>
    /// Produces the NAND_DEV0_ECC_CFG value of the bch_reset pulse: bit 1 (software ECC reset) is
    /// set for one write, after which the caller restores the original value
    /// (streaming.py:594-599, ECC_SW_RESET at nand_config.py:380).
    /// <para>生成 bch_reset 脉冲写入的 NAND_DEV0_ECC_CFG 值：位 1（软件 ECC 复位）置位一次写，
    /// 之后由调用方恢复原值（streaming.py:594-599，ECC_SW_RESET 见 nand_config.py:380）。</para>
    /// </summary>
    /// <param name="eccCfg">The current NAND_DEV0_ECC_CFG value. <para>当前 NAND_DEV0_ECC_CFG 值。</para></param>
    /// <returns>The pulsed ECC config. <para>脉冲后的 ECC 配置。</para></returns>
    internal static uint BchResetPulseValue(uint eccCfg) => eccCfg | 2u;

    /// <summary>
    /// Clears ECC for one register value the way nand_init and nand_post do: bit 0 becomes the
    /// disable flag (0 = ECC on, 1 = ECC off) (streaming.py:80-83, 139-142).
    /// <para>按 nand_init 与 nand_post 的方式设置单个寄存器的 ECC 位：位 0 变为禁用标志
    /// （0 = ECC 开启，1 = ECC 关闭）（streaming.py:80-83、139-142）。</para>
    /// </summary>
    /// <param name="register">The CFG1 or ECC_CFG value. <para>CFG1 或 ECC_CFG 值。</para></param>
    /// <param name="disable">1 to disable ECC, 0 to enable it. <para>1 禁用 ECC，0 启用。</para></param>
    /// <returns>The updated register value. <para>更新后的寄存器值。</para></returns>
    internal static uint ApplyEccDisable(uint register, uint disable)
        => (register & 0xFFFFFFFEu) | disable;

    /// <summary>
    /// Applies the hardware bad-block disable mask to NAND_DEV0_CFG1: bits 6..15 are cleared
    /// (hardware_bad_off, streaming.py:550-553).
    /// <para>对 NAND_DEV0_CFG1 应用硬件坏块禁用掩码：清除 6..15 位（hardware_bad_off，
    /// streaming.py:550-553）。</para>
    /// </summary>
    /// <param name="cfg1">The current NAND_DEV0_CFG1 value. <para>当前 NAND_DEV0_CFG1 值。</para></param>
    /// <returns>The masked CFG1 value. <para>掩码后的 CFG1 值。</para></returns>
    internal static uint DisableHardwareBadBlock(uint cfg1) => cfg1 & ~(0x3FFu << 6);

    /// <summary>
    /// Restores the hardware bad-block detection fields of NAND_DEV0_CFG1: bits 6..16 are cleared,
    /// the bad-block byte number is written into bits 6..15, and the in-spare-area flag into bit 16
    /// (hardware_bad_on, streaming.py:555-560, BAD_BLOCK_BYTE_NUM and BAD_BLOCK_IN_SPARE_AREA at
    /// nand_config.py:368-369).
    /// <para>恢复 NAND_DEV0_CFG1 的硬件坏块检测域：先清 6..16 位，把坏块字节号写入 6..15 位，
    /// spare 区内标志写入位 16（hardware_bad_on，streaming.py:555-560；BAD_BLOCK_BYTE_NUM 与
    /// BAD_BLOCK_IN_SPARE_AREA 见 nand_config.py:368-369）。</para>
    /// </summary>
    /// <param name="cfg1">The current NAND_DEV0_CFG1 value. <para>当前 NAND_DEV0_CFG1 值。</para></param>
    /// <param name="badBlockByteNum">The bad-block marker byte position. <para>坏块标记字节位置。</para></param>
    /// <param name="badBlockInSpareArea">1 when the marker lives in the spare area, else 0.
    /// <para>标记位于 spare 区时为 1，否则为 0。</para></param>
    /// <returns>The updated CFG1 value. <para>更新后的 CFG1 值。</para></returns>
    internal static uint EnableHardwareBadBlock(uint cfg1, uint badBlockByteNum, uint badBlockInSpareArea)
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
    internal static uint BuildPageAddress(uint block, uint page, uint pagesPerBlock)
        => checked((uint)((block * (ulong)pagesPerBlock) + page));

    /// <summary>
    /// Builds the NAND_ADDR0 value for a page: the linear address shifted left by 16
    /// (streaming.py:379).
    /// <para>构建某页的 NAND_ADDR0 值：线性地址左移 16 位（streaming.py:379）。</para>
    /// </summary>
    /// <param name="block">The block number. <para>块号。</para></param>
    /// <param name="page">The page number inside the block. <para>块内页号。</para></param>
    /// <param name="pagesPerBlock">The number of pages per block. <para>每块页数。</para></param>
    /// <returns>The NAND_ADDR0 value. <para>NAND_ADDR0 值。</para></returns>
    internal static uint PageAddress0(uint block, uint page, uint pagesPerBlock)
        => BuildPageAddress(block, page, pagesPerBlock) << 16;

    /// <summary>
    /// Builds the NAND_ADDR1 value for a page: bits 16..23 of the linear address
    /// (streaming.py:380).
    /// <para>构建某页的 NAND_ADDR1 值：线性地址的 16..23 位（streaming.py:380）。</para>
    /// </summary>
    /// <param name="block">The block number. <para>块号。</para></param>
    /// <param name="page">The page number inside the block. <para>块内页号。</para></param>
    /// <param name="pagesPerBlock">The number of pages per block. <para>每块页数。</para></param>
    /// <returns>The NAND_ADDR1 value. <para>NAND_ADDR1 值。</para></returns>
    internal static uint PageAddress1(uint block, uint page, uint pagesPerBlock)
        => (BuildPageAddress(block, page, pagesPerBlock) >> 16) & 0xFF;

    /// <summary>
    /// Builds the erase-phase NAND_ADDR0: the reference writes the plain linear block start with
    /// no 16-bit shift (block_erase, streaming.py:611).
    /// <para>构建擦除阶段的 NAND_ADDR0：参考实现写入不加 16 位移位的线性块起点
    /// （block_erase，streaming.py:611）。</para>
    /// </summary>
    /// <param name="block">The block number. <para>块号。</para></param>
    /// <param name="pagesPerBlock">The number of pages per block. <para>每块页数。</para></param>
    /// <returns>The erase NAND_ADDR0 value. <para>擦除用 NAND_ADDR0 值。</para></returns>
    internal static uint EraseAddress0(uint block, uint pagesPerBlock)
        => checked((uint)(block * (ulong)pagesPerBlock));

    /// <summary>
    /// Clears the read-config bits the block-erase path masks out of NAND_DEV0_CFG0: bits 6..8
    /// (streaming.py:613-614).
    /// <para>清除块擦除路径从 NAND_DEV0_CFG0 掩掉的读配置位：6..8 位（streaming.py:613-614）。</para>
    /// </summary>
    /// <param name="cfg0">The current NAND_DEV0_CFG0 value. <para>当前 NAND_DEV0_CFG0 值。</para></param>
    /// <returns>The erase-phase CFG0 value. <para>擦除阶段的 CFG0 值。</para></returns>
    internal static uint BlockEraseCfg0(uint cfg0) => cfg0 & ~0x1C0u;

    /// <summary>
    /// Judges a block bad from NAND_BUFFER_STATUS: the upper 16 status bits must all read 0xFF
    /// (0xFF in the byte pair 0xFF0000) for a narrow flash, or 0xFFFF0000 for a wide flash
    /// (tst_badblock, streaming.py:391-399, WIDE_FLASH at nand_config.py:365).
    /// <para>依据 NAND_BUFFER_STATUS 判定坏块：窄闪存的低两字节状态必须全为 0xFF（即 0xFF0000 掩码
    /// 相等），宽闪存则为 0xFFFF0000（tst_badblock，streaming.py:391-399；WIDE_FLASH 见
    /// nand_config.py:365）。</para>
    /// </summary>
    /// <param name="bufferStatus">The raw NAND_BUFFER_STATUS value. <para>原始 NAND_BUFFER_STATUS 值。</para></param>
    /// <param name="isWideFlash"><see langword="true"/> for x16 bus flash.
    /// <para>x16 总线闪存时为 <see langword="true"/>。</para></param>
    /// <returns><see langword="true"/> when the block is bad.
    /// <para>该块为坏块时为 <see langword="true"/>。</para></returns>
    internal static bool IsBadBlock(uint bufferStatus, bool isWideFlash)
    {
        var status = bufferStatus & 0xFFFF0000;
        return isWideFlash ? status != 0xFFFF0000 : status != 0xFF0000;
    }

    /// <summary>
    /// Evaluates the ECC verdict after a codeword read: when the buffer-error bit is set and the
    /// ECC-corrected bit of the command-register window reads clear, the read is an uncorrectable
    /// error (-1); otherwise the corrected-bit count is the low five bits
    /// (check_ecc_status, streaming.py:442-446). The odd reference window test
    /// <c>(NAND_FLASH_CMD + 0xEC) &amp; 0x40</c> depends on the controller base address, so the
    /// command-register address is passed explicitly.
    /// <para>评估一次码字读后的 ECC 结论：当缓冲区错误位为 1 且命令寄存器窗口中 ECC 已纠正位
    /// 读到 0 时为不可纠错（-1），否则低 5 位是纠正位数（check_ecc_status，streaming.py:442-446）。
    /// 参考实现那个奇怪的窗口测试 <c>(NAND_FLASH_CMD + 0xEC) &amp; 0x40</c> 依赖控制器基地址，因此
    /// 这里显式传入命令寄存器地址。</para>
    /// </summary>
    /// <param name="bufferStatus">The raw NAND_BUFFER_STATUS value. <para>原始 NAND_BUFFER_STATUS 值。</para></param>
    /// <param name="flashCommandAddress">The address of NAND_FLASH_CMD. <para>NAND_FLASH_CMD 的地址。</para></param>
    /// <returns>-1 for an uncorrectable error, else the corrected-bit count.
    /// <para>不可纠错为 -1，否则为纠正位数。</para></returns>
    internal static int EvaluateEccStatus(uint bufferStatus, uint flashCommandAddress)
    {
        if ((bufferStatus & 0x100u) != 0 && unchecked(((flashCommandAddress + 0xECu) & 0x40u)) == 0)
        {
            return -1;
        }

        return (int)(bufferStatus & 0x1Fu);
    }
}