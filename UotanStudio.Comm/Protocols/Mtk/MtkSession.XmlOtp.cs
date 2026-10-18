using System.Buffers.Binary;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Security;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Per-chip eFuse cell reads of the XML family through its documented custom memory read.
/// <para>XML 家族经其已记载的自定义内存读取执行逐芯片 eFuse 单元读取。</para></summary>
/// <remarks>
/// <para>The reference XML extension reads a fuse table by resolving each cell and peeking four bytes at
/// it through the dispatcher's <c>peek</c>, which is the <c>CMD:CUSTOMMEMR</c> exchange of
/// Library/DA/xml/extension/v6.py:706-718 with :498-516. A resolved value below <c>0x1000</c> is reported
/// as its own little-endian doubleword instead of being read, which is the same rule the XFlash reader
/// applies (Library/DA/xflash/extension/xflash.py:618-650).</para>
/// <para>参考的 XML 扩展会先解析每个单元，再通过分发器的 <c>peek</c> 在其地址上窥探四个字节来读取 fuse 表，
/// 而该 <c>peek</c> 就是 Library/DA/xml/extension/v6.py:706-718 与 :498-516 的 <c>CMD:CUSTOMMEMR</c> 交互。
/// 解析后小于 <c>0x1000</c> 的取值按其自身的小端双字上报而不发起读取，与 XFlash 读取器所用规则相同
/// （Library/DA/xflash/extension/xflash.py:618-650）。</para>
/// <para>The reference leaves that loop at its first non-address cell, because its branch uses
/// <c>return data.append(...)</c> and so abandons the remaining cells (Library/DA/xml/extension/v6.py:714-715).
/// This path reports the value and keeps walking, matching the XFlash and Legacy readers of this session, so
/// a reading is complete or a failure is raised instead of a silently truncated table.</para>
/// <para>参考实现在遇到首个非地址单元时就会离开该循环，因为其分支使用了
/// <c>return data.append(...)</c>，从而放弃剩余单元（Library/DA/xml/extension/v6.py:714-715）。本路径会上报该
/// 取值并继续遍历，与本会话的 XFlash、Legacy 读取器一致，因此一次读取要么完整，要么抛出失败，而不会静默截断表。</para>
/// <para>These reads are the only eFuse access this family offers. The reference never sends an OTP program
/// command for any family (Library/DA/legacy/dalegacy_param.py:105-110 declares the bytes and no client in
/// the reference sends them), so no XML write path exists here and the boot public-key window is not read
/// through XML either: the reference fetches that key only in the XFlash and Legacy clients
/// (Library/DA/xflash/extension/xflash.py:630-636 with Library/DA/legacy/extension/legacy.py:216-222).</para>
/// <para>这些读取是该家族提供的唯一 eFuse 访问。参考从未为任何家族发送 OTP 编程命令
/// （Library/DA/legacy/dalegacy_param.py:105-110 仅声明命令字节，参考中没有任何客户端会发送它们），因此这里
/// 不存在 XML 写入路径；引导公钥窗口也不经 XML 读取：参考只在 XFlash 与 Legacy 客户端中取回该公钥
/// （Library/DA/xflash/extension/xflash.py:630-636，配合
/// Library/DA/legacy/extension/legacy.py:216-222）。</para>
/// </remarks>
public sealed partial class MtkSession
{
    private const string XmlCustomMemoryCommand = "CMD:CUSTOMMEMR";

    /// <summary>Reads eFuse cells through the XML custom memory read.
    /// <para>通过 XML 自定义内存读取读取 eFuse 单元。</para></summary>
    /// <param name="hardwareCode">The hardware code selecting the cell table.
    /// <para>用于选择单元表的硬件代码。</para></param>
    /// <param name="efuseBase">The eFuse base address supplied by the device profile.
    /// <para>由设备配置提供的 eFuse 基址。</para></param>
    /// <param name="startIndex">The first cell index to read. 要读取的首个单元索引。</param>
    /// <param name="count">The number of cells to read, at most <see cref="MtkEfuseAccess.MaximumRequestedCells"/>.
    /// <para>要读取的单元数量，最多 <see cref="MtkEfuseAccess.MaximumRequestedCells"/>。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The per-cell readings in table order. 按表顺序排列的逐单元读取结果。</returns>
    /// <exception cref="NotSupportedException"><paramref name="hardwareCode"/> has no transcribed cell table; the nearest chip's table is not substituted.
    /// <para><paramref name="hardwareCode"/> 没有转录的单元表；不会用最近芯片的表替代。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The requested range is empty, oversized or outside the table.
    /// <para>请求范围为空、超限或不在表内。</para></exception>
    /// <exception cref="InvalidOperationException">No XML DA control stage is attached.
    /// <para>未附加 XML DA 控制阶段。</para></exception>
    /// <exception cref="MtkProtocolException">A cell reply was malformed or short; the XML family faults.
    /// <para>某个单元的回复格式错误或过短；XML 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkEfuseReading> ReadXmlEfuseCellsAsync(ushort hardwareCode, uint efuseBase, int startIndex,
        int count, CancellationToken cancellationToken = default)
    {
        if (!MtkEfuseFieldTable.TryGetCells(hardwareCode, out var cells))
        {
            throw new NotSupportedException(
                $"No transcribed eFuse table exists for hardware code 0x{hardwareCode:X4}.");
        }

        MtkEfuseAccess.ValidateRange(cells, startIndex, count);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = RequireXmlDaMemoryStage();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var readings = new List<MtkEfuseCellReading>(count);
                for (var offset = 0; offset < count; offset++)
                {
                    var index = checked(startIndex + offset);
                    var cell = MtkEfuseAccess.ResolveCell(cells[index], efuseBase);
                    var bytes = cell.IsReportedWithoutRead
                        ? MtkEfuseAccess.EncodeReportedValue(cell.Address)
                        : await PeekXmlBulkOwnedAsync(channel, cell.Address, MtkEfuseAccess.CellLength,
                            cancellationToken).ConfigureAwait(false);
                    readings.Add(new MtkEfuseCellReading(index, cell, Convert.ToHexString(bytes)));
                }

                var reading = new MtkEfuseReading(hardwareCode, efuseBase, readings);
                RecordSecurityEvidence("xml-efuse-read", CommandOutcomeKind.Completed,
                    $"{readings.Count} cells requested, {reading.ReadCellCount} read from the target.",
                    string.Concat(readings.Select(cell => cell.BytesHex)));
                return reading;
            }
            catch
            {
                FaultXml();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private MtkXmlChannel RequireXmlDaMemoryStage()
    {
        ThrowIfDisposed();
        if (!Transport.IsConnected || _state is not (MtkSessionState.XmlControlReady
            or MtkSessionState.XmlRuntimeConfigured or MtkSessionState.XmlHardwareInitialized
            or MtkSessionState.XmlStageTwoStarted or MtkSessionState.XmlStageTwoHardwareInitialized
            or MtkSessionState.XmlStorageGeometryKnown))
        {
            throw new InvalidOperationException(
                $"The XML custom memory command needs an attached XML DA control stage; current state is {_state}.");
        }

        return _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
    }

    private async Task<byte[]> PeekXmlBulkOwnedAsync(MtkXmlChannel channel, uint address, int length,
        CancellationToken cancellationToken)
    {
        await channel.SendCommandAsync(new XElement("da", new XElement("version", "1.0"),
            new XElement("command", XmlCustomMemoryCommand)), cancellationToken).ConfigureAwait(false);
        await RequireXmlOkAsync(channel, cancellationToken).ConfigureAwait(false);
        await channel.SendStreamAsync(
            new MemoryStream(CreateXmlMemoryWord(address), writable: false), 8, cancellationToken).ConfigureAwait(false);
        await channel.SendStreamAsync(
            new MemoryStream(CreateXmlMemoryWord((uint)length), writable: false), 4, cancellationToken).ConfigureAwait(false);
        var data = await channel.ReceivePayloadAsync(length, cancellationToken).ConfigureAwait(false);
        await CompleteXmlCommandOwnedAsync(channel, cancellationToken).ConfigureAwait(false);
        if (data.Length != length)
        {
            throw new MtkProtocolException(
                $"The XML custom memory read returned {data.Length} bytes for a {length}-byte request.");
        }

        return data;
    }

    private static byte[] CreateXmlMemoryWord(uint value)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, value);
        return payload;
    }
}