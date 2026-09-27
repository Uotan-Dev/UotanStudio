using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Selects an explicit non-IoT Legacy DA wire profile.
/// <para>选择显式的非 IoT Legacy DA 线协议配置。</para></summary>
public sealed record MtkLegacyOptions
{
    /// <summary>Gets the exact three-byte response expected after acknowledging the DA1 inventory.
    /// <para>获取确认 DA1 清单后预期的精确三字节响应。</para></summary>
    /// <value>Six hexadecimal characters supplied by the selected loader profile.
    /// <para>所选加载器配置提供的六个十六进制字符。</para></value>
    public required string StageOneResponseHex { get; init; }

    /// <summary>Gets whether the inventory uses a 64-bit rather than 32-bit NAND size.
    /// <para>获取清单是否使用 64 位而非 32 位 NAND 容量。</para></summary>
    /// <value>The explicitly selected layout; it is not inferred from a zero ID count.
    /// <para>显式选择的布局，不根据零 ID 数量推测。</para></value>
    public required bool NandSize64Bit { get; init; }

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

    /// <summary>Gets the NOR configuration word sent during runtime setup.
    /// <para>获取运行时设置发送的 NOR 配置字。</para></summary>
    /// <value>The selected chip configuration, defaulting to 8.
    /// <para>选定的芯片配置，默认为 8。</para></value>
    public ushort NorConfiguration { get; init; } = 8;

    /// <summary>Gets the NAND chip-select byte.
    /// <para>获取 NAND 片选字节。</para></summary>
    /// <value>The selected chip, defaulting to zero.
    /// <para>选定的芯片，默认为零。</para></value>
    public byte NandChipSelect { get; init; }

    /// <summary>Gets the NAND access timing word.
    /// <para>获取 NAND 访问时序字。</para></summary>
    /// <value>The selected timing word, defaulting to 0x7007FFFF.
    /// <para>选定的时序字，默认为 0x7007FFFF。</para></value>
    public uint NandAccessTiming { get; init; } = 0x7007FFFF;

    /// <summary>Gets whether the selected profile enables a NAND bad-block management pool.
    /// <para>获取所选配置是否启用 NAND 坏块管理池。</para></summary>
    /// <value>False unless explicitly enabled by the caller.
    /// <para>除非调用方显式启用，否则为 false。</para></value>
    public bool NandBmtEnabled { get; init; }

    /// <summary>Gets the selected NAND bad-block management pool size.
    /// <para>获取选定的 NAND 坏块管理池大小。</para></summary>
    /// <value>A positive byte count exactly when BMT is enabled; otherwise zero.
    /// <para>启用 BMT 时为正数字节数，否则为零。</para></value>
    public uint NandBmtPoolBytes { get; init; }

    /// <summary>Gets whether charging is forced on rather than automatically detected.
    /// <para>获取是否强制充电而非自动检测。</para></summary>
    /// <value>False selects wire value 2; true selects 1.
    /// <para>false 选择线值 2；true 选择 1。</para></value>
    public bool ForceCharging { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(StageOneResponseHex);
        if (StageOneResponseHex.Length != 6 || !StageOneResponseHex.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("The selected Legacy profile must specify its exact three-byte DA1 response.");
        }
        if (!Enum.IsDefined(HostOs) || !Enum.IsDefined(CompletionProfile) || NandBmtEnabled != (NandBmtPoolBytes > 0))
        {
            throw new ArgumentException("Invalid Legacy host or BMT configuration.");
        }
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
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), NorConfiguration);
        bytes[4] = NandChipSelect;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(5), NandAccessTiming);
        bytes[9] = NandBmtEnabled ? (byte)1 : (byte)0;
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