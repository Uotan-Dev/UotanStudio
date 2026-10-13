using System.Buffers.Binary;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Download-agent register and memory peek, poke and capture access for the XFlash, XML and Legacy
/// families, mirroring the reference dispatcher that selects one per-session extension per command.
/// <para>面向 XFlash、XML 与 Legacy 家族的下载代理寄存器/内存窥探、写入与采集访问，对应参考分发器
/// 按会话为每条命令所选的单一扩展。</para>
/// </summary>
/// <remarks>
/// <para>The reference dispatcher is <c>peek</c>, <c>peek_reg</c> and <c>poke</c> in
/// Library/DA/mtk_daloader.py:322-336 and :364-372; the per-family extensions are
/// Library/DA/xflash/extension/xflash.py:315-366 and :652-659,
/// Library/DA/xml/extension/v6.py:498-536, :542-590 and :610-616, and
/// Library/DA/legacy/extension/legacy.py:96-141 and :364-365.</para>
/// <para>参考分发器为 Library/DA/mtk_daloader.py:322-336 与 :364-372 中的 <c>peek</c>、
/// <c>peek_reg</c> 与 <c>poke</c>；各家族扩展为
/// Library/DA/xflash/extension/xflash.py:315-366 与 :652-659、
/// Library/DA/xml/extension/v6.py:498-536、:542-590 与 :610-616，以及
/// Library/DA/legacy/extension/legacy.py:96-141 与 :364-365。</para>
/// <para>Unlike the reference, which returns an empty buffer or silently short result when a reply is
/// missing, every path here turns a malformed or truncated reply into a protocol failure and faults the
/// owning family, and every poke refuses before the first byte when the caller did not confirm the
/// write. The reference also defines <c>PWR_READ16_CMD</c> (Library/DA/legacy/dalegacy_param.py:33) and
/// the <c>CMD_READ16/32_*</c> status codes (Library/error.py:60-67) but never issues a sixteen-bit
/// download-agent access, so no such path is offered here.</para>
/// <para>与参考实现不同：参考在回复缺失或过短时返回空缓冲或静默的短结果，而此处每一条路径都把格式错误
/// 或被截断的回复视为协议失败并使所属家族进入 fault 状态；每个 poke 在调用方未确认写入时于首字节之前拒绝。
/// 参考还定义了 <c>PWR_READ16_CMD</c>（Library/DA/legacy/dalegacy_param.py:33）与
/// <c>CMD_READ16/32_*</c> 状态码（Library/error.py:60-67），但从未发出十六位下载代理访问，因此此处不提供
/// 该路径。</para>
/// </remarks>
public sealed partial class MtkSession
{
    private const uint XFlashDaMemoryCustomRead = 0x0F0001;
    private const uint XFlashDaMemoryCustomWrite = 0x0F0003;
    private const string XmlCustomMemoryCommand = "CMD:CUSTOMMEMR";
    private const string XmlCustomRegisterCommand = "CMD:CUSTOMREGR";

    /// <summary>Reads a bounded memory window through the XFlash custom bulk-read code.
    /// <para>通过 XFlash 自定义批量读码读取有界的内存窗口。</para></summary>
    /// <remarks>
    /// The framing is the documented bulk read: device-control selector plus status, the
    /// <c>0x0F0001</c> code plus status, one eight-byte little-endian address frame, one four-byte
    /// length frame, the data frame and a final status
    /// (Library/DA/xflash/extension/xflash.py:315-324 with :302-313 and
    /// Library/DA/xflash/xflash_lib.py:93-106, where the address is sent with <c>is64bit</c>).
    /// 帧结构即已记载的批量读取：器件控制选择器与状态、<c>0x0F0001</c> 控制码与状态、一个八字节小端地址帧、
    /// 一个四字节长度帧、数据帧与最终状态（Library/DA/xflash/extension/xflash.py:315-324，配合 :302-313 与
    /// Library/DA/xflash/xflash_lib.py:93-106，其中地址以 <c>is64bit</c> 发送）。
    /// </remarks>
    /// <param name="address">The start address of the window. 窗口起始地址。</param>
    /// <param name="length">The positive byte count, limited to
    /// <see cref="MtkDaMemoryAccess.MaximumBulkReadBytes"/>. 正数字节数，上限为
    /// <see cref="MtkDaMemoryAccess.MaximumBulkReadBytes"/>。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>Exactly <paramref name="length"/> bytes as the agent delivered them.
    /// <para>恰好 <paramref name="length"/> 字节，即代理交付的原始内容。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The length is out of bounds or the window leaves
    /// the 32-bit address space; no byte was sent. <para>长度越界，或窗口越出 32 位地址空间；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">DA2 has not started or the channel is gone.
    /// <para>DA2 未启动或通道已失效。</para></exception>
    /// <exception cref="MtkProtocolException">A frame, the status or the reply length was invalid; the
    /// XFlash family faults. <para>某帧、状态或回复长度无效；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<byte[]> PeekXFlashMemoryAsync(uint address, int length,
        CancellationToken cancellationToken = default)
    {
        MtkDaMemoryAccess.ValidateBulkReadLength(length);
        RequireDaMemoryWindow(address, length);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = RequireXFlashSecurityCustomStage();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var data = await PeekXFlashBulkOwnedAsync(channel, address, length, cancellationToken)
                    .ConfigureAwait(false);
                RecordSecurityEvidence("da-memory-read", CommandOutcomeKind.Completed,
                    $"xflash bulk peek 0x{address:X8}+0x{length:X}.");
                return data;
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

    /// <summary>Reads a bounded register window through the XFlash custom register-read code.
    /// <para>通过 XFlash 自定义寄存器读码读取有界的寄存器窗口。</para></summary>
    /// <remarks>
    /// The reference walks the window in four-byte steps, issuing one register read per doubleword and
    /// abandoning the loop as soon as one read fails (Library/DA/xflash/extension/xflash.py:652-659 with
    /// :325-332); this path keeps that framing but turns the abandoned tail into a protocol failure
    /// instead of a silently short result, and trims the final doubleword to the requested length the
    /// way the Legacy reader does (Library/DA/legacy/extension/legacy.py:107-115).
    /// 参考实现按四字节步长遍历窗口，每个双字发起一次寄存器读取，并在某次读取失败时立刻放弃循环
    /// （Library/DA/xflash/extension/xflash.py:652-659，配合 :325-332）；本路径保留该帧结构，但把被放弃的尾部
    /// 视为协议失败而不是静默的短结果，并按 Legacy 读取器的方式把最后一个双字裁剪到请求长度
    /// （Library/DA/legacy/extension/legacy.py:107-115）。
    /// </remarks>
    /// <param name="address">The start address of the window. 窗口起始地址。</param>
    /// <param name="length">The positive byte count, limited to
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>. 正数字节数，上限为
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>Exactly <paramref name="length"/> little-endian doublewords.
    /// <para>恰好 <paramref name="length"/> 字节的小端双字。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The length is out of bounds or the window leaves
    /// the 32-bit address space; no byte was sent. <para>长度越界，或窗口越出 32 位地址空间；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">DA2 has not started or the channel is gone.
    /// <para>DA2 未启动或通道已失效。</para></exception>
    /// <exception cref="MtkProtocolException">A register frame or status was invalid; the XFlash family
    /// faults. <para>某个寄存器帧或状态无效；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<byte[]> PeekXFlashRegisterAsync(uint address, int length,
        CancellationToken cancellationToken = default)
    {
        MtkDaMemoryAccess.ValidateWordLoopReadLength(length);
        RequireDaMemoryWindow(address, length);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = RequireXFlashSecurityCustomStage();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var data = await PeekWordsOwnedAsync(
                    (word, token) => ReadXFlashSecurityRegisterAsync(channel, word, token), address, length,
                    cancellationToken).ConfigureAwait(false);
                RecordSecurityEvidence("da-memory-read", CommandOutcomeKind.Completed,
                    $"xflash register peek 0x{address:X8}+0x{length:X}.");
                return data;
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

    /// <summary>Writes a bounded memory window through the XFlash custom bulk-write code.
    /// <para>通过 XFlash 自定义批量写码写入有界的内存窗口。</para></summary>
    /// <remarks>
    /// The framing is the documented bulk write: selector plus status, the <c>0x0F0003</c> code plus
    /// status, one eight-byte little-endian address frame, one four-byte length frame, the data frame
    /// and a final status (Library/DA/xflash/extension/xflash.py:334-342 with :302-313).
    /// Writing device memory is destructive and irreversible, so the exchange is only attempted when
    /// the caller confirms it, exactly like the secure-boot configuration writes.
    /// 帧结构即已记载的批量写入：选择器与状态、<c>0x0F0003</c> 控制码与状态、一个八字节小端地址帧、一个
    /// 四字节长度帧、数据帧与最终状态（Library/DA/xflash/extension/xflash.py:334-342，配合 :302-313）。
    /// 写入设备内存是破坏性且不可逆的操作，因此仅在调用方确认后才发起交换，与安全启动配置写入一致。
    /// </remarks>
    /// <param name="address">The start address of the window. 窗口起始地址。</param>
    /// <param name="data">The payload to write, limited to
    /// <see cref="MtkDaMemoryAccess.MaximumBulkWriteBytes"/>. 要写入的载荷，上限为
    /// <see cref="MtkDaMemoryAccess.MaximumBulkWriteBytes"/>。</param>
    /// <param name="confirmWrite">The explicit caller opt-in that authorizes the write.
    /// <para>授权本次写入的显式调用方选择。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The number of payload bytes submitted. 已提交的载荷字节数。</returns>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmWrite"/> is
    /// <see langword="false"/>; no byte was sent and the session keeps its stage.
    /// <para><paramref name="confirmWrite"/> 为 <see langword="false"/>；未发送任何字节，会话保持原阶段。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The payload is empty, oversized, or the window
    /// leaves the 32-bit address space. <para>载荷为空、超限，或窗口越出 32 位地址空间。</para></exception>
    /// <exception cref="InvalidOperationException">DA2 has not started or the channel is gone.
    /// <para>DA2 未启动或通道已失效。</para></exception>
    /// <exception cref="MtkProtocolException">A frame or the completion status was invalid; the XFlash
    /// family faults. <para>某帧或完成状态无效；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<int> PokeXFlashMemoryAsync(uint address, ReadOnlyMemory<byte> data, bool confirmWrite,
        CancellationToken cancellationToken = default)
    {
        MtkDaMemoryAccess.ValidateBulkWriteLength(data.Length);
        RequireDaMemoryWindow(address, data.Length);
        RequireDaMemoryWriteConfirmation(confirmWrite, "xflash");
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = RequireXFlashSecurityCustomStage();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SendXFlashSecurityControlCodeAsync(channel, XFlashDaMemoryCustomWrite, cancellationToken)
                    .ConfigureAwait(false);
                await SendXFlashDaMemoryAddressAsync(channel, address, cancellationToken).ConfigureAwait(false);
                await SendXFlashWordAsync(channel, (uint)data.Length, cancellationToken).ConfigureAwait(false);
                await channel.SendAsync(1, data.ToArray(), cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "custom memory write", cancellationToken).ConfigureAwait(false);
                RecordSecurityEvidence("da-memory-write", CommandOutcomeKind.Completed,
                    $"xflash poke 0x{address:X8}: {data.Length} bytes written.");
                return data.Length;
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

    /// <summary>Reads a bounded memory window through the Legacy register-read command.
    /// <para>通过 Legacy 寄存器读命令读取有界的内存窗口。</para></summary>
    /// <remarks>
    /// The Legacy extension has no bulk read: <c>custom_read</c> rounds the length up to whole
    /// doublewords and issues one <c>READ_REG32_CMD</c> per doubleword, then trims the tail
    /// (Library/DA/legacy/extension/legacy.py:107-115 with
    /// Library/DA/legacy/dalegacy_lib.py:92-99 and Library/DA/legacy/dalegacy_param.py:31). Each value
    /// arrives big-endian on the wire and is re-emitted little-endian, so the result is byte-reversed
    /// per doubleword relative to the reply.
    /// Legacy 扩展没有批量读取：<c>custom_read</c> 把长度向上取整到完整双字，每个双字发出一条
    /// <c>READ_REG32_CMD</c>，然后裁剪尾部（Library/DA/legacy/extension/legacy.py:107-115，配合
    /// Library/DA/legacy/dalegacy_lib.py:92-99 与 Library/DA/legacy/dalegacy_param.py:31）。每个取值在线路上以大端
    /// 到达并按小端重新输出，因此结果相对回复逐双字反序。
    /// </remarks>
    /// <param name="address">The start address of the window. 窗口起始地址。</param>
    /// <param name="length">The positive byte count, limited to
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>. 正数字节数，上限为
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>Exactly <paramref name="length"/> little-endian bytes.
    /// <para>恰好 <paramref name="length"/> 字节的小端内容。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The length is out of bounds or the window leaves the
    /// 32-bit address space; no byte was sent. <para>长度越界，或窗口越出 32 位地址空间；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">Legacy storage is not ready.
    /// <para>Legacy 存储未就绪。</para></exception>
    /// <exception cref="MtkProtocolException">A register exchange was not acknowledged; the Legacy family
    /// faults. <para>某次寄存器交互未被确认；Legacy 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<byte[]> PeekLegacyMemoryAsync(uint address, int length,
        CancellationToken cancellationToken = default)
    {
        MtkDaMemoryAccess.ValidateWordLoopReadLength(length);
        RequireDaMemoryWindow(address, length);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.LegacyStorageReady);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var data = await PeekLegacyWordsOwnedAsync(address, length, cancellationToken).ConfigureAwait(false);
                RecordSecurityEvidence("da-memory-read", CommandOutcomeKind.Completed,
                    $"legacy peek 0x{address:X8}+0x{length:X}.");
                return data;
            }
            catch
            {
                FaultLegacy();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Reads a bounded register window through the Legacy agent.
    /// <para>通过 Legacy 代理读取有界的寄存器窗口。</para></summary>
    /// <remarks>
    /// The reference register peek is a pure delegation to the memory peek, so the Legacy family has one
    /// single framing for both calls and, unlike the other families, no distinct register framing
    /// (Library/DA/legacy/extension/legacy.py:364-365 delegating to :107-115).
    /// 参考实现的寄存器 peek 只是纯转发到内存 peek，因此 Legacy 家族两个调用共用同一帧结构，与其他家族不同，
    /// 不存在独立的寄存器帧结构（Library/DA/legacy/extension/legacy.py:364-365 转发至 :107-115）。
    /// </remarks>
    /// <param name="address">The start address of the window. 窗口起始地址。</param>
    /// <param name="length">The positive byte count, limited to
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>. 正数字节数，上限为
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>Exactly <paramref name="length"/> little-endian bytes.
    /// <para>恰好 <paramref name="length"/> 字节的小端内容。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The length is out of bounds or the window leaves the
    /// 32-bit address space; no byte was sent. <para>长度越界，或窗口越出 32 位地址空间；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">Legacy storage is not ready.
    /// <para>Legacy 存储未就绪。</para></exception>
    /// <exception cref="MtkProtocolException">A register exchange was not acknowledged; the Legacy family
    /// faults. <para>某次寄存器交互未被确认；Legacy 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public Task<byte[]> PeekLegacyRegisterAsync(uint address, int length,
        CancellationToken cancellationToken = default) => PeekLegacyMemoryAsync(address, length, cancellationToken);

    /// <summary>Writes a bounded memory window through the Legacy register-write command.
    /// <para>通过 Legacy 寄存器写命令写入有界的内存窗口。</para></summary>
    /// <remarks>
    /// The reference write path is <c>custom_write</c> to <c>writemem</c> to <c>writeregister</c>: the
    /// payload is split into doublewords, each decoded little-endian and stored with one
    /// <c>WRITE_REG32_CMD</c> (Library/DA/legacy/extension/legacy.py:130-141 with
    /// Library/DA/legacy/dalegacy_lib.py:101-108 and Library/DA/legacy/dalegacy_param.py:32). A final
    /// partial doubleword is zero-padded and written as a whole word, exactly like the reference, so a
    /// length that is not a multiple of four touches up to three bytes past the payload.
    /// 参考写入路径为 <c>custom_write</c> → <c>writemem</c> → <c>writeregister</c>：载荷拆成双字，每个按小端解码
    /// 并用一条 <c>WRITE_REG32_CMD</c> 存储（Library/DA/legacy/extension/legacy.py:130-141，配合
    /// Library/DA/legacy/dalegacy_lib.py:101-108 与 Library/DA/legacy/dalegacy_param.py:32）。最后一个不完整的双字会
    /// 补零并整体写入，与参考一致，因此非四字节倍数的长度会波及载荷之后最多三个字节。
    /// </remarks>
    /// <param name="address">The start address of the window. 窗口起始地址。</param>
    /// <param name="data">The payload to write, limited to
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopWriteBytes"/>. 要写入的载荷，上限为
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopWriteBytes"/>。</param>
    /// <param name="confirmWrite">The explicit caller opt-in that authorizes the write.
    /// <para>授权本次写入的显式调用方选择。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The number of payload bytes submitted. 已提交的载荷字节数。</returns>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmWrite"/> is
    /// <see langword="false"/>; no byte was sent and the session keeps its stage.
    /// <para><paramref name="confirmWrite"/> 为 <see langword="false"/>；未发送任何字节，会话保持原阶段。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The payload is empty, oversized, or the window leaves
    /// the 32-bit address space. <para>载荷为空、超限，或窗口越出 32 位地址空间。</para></exception>
    /// <exception cref="InvalidOperationException">Legacy storage is not ready.
    /// <para>Legacy 存储未就绪。</para></exception>
    /// <exception cref="MtkProtocolException">A register write was not acknowledged; the Legacy family
    /// faults. <para>某次寄存器写入未被确认；Legacy 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<int> PokeLegacyMemoryAsync(uint address, ReadOnlyMemory<byte> data, bool confirmWrite,
        CancellationToken cancellationToken = default)
    {
        MtkDaMemoryAccess.ValidateWordLoopWriteLength(data.Length);
        RequireDaMemoryWindow(address, checked(data.Length + MtkDaMemoryAccess.RegisterCellBytes));
        RequireDaMemoryWriteConfirmation(confirmWrite, "legacy");
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.LegacyStorageReady);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var payload = data.ToArray();
                var cell = new byte[MtkDaMemoryAccess.RegisterCellBytes];
                for (var offset = 0; offset < payload.Length; offset += MtkDaMemoryAccess.RegisterCellBytes)
                {
                    Array.Clear(cell);
                    payload.AsSpan(offset,
                        Math.Min(MtkDaMemoryAccess.RegisterCellBytes, payload.Length - offset)).CopyTo(cell);
                    await WriteLegacySecurityRegisterAsync(checked(address + (uint)offset),
                        BinaryPrimitives.ReadUInt32LittleEndian(cell), cancellationToken).ConfigureAwait(false);
                }

                RecordSecurityEvidence("da-memory-write", CommandOutcomeKind.Completed,
                    $"legacy poke 0x{address:X8}: {payload.Length} bytes written.");
                return payload.Length;
            }
            catch
            {
                FaultLegacy();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Reads a bounded memory window through the XML custom memory-read command.
    /// <para>通过 XML 自定义内存读取命令读取有界的内存窗口。</para></summary>
    /// <remarks>
    /// The documented exchange is the command element, its <c>OK</c>, one eight-byte little-endian address
    /// frame, one four-byte length frame, the raw data frame, and the closing <c>CMD:END</c> plus the next
    /// <c>CMD:START</c>, each acknowledged (Library/DA/xml/extension/v6.py:498-516 with
    /// Library/DA/xml/xml_cmd.py:18-27 and Library/DA/xml/xml_lib.py:135-152).
    /// 已记载的交互是命令元素及其 <c>OK</c>、一个八字节小端地址帧、一个四字节长度帧、原始数据帧，以及收尾的
    /// <c>CMD:END</c> 与紧随的 <c>CMD:START</c>，每个都需确认
    /// （Library/DA/xml/extension/v6.py:498-516，配合 Library/DA/xml/xml_cmd.py:18-27 与
    /// Library/DA/xml/xml_lib.py:135-152）。
    /// </remarks>
    /// <param name="address">The start address of the window. 窗口起始地址。</param>
    /// <param name="length">The positive byte count, limited to
    /// <see cref="MtkDaMemoryAccess.MaximumBulkReadBytes"/>. 正数字节数，上限为
    /// <see cref="MtkDaMemoryAccess.MaximumBulkReadBytes"/>。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>Exactly <paramref name="length"/> bytes as the agent delivered them.
    /// <para>恰好 <paramref name="length"/> 字节，即代理交付的原始内容。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The length is out of bounds or the window leaves the
    /// 32-bit address space; no byte was sent. <para>长度越界，或窗口越出 32 位地址空间；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">No XML DA control stage is attached.
    /// <para>未附加 XML DA 控制阶段。</para></exception>
    /// <exception cref="MtkProtocolException">The DA rejected or malformed the exchange; the XML family
    /// faults. <para>DA 拒绝或破坏了本次交换；XML 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<byte[]> PeekXmlMemoryAsync(uint address, int length,
        CancellationToken cancellationToken = default)
    {
        MtkDaMemoryAccess.ValidateBulkReadLength(length);
        RequireDaMemoryWindow(address, length);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = RequireXmlDaMemoryStage();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var data = await PeekXmlBulkOwnedAsync(channel, address, length, cancellationToken)
                    .ConfigureAwait(false);
                RecordSecurityEvidence("da-memory-read", CommandOutcomeKind.Completed,
                    $"xml bulk peek 0x{address:X8}+0x{length:X}.");
                return data;
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

    /// <summary>Reads a bounded register window through the XML custom register-read command.
    /// <para>通过 XML 自定义寄存器读取命令读取有界的寄存器窗口。</para></summary>
    /// <remarks>
    /// The reference register peek walks the window in four-byte steps, issuing one
    /// <c>CMD:CUSTOMREGR</c> exchange per doubleword and abandoning the loop when one read fails
    /// (Library/DA/xml/extension/v6.py:516-536); the reply is parsed little-endian and re-emitted
    /// little-endian, so the bytes are kept as received, the tail is trimmed like the Legacy reader, and a
    /// failed word is a protocol fault rather than a short result.
    /// 参考寄存器 peek 按四字节步长遍历窗口，每个双字发起一次 <c>CMD:CUSTOMREGR</c> 交互，并在某次读取失败时
    /// 放弃循环（Library/DA/xml/extension/v6.py:516-536）；回复按小端解析再按小端输出，因此字节保持接收原样，
    /// 尾部像 Legacy 读取器一样裁剪，且失败的字被视为协议故障而不是短结果。
    /// </remarks>
    /// <param name="address">The start address of the window. 窗口起始地址。</param>
    /// <param name="length">The positive byte count, limited to
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>. 正数字节数，上限为
    /// <see cref="MtkDaMemoryAccess.MaximumWordLoopReadBytes"/>。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>Exactly <paramref name="length"/> little-endian bytes.
    /// <para>恰好 <paramref name="length"/> 字节的小端内容。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The length is out of bounds or the window leaves the
    /// 32-bit address space; no byte was sent. <para>长度越界，或窗口越出 32 位地址空间；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">No XML DA control stage is attached.
    /// <para>未附加 XML DA 控制阶段。</para></exception>
    /// <exception cref="MtkProtocolException">A message or reply width was invalid; the XML family faults.
    /// <para>某条消息或回复宽度无效；XML 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<byte[]> PeekXmlRegisterAsync(uint address, int length,
        CancellationToken cancellationToken = default)
    {
        MtkDaMemoryAccess.ValidateWordLoopReadLength(length);
        RequireDaMemoryWindow(address, length);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = RequireXmlDaMemoryStage();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var data = await PeekWordsOwnedAsync(
                    (word, token) => ReadXmlDaMemoryRegisterOwnedAsync(channel, word, token), address, length,
                    cancellationToken).ConfigureAwait(false);
                RecordSecurityEvidence("da-memory-read", CommandOutcomeKind.Completed,
                    $"xml register peek 0x{address:X8}+0x{length:X}.");
                return data;
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

    /// <summary>Writes a bounded memory window through the XML download agent.
    /// <para>通过 XML 下载代理写入有界的内存窗口。</para></summary>
    /// <remarks>
    /// The reference write path reproduces the documented frames — command, <c>OK</c>, address frame,
    /// length frame, data frame, <c>CMD:END</c> and <c>CMD:START</c> — but submits them under the
    /// <i>read</i> command token <c>CMD:CUSTOMMEMR</c> (Library/DA/xml/extension/v6.py:542-558); no write
    /// token for a bulk memory write exists anywhere in the reference source, so this path keeps that
    /// token instead of inventing one. A device that answers the read token with a refusal surfaces as a
    /// protocol fault.
    /// 参考写入路径复现了已记载的帧——命令、<c>OK</c>、地址帧、长度帧、数据帧、<c>CMD:END</c> 与
    /// <c>CMD:START</c>——但把这些帧提交在<i>读取</i>命令标记 <c>CMD:CUSTOMMEMR</c> 之下
    /// （Library/DA/xml/extension/v6.py:542-558）；参考源码中不存在批量内存写入的写入标记，因此本路径保留该标记
    /// 而不是凭空创造。若设备对读取标记回以拒绝，将表现为协议故障。
    /// </remarks>
    /// <param name="address">The start address of the window. 窗口起始地址。</param>
    /// <param name="data">The payload to write, limited to
    /// <see cref="MtkDaMemoryAccess.MaximumBulkWriteBytes"/>. 要写入的载荷，上限为
    /// <see cref="MtkDaMemoryAccess.MaximumBulkWriteBytes"/>。</param>
    /// <param name="confirmWrite">The explicit caller opt-in that authorizes the write.
    /// <para>授权本次写入的显式调用方选择。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The number of payload bytes submitted. 已提交的载荷字节数。</returns>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmWrite"/> is
    /// <see langword="false"/>; no byte was sent and the session keeps its stage.
    /// <para><paramref name="confirmWrite"/> 为 <see langword="false"/>；未发送任何字节，会话保持原阶段。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The payload is empty, oversized, or the window leaves
    /// the 32-bit address space. <para>载荷为空、超限，或窗口越出 32 位地址空间。</para></exception>
    /// <exception cref="InvalidOperationException">No XML DA control stage is attached.
    /// <para>未附加 XML DA 控制阶段。</para></exception>
    /// <exception cref="MtkProtocolException">The DA rejected or malformed the exchange; the XML family
    /// faults. <para>DA 拒绝或破坏了本次交换；XML 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<int> PokeXmlMemoryAsync(uint address, ReadOnlyMemory<byte> data, bool confirmWrite,
        CancellationToken cancellationToken = default)
    {
        MtkDaMemoryAccess.ValidateBulkWriteLength(data.Length);
        RequireDaMemoryWindow(address, data.Length);
        RequireDaMemoryWriteConfirmation(confirmWrite, "xml");
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = RequireXmlDaMemoryStage();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var payload = data.ToArray();
                await channel.SendCommandAsync(CreateXmlDaMemoryCommand(XmlCustomMemoryCommand), cancellationToken)
                    .ConfigureAwait(false);
                await RequireXmlOkAsync(channel, cancellationToken).ConfigureAwait(false);
                await SendXmlDaMemoryAddressAsync(channel, address, cancellationToken).ConfigureAwait(false);
                await SendXmlDaMemoryWordAsync(channel, (uint)payload.Length, cancellationToken).ConfigureAwait(false);
                await channel.SendStreamAsync(new MemoryStream(payload, writable: false), payload.Length,
                    cancellationToken).ConfigureAwait(false);
                await CompleteXmlCommandOwnedAsync(channel, cancellationToken).ConfigureAwait(false);
                RecordSecurityEvidence("da-memory-write", CommandOutcomeKind.Completed,
                    $"xml poke 0x{address:X8}: {payload.Length} bytes written.");
                return payload.Length;
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

    /// <summary>Rejects a device-memory write before any byte leaves the host.
    /// <para>在任何字节离开主机之前拒绝设备内存写入。</para></summary>
    /// <param name="confirmWrite">The caller-supplied opt-in. 调用方提供的选择。</param>
    /// <param name="family">The family named in the refusal. 拒绝信息中指名的家族。</param>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmWrite"/> is
    /// <see langword="false"/>. <para><paramref name="confirmWrite"/> 为 <see langword="false"/>。</para></exception>
    private static void RequireDaMemoryWriteConfirmation(bool confirmWrite, string family)
    {
        if (!confirmWrite)
        {
            throw new UnauthorizedAccessException(
                $"The {family} download agent write changes device memory and requires an explicit confirmation.");
        }
    }

    /// <summary>Checks that a requested window stays inside the 32-bit agent address space and away from
    /// the eFuse window. <para>校验请求的窗口保持在 32 位代理地址空间内，并避开 eFuse 窗口。</para></summary>
    /// <remarks>
    /// The eFuse span is excluded here because peek and poke are the ordinary memory paths: the reference
    /// reads that window only where it names it as its own memory-dump region
    /// (Library/DA/mtk_da_handler.py:794-795 and :808-809), so no window reaching into it is accepted.
    /// The replay-protected area keeps the same discipline from the other side: it is addressed only
    /// through its own commands (MtkRpmbAccess.cs). No write to the eFuse span is offered by any path,
    /// because the reference never sends the OTP program commands it declares
    /// (Library/DA/legacy/dalegacy_param.py:105-110 with the interface variants at :18-23 and :38-43).
    /// 此处排除 eFuse 范围，是因为 peek 与 poke 属于普通内存路径：参考只在该范围被显式命名为内存转储区域时读取它
    /// （Library/DA/mtk_da_handler.py:794-795 与 :808-809），因此此处不接受任何伸入该范围的窗口。
    /// 重放保护区域从另一侧保持同样的纪律：只经其专用命令寻址（MtkRpmbAccess.cs）。任何路径都不提供对
    /// eFuse 范围的写入，因为参考从不发送它声明的 OTP 编程命令
    /// （Library/DA/legacy/dalegacy_param.py:105-110，接口变体见 :18-23 与 :38-43）。
    /// </remarks>
    /// <param name="address">The inclusive start address. 起始（含）地址。</param>
    /// <param name="lengthBytes">The byte count to cover. 需要覆盖的字节数。</param>
    /// <exception cref="ArgumentOutOfRangeException">The end address would leave the 32-bit range, or the
    /// window reaches into the eFuse window. <para>结束地址将越出 32 位范围，或窗口伸入 eFuse 窗口。</para></exception>
    private static void RequireDaMemoryWindow(uint address, int lengthBytes)
    {
        if ((ulong)address + (ulong)(uint)lengthBytes > 0x1_0000_0000UL)
        {
            throw new ArgumentOutOfRangeException(nameof(address), address,
                "The requested range leaves the 32-bit download-agent address space.");
        }

        if (MtkDaMemoryAccess.IntersectsEfuseWindow(address, (long)address + lengthBytes))
        {
            throw new ArgumentOutOfRangeException(nameof(address), address,
                "Ordinary download-agent memory access does not reach the eFuse window; capture it as an " +
                "explicit eFuse region instead.");
        }
    }

    /// <summary>Accepts a caller sub-window or falls back to the documented boot-ROM range.
    /// <para>接受调用方的子窗口，或回退到已记载的 boot-ROM 范围。</para></summary>
    /// <param name="range">The caller range, or <see langword="null"/>. 调用方范围，或 <see langword="null"/>。</param>
    /// <returns>The validated range to walk. 已校验、待遍历的范围。</returns>
    private static MtkDaMemoryRange ResolveDaMemoryRange(MtkDaMemoryRange? range)
    {
        var window = range ?? MtkDaMemoryRange.BromDump;
        window.Validate(nameof(range));
        return window;
    }

    /// <summary>Accepts only the stages where an attached XML DA answers custom commands.
    /// <para>仅接受已附加的 XML DA 会应答自定义命令的阶段。</para></summary>
    /// <returns>The live XML channel. 当前存活的 XML 通道。</returns>
    /// <exception cref="InvalidOperationException">The XML control channel is not attached or the
    /// transport is disconnected. <para>未附加 XML 控制通道，或传输已断开。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
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

    /// <summary>Reads a Legacy window one big-endian register value at a time and re-emits it
    /// little-endian. <para>逐个大端寄存器取值读取 Legacy 窗口，并按小端重新输出。</para></summary>
    /// <param name="address">The start address. 起始地址。</param>
    /// <param name="length">The requested byte count. 请求的字节数。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>Exactly <paramref name="length"/> little-endian bytes.
    /// <para>恰好 <paramref name="length"/> 字节的小端内容。</para></returns>
    private Task<byte[]> PeekLegacyWordsOwnedAsync(uint address, int length, CancellationToken cancellationToken)
        => PeekWordsOwnedAsync(ReadLegacyDaMemoryWordAsync, address, length, cancellationToken);

    /// <summary>Reads one doubleword register through the Legacy register command and re-emits it
    /// little-endian. <para>通过 Legacy 寄存器命令读取一个双字寄存器并按小端重新输出。</para></summary>
    /// <param name="address">The register address. 寄存器地址。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The four little-endian bytes. 四个小端字节。</returns>
    private async Task<byte[]> ReadLegacyDaMemoryWordAsync(uint address, CancellationToken cancellationToken)
    {
        var value = await ReadLegacySecurityRegisterAsync(address, cancellationToken).ConfigureAwait(false);
        var bytes = new byte[MtkDaMemoryAccess.RegisterCellBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>Reads one doubleword register through the XML custom register command.
    /// <para>通过 XML 自定义寄存器命令读取一个双字寄存器。</para></summary>
    /// <param name="channel">The live XML channel. 当前存活的 XML 通道。</param>
    /// <param name="address">The register address. 寄存器地址。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The four reply bytes, which the reference re-emits unchanged.
    /// <para>四个回复字节，参考实现原样重新输出。</para></returns>
    /// <exception cref="MtkProtocolException">The reply width is not one doubleword.
    /// <para>回复宽度不是一个双字。</para></exception>
    private async Task<byte[]> ReadXmlDaMemoryRegisterOwnedAsync(MtkXmlChannel channel, uint address,
        CancellationToken cancellationToken)
    {
        await channel.SendCommandAsync(CreateXmlDaMemoryCommand(XmlCustomRegisterCommand), cancellationToken)
            .ConfigureAwait(false);
        await RequireXmlOkAsync(channel, cancellationToken).ConfigureAwait(false);
        await SendXmlDaMemoryWordAsync(channel, address, cancellationToken).ConfigureAwait(false);
        var data = await channel.ReceivePayloadAsync(MtkDaMemoryAccess.RegisterCellBytes, cancellationToken)
            .ConfigureAwait(false);
        await CompleteXmlCommandOwnedAsync(channel, cancellationToken).ConfigureAwait(false);
        if (data.Length != MtkDaMemoryAccess.RegisterCellBytes)
        {
            throw new MtkProtocolException(
                $"The XML register read reply must be {MtkDaMemoryAccess.RegisterCellBytes} bytes; the agent sent " +
                $"{data.Length}.");
        }

        return data;
    }

    /// <summary>Reads one bulk window through the XML custom memory command.
    /// <para>通过 XML 自定义内存命令读取一个批量窗口。</para></summary>
    /// <param name="channel">The live XML channel. 当前存活的 XML 通道。</param>
    /// <param name="address">The start address. 起始地址。</param>
    /// <param name="length">The requested byte count. 请求的字节数。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The delivered payload. 已交付的载荷。</returns>
    /// <exception cref="MtkProtocolException">The reply length does not match the request.
    /// <para>回复长度与请求不符。</para></exception>
    private async Task<byte[]> PeekXmlBulkOwnedAsync(MtkXmlChannel channel, uint address, int length,
        CancellationToken cancellationToken)
    {
        await channel.SendCommandAsync(CreateXmlDaMemoryCommand(XmlCustomMemoryCommand), cancellationToken)
            .ConfigureAwait(false);
        await RequireXmlOkAsync(channel, cancellationToken).ConfigureAwait(false);
        await SendXmlDaMemoryAddressAsync(channel, address, cancellationToken).ConfigureAwait(false);
        await SendXmlDaMemoryWordAsync(channel, (uint)length, cancellationToken).ConfigureAwait(false);
        var data = await channel.ReceivePayloadAsync(length, cancellationToken).ConfigureAwait(false);
        await CompleteXmlCommandOwnedAsync(channel, cancellationToken).ConfigureAwait(false);
        RequireDaMemoryReplyLength(data.Length, length, "XML custom memory read");
        return data;
    }

    /// <summary>Reads one bulk window through the XFlash custom read code.
    /// <para>通过 XFlash 自定义读取码读取一个批量窗口。</para></summary>
    /// <param name="channel">The live XFlash channel. 当前存活的 XFlash 通道。</param>
    /// <param name="address">The start address. 起始地址。</param>
    /// <param name="length">The requested byte count. 请求的字节数。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The delivered payload. 已交付的载荷。</returns>
    /// <exception cref="MtkProtocolException">The reply length does not match the request.
    /// <para>回复长度与请求不符。</para></exception>
    private async Task<byte[]> PeekXFlashBulkOwnedAsync(MtkXFlashChannel channel, uint address, int length,
        CancellationToken cancellationToken)
    {
        await SendXFlashSecurityControlCodeAsync(channel, XFlashDaMemoryCustomRead, cancellationToken)
            .ConfigureAwait(false);
        await SendXFlashDaMemoryAddressAsync(channel, address, cancellationToken).ConfigureAwait(false);
        await SendXFlashWordAsync(channel, (uint)length, cancellationToken).ConfigureAwait(false);
        var data = await ReadXFlashSecurityPayloadAsync(channel, MtkDaMemoryAccess.MaximumBulkReadBytes,
            cancellationToken).ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, "custom memory read", cancellationToken).ConfigureAwait(false);
        RequireDaMemoryReplyLength(data.Length, length, "XFlash custom memory read");
        return data;
    }

    /// <summary>Reads a window doubleword by doubleword and trims the tail to the requested length.
    /// <para>逐双字读取窗口，并把尾部裁剪到请求长度。</para></summary>
    /// <param name="readWord">The family's doubleword reader. 该家族的双字读取器。</param>
    /// <param name="address">The start address. 起始地址。</param>
    /// <param name="length">The requested byte count. 请求的字节数。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>Exactly <paramref name="length"/> bytes. 恰好 <paramref name="length"/> 字节。</returns>
    /// <exception cref="MtkProtocolException">A word reply was not one doubleword wide.
    /// <para>某次字回复宽度不是一个双字。</para></exception>
    private static async Task<byte[]> PeekWordsOwnedAsync(Func<uint, CancellationToken, Task<byte[]>> readWord,
        uint address, int length, CancellationToken cancellationToken)
    {
        var words = MtkDaMemoryAccess.ComputeWordCount(length);
        var data = new byte[checked(words * MtkDaMemoryAccess.RegisterCellBytes)];
        for (var index = 0; index < words; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await readWord(
                checked(address + (uint)(index * MtkDaMemoryAccess.RegisterCellBytes)), cancellationToken)
                .ConfigureAwait(false);
            RequireDaMemoryReplyLength(bytes.Length, MtkDaMemoryAccess.RegisterCellBytes, "download-agent word read");
            bytes.CopyTo(data.AsSpan(index * MtkDaMemoryAccess.RegisterCellBytes));
        }

        return length == data.Length ? data : data[..length];
    }

    /// <summary>Checks a device reply length against the request that produced it.
    /// <para>把设备回复长度与产生它的请求进行比对。</para></summary>
    /// <param name="received">The reply byte count. 回复字节数。</param>
    /// <param name="expected">The requested byte count. 请求的字节数。</param>
    /// <param name="operation">The operation named in the failure. 失败信息中指名的操作。</param>
    /// <exception cref="MtkProtocolException">The two lengths differ. <para>两个长度不相等。</para></exception>
    private static void RequireDaMemoryReplyLength(int received, int expected, string operation)
    {
        if (received != expected)
        {
            throw new MtkProtocolException(
                $"{operation} returned {received} bytes but {expected} were requested.");
        }
    }

    /// <summary>Sends the little-endian quadword address frame the reference uses for bulk windows.
    /// <para>发送参考实现用于批量窗口的小端八字节地址帧。</para></summary>
    /// <param name="channel">The live XFlash channel. 当前存活的 XFlash 通道。</param>
    /// <param name="address">The start address. 起始地址。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    private static async Task SendXFlashDaMemoryAddressAsync(MtkXFlashChannel channel, uint address,
        CancellationToken cancellationToken)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, address);
        await channel.SendAsync(1, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends one little-endian doubleword stream frame to the XML agent.
    /// <para>向 XML 代理发送一个小端双字流帧。</para></summary>
    /// <param name="channel">The live XML channel. 当前存活的 XML 通道。</param>
    /// <param name="value">The value to send. 要发送的取值。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    private static async Task SendXmlDaMemoryWordAsync(MtkXmlChannel channel, uint value,
        CancellationToken cancellationToken)
    {
        var payload = new byte[MtkDaMemoryAccess.RegisterCellBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
        await channel.SendStreamAsync(new MemoryStream(payload, writable: false), payload.Length, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Sends the little-endian quadword address frame used by the XML bulk commands.
    /// <para>发送 XML 批量命令所用的小端八字节地址帧。</para></summary>
    /// <param name="channel">The live XML channel. 当前存活的 XML 通道。</param>
    /// <param name="address">The start address. 起始地址。</param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    private static async Task SendXmlDaMemoryAddressAsync(MtkXmlChannel channel, uint address,
        CancellationToken cancellationToken)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, address);
        await channel.SendStreamAsync(new MemoryStream(payload, writable: false), payload.Length, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Builds one bare XML custom command element, matching the reference command builder.
    /// <para>构造一条简单的 XML 自定义命令元素，与参考命令构造器一致。</para></summary>
    /// <param name="command">The command token including the <c>CMD:</c> prefix.
    /// <para>含 <c>CMD:</c> 前缀的命令标记。</para></param>
    /// <returns>The command element. 命令元素。</returns>
    private static XElement CreateXmlDaMemoryCommand(string command) => new("da",
        new XElement("version", "1.0"), new XElement("command", command));
}