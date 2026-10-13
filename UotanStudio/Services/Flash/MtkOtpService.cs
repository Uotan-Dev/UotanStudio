using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Security;

namespace UotanStudio.Services.Flash;

/// <summary>Read-only one-time-programmable access of the download agent, plus a preview that sends nothing.
/// <para>下载代理的只读一次性可编程访问，以及一个不发送任何字节的预览。</para></summary>
internal static class MtkOtpService
{
    /// <summary>Describes an eFuse cell read without touching the session, the transport or the device.
    /// <para>在不触及会话、传输与设备的前提下描述一次 eFuse 单元读取。</para></summary>
    /// <param name="hardwareCode">The hardware code selecting the cell table.
    /// <para>用于选择单元表的硬件代码。</para></param>
    /// <param name="efuseBase">The eFuse base address the cells resolve against.
    /// <para>单元解析所用的 eFuse 基址。</para></param>
    /// <param name="startIndex">The first cell index the read would request.
    /// <para>读取将要请求的首个单元索引。</para></param>
    /// <param name="count">The number of cells the read would request.
    /// <para>读取将要请求的单元数量。</para></param>
    /// <returns>The resolved plan of cells to read and cells to report.
    /// <para>已解析的计划，含将读取与将直接上报的单元。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The range is empty, wider than the documented table
    /// ceiling, or outside the table. <para>范围为空、超过已记载的表上限，或不在表内。</para></exception>
    internal static MtkOtpReadPreview DescribeEfuseRead(ushort hardwareCode, uint efuseBase, int startIndex,
        int count)
    {
        if (!MtkEfuseFieldTable.TryGetCells(hardwareCode, out var cells))
        {
            return new MtkOtpReadPreview(hardwareCode, efuseBase, false, [],
                $"No transcribed eFuse table exists for hardware code 0x{hardwareCode:X4}.");
        }

        MtkEfuseAccess.ValidateRange(cells, startIndex, count);
        var plan = new List<MtkOtpCellPreview>(count);
        for (var offset = 0; offset < count; offset++)
        {
            var index = checked(startIndex + offset);
            var cell = MtkEfuseAccess.ResolveCell(cells[index], efuseBase);
            plan.Add(new MtkOtpCellPreview(index, cell.Address, !cell.IsReportedWithoutRead));
        }

        return new MtkOtpReadPreview(hardwareCode, efuseBase, true, plan, null);
    }

    /// <summary>Reads eFuse cells through the framing of the family the session is already in.
    /// <para>通过会话已处家族的帧结构读取 eFuse 单元。</para></summary>
    /// <param name="session">A session already at a download-agent stage of the named family.
    /// <para>已处于所指代理家族下载代理阶段的会话。</para></param>
    /// <param name="family">The agent family whose read framing is used; no fallback occurs.
    /// <para>使用其读取帧结构的代理家族；不发生回退。</para></param>
    /// <param name="hardwareCode">The hardware code selecting the cell table.
    /// <para>用于选择单元表的硬件代码。</para></param>
    /// <param name="efuseBase">The eFuse base address supplied by the device profile.
    /// <para>由设备配置提供的 eFuse 基址。</para></param>
    /// <param name="startIndex">The first cell index to read. <para>要读取的首个单元索引。</para></param>
    /// <param name="count">The number of cells to read. <para>要读取的单元数量。</para></param>
    /// <param name="ct">Cancellation honoured before the first cell and between every cell.
    /// <para>在首个单元之前以及每个单元之间都会响应的取消令牌。</para></param>
    /// <returns>The per-cell readings in table order. <para>按表顺序排列的逐单元读取结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="family"/> is not one of the three
    /// download-agent families. <para><paramref name="family"/> 不是三个下载代理家族之一。</para></exception>
    internal static Task<MtkEfuseReading> ReadEfuseCellsAsync(MtkSession session, MtkDaProtocol family,
        ushort hardwareCode, uint efuseBase, int startIndex, int count, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (family is not (MtkDaProtocol.XFlash or MtkDaProtocol.Legacy or MtkDaProtocol.Xml))
        {
            throw new ArgumentOutOfRangeException(nameof(family),
                "eFuse reads support the XFlash, XML and Legacy download agent families.");
        }

        return family switch
        {
            MtkDaProtocol.XFlash => session.ReadXFlashEfuseCellsAsync(hardwareCode, efuseBase, startIndex, count, ct),
            MtkDaProtocol.Legacy => session.ReadLegacyEfuseCellsAsync(hardwareCode, efuseBase, startIndex, count, ct),
            _ => session.ReadXmlEfuseCellsAsync(hardwareCode, efuseBase, startIndex, count, ct)
        };
    }
}

/// <summary>An eFuse read plan resolved without contacting the device.
/// <para>无需接触设备即可完成解析的 eFuse 读取计划。</para></summary>
/// <param name="HardwareCode">The hardware code whose table was consulted.
/// <para>被查表的硬件代码。</para></param>
/// <param name="EfuseBase">The base address the cells were resolved against.
/// <para>解析单元所用的基址。</para></param>
/// <param name="TableKnown">Whether a transcribed table covers the hardware code.
/// <para>是否存在覆盖该硬件代码的转录表。</para></param>
/// <param name="Cells">The resolved cells, in table order; empty when no table is known.
/// <para>已解析单元，按表顺序排列；未知表时为空。</para></param>
/// <param name="Refusal">Why the read would be refused, or <see langword="null"/> when it would proceed.
/// <para>读取会被拒绝的原因；可以 proceed 时为 <see langword="null"/>。</para></param>
internal sealed record MtkOtpReadPreview(ushort HardwareCode, uint EfuseBase, bool TableKnown,
    IReadOnlyList<MtkOtpCellPreview> Cells, string? Refusal);

/// <summary>One resolved eFuse cell of a preview.
/// <para>预览中一个已解析的 eFuse 单元。</para></summary>
/// <param name="Index">The zero-based index in the per-chip table.
/// <para>逐芯片表中从零开始的索引。</para></param>
/// <param name="Address">The resolved address, or the reported value itself.
/// <para>解析后的地址，或即为待上报的取值。</para></param>
/// <param name="WouldReadFromTarget">Whether the documented reader reads at this address rather than
/// reporting the value. <para>已记载的读取器是否在该地址读取，而不是直接上报取值。</para></param>
internal sealed record MtkOtpCellPreview(int Index, uint Address, bool WouldReadFromTarget);