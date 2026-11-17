using System.Globalization;

namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// NVIDIA Tegra generation behind an RCM (APX) endpoint; the generation selects the RCM message layout and
/// version the host must speak.
/// <para>RCM（APX）端点所属的 NVIDIA Tegra 代际；代际决定主机必须使用的 RCM 消息布局与版本。</para>
/// </summary>
/// <remarks>
/// The member order is the identifier order from <c>tegrarcm</c>'s <c>usb.h</c>: a product ID maps onto a
/// generation by its low byte (0x20, 0x30, 0x35, 0x40).
/// <para>成员顺序即 <c>tegrarcm</c> 的 <c>usb.h</c> 中的标识顺序：产品 ID 按低字节（0x20、0x30、0x35、0x40）
/// 映射到代际。</para>
/// </remarks>
public enum TegraChip
{
    /// <summary>
    /// Tegra 2 (T20), RCM version 1.0, rcm1 message header.
    /// <para>Tegra 2（T20），RCM 版本 1.0，使用 rcm1 消息头。</para>
    /// </summary>
    Tegra20 = 0,

    /// <summary>
    /// Tegra 3 (T30), RCM version 1.0, rcm1 message header.
    /// <para>Tegra 3（T30），RCM 版本 1.0，使用 rcm1 消息头。</para>
    /// </summary>
    Tegra30 = 1,

    /// <summary>
    /// Tegra 4 (T114), RCM version 0x35.1, rcm35 message header.
    /// <para>Tegra 4（T114），RCM 版本 0x35.1，使用 rcm35 消息头。</para>
    /// </summary>
    Tegra114 = 2,

    /// <summary>
    /// Tegra K1 (T124), RCM version 0x40.1, rcm40 message header.
    /// <para>Tegra K1（T124），RCM 版本 0x40.1，使用 rcm40 消息头。</para>
    /// </summary>
    Tegra124 = 3,

    /// <summary>
    /// A Tegra device whose generation the host could not establish, or an endpoint that is not a Tegra RCM
    /// port at all.
    /// <para>主机无法确定代际的 Tegra 设备，或根本不是 Tegra RCM 端口的端点。</para>
    /// </summary>
    Unknown = 4,
}

/// <summary>
/// Helpers that map between Tegra generations, the kernel's <c>tegraid</c> value and the RCM message layout.
/// <para>Tegra 代际、内核 <c>tegraid</c> 取值与 RCM 消息布局之间的映射辅助方法。</para>
/// </summary>
/// <remarks>
/// C# does not allow static members on an enum, so the enum's companion operations live here. The layout and
/// version mapping is delegated to <see cref="TegraRcmHeaderLayout.For"/> so both directions share one table.
/// <para>C# 不允许在枚举上声明静态成员，因此枚举的配套操作放在此处。布局与版本映射委托给
/// <see cref="TegraRcmHeaderLayout.For"/>，两个方向共用同一张表。</para>
/// </remarks>
public static class TegraChips
{
    /// <summary>
    /// Reports whether <paramref name="chip"/> names a generation the RCM protocol knows.
    /// <para>报告 <paramref name="chip"/> 是否为 RCM 协议已知的代际。</para>
    /// </summary>
    /// <param name="chip">The chip generation to test. <para>待检测的芯片代际。</para></param>
    /// <returns><see langword="true"/> for any generation other than <see cref="TegraChip.Unknown"/>.
    /// <para>除 <see cref="TegraChip.Unknown"/> 以外的任何代际都为 <see langword="true"/>。</para></returns>
    public static bool IsKnown(TegraChip chip) => chip != TegraChip.Unknown;

    /// <summary>
    /// Resolves the kernel command-line <c>tegraid</c> value, such as <c>35.1.2.0.0</c>, into a chip generation.
    /// <para>把内核命令行 <c>tegraid</c> 取值（如 <c>35.1.2.0.0</c>）解析为芯片代际。</para>
    /// </summary>
    /// <param name="tegraId">The <c>tegraid</c> value, optionally carrying an <c>0x</c> prefix; surrounding
    /// whitespace is ignored. <para><c>tegraid</c> 取值，可带 <c>0x</c> 前缀；首尾空白会被忽略。</para></param>
    /// <returns>
    /// The generation whose RCM device ID the first dot-separated component spells in hexadecimal — 0x20 maps to
    /// <see cref="TegraChip.Tegra20"/>, 0x30 to <see cref="TegraChip.Tegra30"/>, 0x35 to
    /// <see cref="TegraChip.Tegra114"/> and 0x40 to <see cref="TegraChip.Tegra124"/> — or
    /// <see cref="TegraChip.Unknown"/> when the value is empty, malformed or names another silicon revision.
    /// <para>首个点分分量按十六进制拼出的 RCM 设备 ID 所对应的代际——0x20 映射到 <see cref="TegraChip.Tegra20"/>，
    /// 0x30 到 <see cref="TegraChip.Tegra30"/>，0x35 到 <see cref="TegraChip.Tegra114"/>，0x40 到
    /// <see cref="TegraChip.Tegra124"/>；取值为空、格式错误或指向其它芯片版本时为 <see cref="TegraChip.Unknown"/>。</para>
    /// </returns>
    /// <example>
    /// <code>
    /// var chip = TegraChips.FromTegraId("35.1.2.0.0"); // TegraChip.Tegra114
    /// </code>
    /// </example>
    public static TegraChip FromTegraId(string? tegraId)
        => tegraId is null ? TegraChip.Unknown : FromTegraId(tegraId.AsSpan());

    /// <summary>
    /// Resolves the kernel command-line <c>tegraid</c> value, such as <c>35.1.2.0.0</c>, into a chip generation.
    /// <para>把内核命令行 <c>tegraid</c> 取值（如 <c>35.1.2.0.0</c>）解析为芯片代际。</para>
    /// </summary>
    /// <param name="tegraId">The <c>tegraid</c> value, optionally carrying an <c>0x</c> prefix; surrounding
    /// whitespace is ignored. <para><c>tegraid</c> 取值，可带 <c>0x</c> 前缀；首尾空白会被忽略。</para></param>
    /// <returns>The resolved generation, or <see cref="TegraChip.Unknown"/> when the value names none of them.
    /// <para>解析出的代际；取值不对应任何代际时为 <see cref="TegraChip.Unknown"/>。</para></returns>
    public static TegraChip FromTegraId(ReadOnlySpan<char> tegraId)
        => ParseMajorDeviceId(tegraId) switch
        {
            TegraConstants.Tegra20DeviceId => TegraChip.Tegra20,
            TegraConstants.Tegra30DeviceId => TegraChip.Tegra30,
            TegraConstants.Tegra114DeviceId => TegraChip.Tegra114,
            TegraConstants.Tegra124DeviceId => TegraChip.Tegra124,
            _ => TegraChip.Unknown,
        };

    /// <summary>
    /// Gets the RCM message version a generation speaks: <c>RCM_VERSION_1</c> (1.0) for Tegra 2 and Tegra 3,
    /// <c>RCM_VERSION_35</c> (0x35.1) for Tegra 4 and <c>RCM_VERSION_40</c> (0x40.1) for Tegra K1.
    /// <para>获取某代际使用的 RCM 消息版本：Tegra 2 与 Tegra 3 为 <c>RCM_VERSION_1</c>（1.0），Tegra 4 为
    /// <c>RCM_VERSION_35</c>（0x35.1），Tegra K1 为 <c>RCM_VERSION_40</c>（0x40.1）。</para>
    /// </summary>
    /// <param name="chip">The chip generation. <para>芯片代际。</para></param>
    /// <returns>The packed <c>NVBOOT_VERSION(major, minor)</c> word the generation uses.
    /// <para>该代际使用的 <c>NVBOOT_VERSION(major, minor)</c> 打包字。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chip"/> is
    /// <see cref="TegraChip.Unknown"/>, which has no RCM version.
    /// <para><paramref name="chip"/> 为 <see cref="TegraChip.Unknown"/>，该值没有 RCM 版本。</para></exception>
    public static uint GetRcmVersion(TegraChip chip) => TegraRcmHeaderLayout.For(chip).RcmVersion;

    /// <summary>
    /// Gets the RCM message layout a generation speaks, which fixes every header field offset.
    /// <para>获取某代际使用的 RCM 消息布局，该布局固定了每个头部字段的偏移。</para>
    /// </summary>
    /// <param name="chip">The chip generation. <para>芯片代际。</para></param>
    /// <returns>The layout whose header size and offsets apply to <paramref name="chip"/>.
    /// <para>适用于 <paramref name="chip"/> 的头部大小与偏移所在的布局。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chip"/> is
    /// <see cref="TegraChip.Unknown"/>, which has no RCM layout.
    /// <para><paramref name="chip"/> 为 <see cref="TegraChip.Unknown"/>，该值没有 RCM 布局。</para></exception>
    public static TegraRcmHeaderLayout GetLayout(TegraChip chip) => TegraRcmHeaderLayout.For(chip);

    private static int ParseMajorDeviceId(ReadOnlySpan<char> tegraId)
    {
        var text = tegraId.Trim();
        if (text.IsEmpty)
        {
            return -1;
        }

        var separator = text.IndexOf('.');
        var major = separator < 0 ? text : text[..separator];
        if (major.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            major = major[2..];
        }

        // The kernel prints the packed chip id in hexadecimal, so "35" stands for 0x35 and not for 53.
        return int.TryParse(major, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : -1;
    }
}