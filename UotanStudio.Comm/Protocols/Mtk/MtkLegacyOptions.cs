using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Selects an explicit non-IoT Legacy DA wire profile.
/// <para>选择显式的非 IoT Legacy DA 线协议配置。</para></summary>
public sealed record MtkLegacyOptions
{
    /// <summary>Gets the exact three-byte DA1 inventory response asserted after acknowledging the inventory.
    /// <para>获取确认 DA1 清单后所断言的精确三字节响应。</para></summary>
    /// <value>
    /// Six hexadecimal characters, or <see langword="null"/> to accept the response the agent actually
    /// sends. The reference implementation logs these three bytes and never compares them, so a loader
    /// package that does not publish them can still start; when a value is supplied it acts as an
    /// additional identity assertion and a mismatch faults the session. The bytes the agent reported are
    /// recorded in the startup evidence and named in the mismatch message.
    /// <para>六个十六进制字符；为 <see langword="null"/> 时接受代理实际上报的响应。参考实现只记录这三个字节而不做
    /// 比较，因此未公开该值的加载器包同样可以启动；提供数值时它作为额外的身份断言，不匹配即使会话失效。代理上报的
    /// 字节会写入启动证据，并出现在不匹配的错误信息中。</para>
    /// </value>
    public string? StageOneResponseHex { get; init; }

    /// <summary>Gets whether the inventory uses a 64-bit rather than 32-bit NAND size.
    /// <para>获取清单是否使用 64 位而非 32 位 NAND 容量。</para></summary>
    /// <value>
    /// False by default and applied only to the NAND family; the total inventory byte count is the same
    /// for either layout, so the flag selects how the size field is parsed. It is not inferred from a
    /// zero ID count.
    /// <para>默认为 false，仅作用于 NAND 家族；两种布局的清单一字节数相同，本标志决定容量字段如何解析。
    /// 不根据零 ID 数量推测。</para>
    /// </value>
    public bool NandSize64Bit { get; init; }

    /// <summary>Gets the host platform byte used in read commands.
    /// <para>获取读取命令使用的主机平台字节。</para></summary>
    /// <value>The explicit Windows or Linux wire profile.
    /// <para>显式的 Windows 或 Linux 线协议配置。</para></value>
    public required MtkXmlHostOs HostOs { get; init; }

    /// <summary>Gets the explicitly selected startup completion framing.
    /// <para>获取显式选择的启动完成帧布局。</para></summary>
    /// <value>Standard ten-byte completion by default; the SDK processing wait must be explicitly enabled.
    /// No byte scanning or automatic layout fallback occurs.
    /// <para>默认使用标准十字节完成帧；SDK 处理等待必须显式启用。不扫描字节，也不自动回退布局。</para></value>
    public MtkLegacyCompletionProfile CompletionProfile { get; init; }

    /// <summary>Gets the bounded DA2 upload packet length.
    /// <para>获取有界的 DA2 上传包长度。</para></summary>
    /// <value>A multiple of four from 4 through 1048576, defaulting to 4096.
    /// <para>4 至 1048576 范围内的四字节倍数，默认为 4096。</para></value>
    public int AgentPacketLength { get; init; } = 4096;

    /// <summary>Gets the packet length selected for eMMC and NOR reads and writes.
    /// <para>获取为 eMMC 和 NOR 读写选择的包长度。</para></summary>
    /// <value>A multiple of 512 from 512 through 1048576.
    /// <para>512 至 1048576 范围内的 512 字节倍数。</para></value>
    public int StoragePacketLength { get; init; } = 1024 * 1024;

    /// <summary>Gets the cooperative deadline for the complete agent startup.
    /// <para>获取完整代理启动过程的协作式截止时间。</para></summary>
    /// <value>Milliseconds from 1 through 86400000.
    /// <para>1 至 86400000 毫秒。</para></value>
    public int StartupTimeoutMs { get; init; } = 120000;

    /// <summary>Gets the two-byte NOR_CFG chip-select word sent during runtime setup.
    /// <para>获取运行时设置发送的双字节 NOR_CFG 片选字。</para></summary>
    /// <value>
    /// The reference implementation writes this word big-endian and documents only two values,
    /// <c>0x0000</c> for CS0 and <c>0x0008</c> for CS_WITH_DECODER, transmitting <c>0x0008</c>. Any other
    /// value is refused before the payload is built.
    /// <para>参考实现以大端写入该字，且只记录两个取值：<c>0x0000</c> 表示 CS0，<c>0x0008</c> 表示
    /// CS_WITH_DECODER，实际发送 <c>0x0008</c>。其它取值会在构造负载之前被拒绝。</para>
    /// </value>
    public ushort NorChipSelect { get; init; } = 0x0008;

    /// <summary>Gets the single chip-select byte sent immediately after the NOR_CFG word.
    /// <para>获取紧随 NOR_CFG 字发送的单字节片选。</para></summary>
    /// <value>
    /// The reference implementation labels this byte <c>m_nor_chip_select</c> at one site and
    /// <c>m_nand_chip_select CS0</c> at another while transmitting <c>0x00</c> at both, so the byte selects
    /// which flash chip select the loader drives rather than echoing the inventory field of the same name.
    /// It is never inferred from device bytes.
    /// <para>参考实现在一处将该字节标注为 <c>m_nor_chip_select</c>，在另一处标注为 <c>m_nand_chip_select CS0</c>，
    /// 两处均发送 <c>0x00</c>；因此该字节选择加载器驱动哪个闪存片选，而不是回填同名的清单上报字段。不根据设备字节推测。</para>
    /// </value>
    public byte NandChipSelect { get; init; }

    /// <summary>Gets the NAND access timing word.
    /// <para>获取 NAND 访问时序字。</para></summary>
    /// <value>
    /// The raw <c>m_nand_acccon</c> word the loader programs into the flash controller, written big-endian;
    /// the reference implementation transmits <c>0x7007FFFF</c> and defines no further value domain, so no
    /// range is imposed here.
    /// <para>加载器写入闪存控制器的原始 <c>m_nand_acccon</c> 字，按大端序书写；参考实现发送 <c>0x7007FFFF</c>
    /// 且未定义其它取值范围，因此此处不额外限制区间。</para>
    /// </value>
    public uint NandAccessTiming { get; init; } = 0x7007FFFF;

    /// <summary>Gets the bad-block management flag byte sent during runtime setup.
    /// <para>获取运行时设置发送的坏块管理标志字节。</para></summary>
    /// <value>
    /// Zero, one or two. The reference table picks the flag per chip and flash family and uses all three
    /// values, including two for the MT6582 eMMC family, so this is deliberately a byte and not a boolean.
    /// One is the reference default for every chip the table leaves untouched.
    /// <para>0、1 或 2。参考表按芯片与闪存家族选择该标志，三个取值都会被使用，其中 MT6582 eMMC 使用 2，
    /// 因此本项刻意保留为字节而不是布尔。表格未覆盖的芯片默认使用参考默认值 1。</para>
    /// </value>
    public byte NandBmtFlag { get; init; } = 1;

    /// <summary>Gets the bad-block management pool size sent during runtime setup.
    /// <para>获取运行时设置发送的坏块管理池大小。</para></summary>
    /// <value>
    /// The big-endian <c>bmtpartsize</c> word. The reference pairs it with the flag freely, transmitting zero
    /// for chips whose flag is one and a non-zero pool for chips whose flag is zero, so no coupling between
    /// the two is enforced here.
    /// <para>大端的 <c>bmtpartsize</c> 字。参考实现允许它与标志自由组合：某些芯片标志为 1 而池大小为 0，
    /// 另一些芯片标志为 0 而池大小非 0，因此此处不强制两者耦合。</para>
    /// </value>
    public uint NandBmtPoolBytes { get; init; }

    /// <summary>Gets whether charging is forced on rather than automatically detected.
    /// <para>获取是否强制充电而非自动检测。</para></summary>
    /// <value>
    /// The reference implementation names this byte <c>force_charge</c>, documents <c>0x02</c> as Auto and
    /// <c>0x01</c> as On, and transmits On. False selects wire value 2 and true selects 1.
    /// <para>参考实现将该字节命名为 <c>force_charge</c>，注明 <c>0x02</c> 为自动、<c>0x01</c> 为强制开启，并发送开启值。
    /// false 选择线值 2，true 选择 1。</para>
    /// </value>
    public bool ForceCharging { get; init; }

    internal void Validate()
    {
        if (StageOneResponseHex is { } expected
            && (expected.Length != 6 || !expected.All(char.IsAsciiHexDigit)))
        {
            throw new ArgumentException("A supplied Legacy DA1 response assertion must be exactly three bytes in hexadecimal.");
        }
        if (!Enum.IsDefined(HostOs) || !Enum.IsDefined(CompletionProfile))
        {
            throw new ArgumentException("Invalid Legacy host or completion profile.");
        }
        if (NorChipSelect is not (0x0000 or 0x0008))
        {
            throw new ArgumentOutOfRangeException(nameof(NorChipSelect),
                "The Legacy NOR_CFG word selects either CS0 or CS_WITH_DECODER and must be 0x0000 or 0x0008.");
        }
        ArgumentOutOfRangeException.ThrowIfGreaterThan(NandBmtFlag, (byte)2);
        if (AgentPacketLength is < 4 or > 1048576 || AgentPacketLength % 4 != 0
            || StoragePacketLength is < 512 or > 1048576 || StoragePacketLength % 512 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AgentPacketLength));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(StartupTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(StartupTimeoutMs, 86400000);
    }

    internal byte[] BuildRuntime(ushort hardwareCode, MtkLegacyBootVersions versions)
    {
        Validate();
        if (hardwareCode is 0x6261 or 0x2625 or 0x2523 or 0x7682 or 0x7686 or 0x5932)
        {
            throw new NotSupportedException("The selected non-IoT Legacy profile cannot initialize this IoT boot chain.");
        }
        var suffixLength = hardwareCode switch
        {
            0x6592 or 0x6583 or 0x6589 or 0x6582 => 4,
            0x6580 or 0x8163 => 24,
            0x8127 => 28,
            _ => 0
        };
        var bytes = new byte[18 + suffixLength];
        bytes[0] = versions.BootRom;
        bytes[1] = versions.Bootloader;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), NorChipSelect);
        bytes[4] = NandChipSelect;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(5), NandAccessTiming);
        bytes[9] = NandBmtFlag;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(10), NandBmtPoolBytes);
        bytes[14] = ForceCharging ? (byte)1 : (byte)2;
        bytes[15] = hardwareCode == 0x6583 ? (byte)0 : (byte)1;
        bytes[16] = 2;
        if (hardwareCode is 0x6580 or 0x8163 or 0x8127)
        {
            var start = hardwareCode == 0x8127 ? 22 : 18;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(start), 1);
            bytes[start + 4] = 0x46;
            bytes[start + 5] = 0x46;
            bytes[start + 20] = 0xFF;
        }
        else if (hardwareCode is 0x6589 or 0x6582)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(18), 1);
        }
        return bytes;
    }
}

/// <summary>Contains the one-byte boot versions queried before starting a Legacy agent.
/// <para>包含启动 Legacy 代理之前查询的单字节引导版本。</para></summary>
/// <param name="BootRom">The response to command FF.
/// <para>命令 FF 的响应。</para></param>
/// <param name="Bootloader">The response to command FE, including FE itself in Boot ROM.
/// <para>命令 FE 的响应，包括 Boot ROM 中的 FE 本身。</para></param>
public sealed record MtkLegacyBootVersions(byte BootRom, byte Bootloader);