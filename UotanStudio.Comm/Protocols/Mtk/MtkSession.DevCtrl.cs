using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Security;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Extended device-control queries of the XFlash download agent: RAM inventory, random identifier,
/// storage life percentage, expiry date and partition-table category.
/// <para>XFlash 下载代理的扩展器件控制查询：内存清单、随机标识、存储寿命百分比、过期日期与分区表类别。</para>
/// </summary>
/// <remarks>
/// <para>These are the read-only selectors catalogued by the reference dispatcher (device-control
/// wrapper at Library/DA/xflash/xflash_lib.py:190-206 with Library/DA/xflash/xflash_param.py:14 and
/// the individual getters at :487-505, :567-574, :607-611, :556-563 and :613-622). Every query uses
/// the same bounded device-control framing as the existing chip-identity path, bounds the reply
/// before any payload buffer is allocated and decodes only the layouts the reference documents;
/// where the reference silently tolerates a malformed or unrecognized reply, this dispatcher faults
/// the XFlash family instead of guessing.</para>
/// <para>这些是参考分发器登记的只读选择器（器件控制包装位于
/// Library/DA/xflash/xflash_lib.py:190-206，控制码见 Library/DA/xflash/xflash_param.py:14；各 getter 位于
/// :487-505、:567-574、:607-611、:556-563 与 :613-622）。每个查询都复用与现有芯片标识路径相同的有界器件
/// 控制帧结构，在分配任何载荷缓冲之前限制回复大小，且只解码参考记载的布局；参考会静默容忍格式错误或不认识
/// 的回复，而本分发器改为让 XFlash 家族进入 fault，不做猜测。</para>
/// </remarks>
public sealed partial class MtkSession
{
    private const uint XFlashGetRamInfo = 0x04000C;
    private const uint XFlashGetRandomId = 0x040008;
    private const uint XFlashStorageLifeCycleCheck = 0x080007;
    private const uint XFlashGetExpireDate = 0x040011;
    private const uint XFlashGetPartitionTableCategory = 0x040009;
    private const int MaximumXFlashOpaqueDeviceControlBytes = 0x100;
    private const int XFlashDeviceControlWordBytes = 4;

    /// <summary>Queries the SRAM and DRAM inventory through the documented RAM-info control code.
    /// <para>通过已记载的内存信息控制码查询 SRAM 与 DRAM 清单。</para></summary>
    /// <remarks>
    /// The reference accepts a 24-byte doubleword reply or a 48-byte quadword reply and nothing else
    /// (Library/DA/xflash/xflash_lib.py:487-505); both widths are decoded here, any other width
    /// faults the family, and the receive ceiling matches the widest documented reply so an
    /// oversized declaration is rejected before any payload buffer is allocated.
    /// 参考只接受 24 字节双字回复或 48 字节四字回复，其他一概不接受（Library/DA/xflash/xflash_lib.py:487-505）；
    /// 这里两种宽度都解码，其他宽度使家族进入 fault，接收上限与最宽的已记载回复一致，因此超限声明会在分配
    /// 任何载荷缓冲之前被拒绝。
    /// </remarks>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The decoded regions with the exact reply preserved.
    /// <para>解码后的区域，并保留精确回复。</para></returns>
    /// <exception cref="InvalidOperationException">No XFlash stage-one agent is attached.
    /// <para>未附加 XFlash 一阶段代理。</para></exception>
    /// <exception cref="MtkProtocolException">A status was rejected, the reply was oversized or its
    /// width is undocumented; the XFlash family faults.
    /// <para>某状态被拒绝、回复超限或宽度未记载；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public Task<MtkXFlashRamInfo> GetXFlashRamInfoAsync(CancellationToken cancellationToken = default) =>
        QueryXFlashDeviceControlAsync(XFlashGetRamInfo, 48, "xflash-ram-info",
            reply => MtkXFlashRamInfo.Decode(reply),
            info => $"SRAM type 0x{info.Sram.Type:X} at 0x{info.Sram.BaseAddress:X8}+0x{info.Sram.Size:X}, " +
                    $"DRAM type 0x{info.Dram.Type:X} at 0x{info.Dram.BaseAddress:X8}+0x{info.Dram.Size:X}.",
            cancellationToken);

    /// <summary>Queries the random identifier through the documented random-id control code.
    /// <para>通过已记载的随机标识控制码查询随机标识符。</para></summary>
    /// <remarks>
    /// The reference keeps this reply opaque (Library/DA/xflash/xflash_lib.py:567-574), so the exact
    /// bytes are returned and recorded as evidence without any interpretation; the reply is bounded
    /// to 256 bytes before allocation.
    /// 参考把该回复保持为不透明字节（Library/DA/xflash/xflash_lib.py:567-574），因此这里返回并记录精确字节
    /// 作为证据而不作解释；回复在分配前被限制在 256 字节以内。
    /// </remarks>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The exact reply bytes. 精确的回复字节。</returns>
    /// <exception cref="InvalidOperationException">No XFlash stage-one agent is attached.
    /// <para>未附加 XFlash 一阶段代理。</para></exception>
    /// <exception cref="MtkProtocolException">A status was rejected or the reply exceeds the bound;
    /// the XFlash family faults. <para>某状态被拒绝或回复超限；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public Task<byte[]> GetXFlashRandomIdAsync(CancellationToken cancellationToken = default) =>
        QueryXFlashDeviceControlAsync(XFlashGetRandomId, MaximumXFlashOpaqueDeviceControlBytes,
            "xflash-random-id", reply => reply, reply => $"{reply.Length} opaque bytes", cancellationToken);

    /// <summary>Queries the storage life percentage through the documented life-cycle control code.
    /// <para>通过已记载的生命周期控制码查询存储寿命百分比。</para></summary>
    /// <remarks>
    /// The reference unpacks one little-endian doubleword and, unusually for this dispatcher, never
    /// checks the completion status (Library/DA/xflash/xflash_lib.py:607-611); the strict status
    /// checks of the shared device-control framing are kept here and a rejected status surfaces as a
    /// protocol fault. The reply is bounded to the documented doubleword width before allocation.
    /// 参考解开一个小端双字，并且对该分发器而言异常地不检查完成状态（Library/DA/xflash/xflash_lib.py:607-611）；
    /// 此处保留共享器件控制帧结构的严格状态检查，被拒绝的状态表现为协议 fault。回复在分配前被限制在已记载的
    /// 双字宽度。
    /// </remarks>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The reported life-cycle value. 上报的寿命取值。</returns>
    /// <exception cref="InvalidOperationException">No XFlash stage-one agent is attached.
    /// <para>未附加 XFlash 一阶段代理。</para></exception>
    /// <exception cref="MtkProtocolException">A status was rejected, the reply was oversized or its
    /// width is not one doubleword; the XFlash family faults.
    /// <para>某状态被拒绝、回复超限或宽度不是双字；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public Task<uint> GetXFlashStorageLifeCycleCheckAsync(CancellationToken cancellationToken = default) =>
        QueryXFlashDeviceControlAsync(XFlashStorageLifeCycleCheck, XFlashDeviceControlWordBytes,
            "xflash-storage-life", DecodeXFlashWord, value => $"Life value {value}.", cancellationToken);

    /// <summary>Queries the device expiry date through the documented expire-date control code.
    /// <para>通过已记载的过期日期控制码查询设备过期日期。</para></summary>
    /// <remarks>
    /// The reference keeps this reply as raw bytes for the host to render
    /// (Library/DA/xflash/xflash_lib.py:556-563), so no calendar interpretation is attempted here;
    /// the reply is bounded to 256 bytes before allocation.
    /// 参考把该回复保留为原始字节交由主机渲染（Library/DA/xflash/xflash_lib.py:556-563），因此这里不作任何
    /// 日历解释；回复在分配前被限制在 256 字节以内。
    /// </remarks>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The exact reply bytes. 精确的回复字节。</returns>
    /// <exception cref="InvalidOperationException">No XFlash stage-one agent is attached.
    /// <para>未附加 XFlash 一阶段代理。</para></exception>
    /// <exception cref="MtkProtocolException">A status was rejected or the reply exceeds the bound;
    /// the XFlash family faults. <para>某状态被拒绝或回复超限；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public Task<byte[]> GetXFlashExpireDateAsync(CancellationToken cancellationToken = default) =>
        QueryXFlashDeviceControlAsync(XFlashGetExpireDate, MaximumXFlashOpaqueDeviceControlBytes,
            "xflash-expire-date", reply => reply, reply => $"{reply.Length} raw bytes", cancellationToken);

    /// <summary>Queries whether the target organizes storage with the GUID or the proprietary
    /// partition table. <para>查询目标使用 GUID 分区表还是专有分区表组织存储。</para></summary>
    /// <remarks>
    /// The reference unpacks one little-endian doubleword, maps 0x64 to GPT and 0x65 to PMT, and
    /// reports a bare zero for everything else (Library/DA/xflash/xflash_lib.py:613-622); an
    /// unmapped value or a wrong reply width faults the family here instead. The reply is bounded to
    /// the documented doubleword width before allocation.
    /// 参考解开一个小端双字，把 0x64 映射为 GPT、0x65 映射为 PMT，其余取值仅上报 0
    /// （Library/DA/xflash/xflash_lib.py:613-622）；此处对无法映射的取值或错误宽度会让家族进入 fault。回复
    /// 在分配前被限制在已记载的双字宽度。
    /// </remarks>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The documented category. 已记载的类别。</returns>
    /// <exception cref="InvalidOperationException">No XFlash stage-one agent is attached.
    /// <para>未附加 XFlash 一阶段代理。</para></exception>
    /// <exception cref="MtkProtocolException">A status was rejected, the reply was oversized, its
    /// width is not one doubleword or the category is unknown; the XFlash family faults.
    /// <para>某状态被拒绝、回复超限、宽度不是双字或类别未知；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public Task<MtkPartitionTableCategory> GetXFlashPartitionTableCategoryAsync(
        CancellationToken cancellationToken = default) =>
        QueryXFlashDeviceControlAsync(XFlashGetPartitionTableCategory, XFlashDeviceControlWordBytes,
            "xflash-partition-category", DecodeXFlashPartitionTableCategory,
            category => $"Partition table category {category}.", cancellationToken);

    private async Task<T> QueryXFlashDeviceControlAsync<T>(uint controlCode, int maximumPayload,
        string evidencePhase, Func<byte[], T> decode, Func<T, string> describe,
        CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireXFlashSecurityControlStage();
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reply = await QueryXFlashControlAsync(channel, controlCode, cancellationToken,
                    maximumPayload).ConfigureAwait(false);
                var value = decode(reply);
                RecordSecurityEvidence(evidencePhase, CommandOutcomeKind.Completed,
                    $"Control code 0x{controlCode:X8}: {describe(value)}.", Convert.ToHexString(reply));
                return value;
            }
            catch
            {
                FaultXFlashSecurity();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private static uint DecodeXFlashWord(byte[] reply)
    {
        if (reply.Length != XFlashDeviceControlWordBytes)
        {
            throw new MtkProtocolException(
                $"The device-control word reply must be {XFlashDeviceControlWordBytes} bytes; the agent " +
                $"returned {reply.Length}.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(reply);
    }

    private static MtkPartitionTableCategory DecodeXFlashPartitionTableCategory(byte[] reply)
    {
        var value = DecodeXFlashWord(reply);
        if (!Enum.IsDefined((MtkPartitionTableCategory)value))
        {
            throw new MtkProtocolException(
                $"The agent reported unknown partition-table category 0x{value:X8}.", value);
        }

        return (MtkPartitionTableCategory)value;
    }
}