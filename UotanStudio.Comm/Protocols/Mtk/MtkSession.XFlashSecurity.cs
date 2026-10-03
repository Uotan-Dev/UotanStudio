using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Security;
using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Queries the chip identity reported by the initialized XFlash download agent.
    /// <para>查询已初始化 XFlash 下载代理上报的芯片身份。</para></summary>
    /// <remarks>
    /// This is a read-only device-control query; it writes no register, unlocks no zone and changes
    /// no lock state. The reply is decoded only as far as the documented five-word layout allows and
    /// is also recorded verbatim as session evidence.
    /// 这是只读的器件控制查询：不写寄存器、不解锁任何区域、不更改锁定状态。回复仅按已记载的 5 个字
    /// 布局解码，并同时以原文记录为会话证据。
    /// </remarks>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The decoded identity together with the exact reply bytes.
    /// <para>解码后的身份信息及其精确回复字节。</para></returns>
    /// <exception cref="InvalidOperationException">No XFlash stage-one agent is attached or the
    /// transport is disconnected. <para>未附加 XFlash 一阶段代理或传输已断开。</para></exception>
    /// <exception cref="MtkProtocolException">The reply length or a status frame is wrong; the
    /// XFlash family faults. <para>回复长度或某个状态帧不正确；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkXFlashChipId> GetXFlashChipIdAsync(CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireXFlashSecurityControlStage();
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reply = await QueryXFlashControlAsync(channel, XFlashChipIdControlCode, cancellationToken)
                    .ConfigureAwait(false);
                var identity = MtkXFlashChipId.Decode(reply);
                RecordSecurityEvidence("xflash-chip-id", CommandOutcomeKind.Completed,
                    $"HW 0x{identity.HardwareCode:X4} reported by control code 0x{XFlashChipIdControlCode:X8}.",
                    identity.ReplyHex);
                return identity;
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

    /// <summary>Runs one documented security control code whose reply the reference client keeps opaque.
    /// <para>执行一个已记载的安全控制码，其回复在参考客户端中保持不透明。</para></summary>
    /// <remarks>
    /// Only <see cref="MtkXFlashRawControlCode"/> values are accepted. Both are pure queries in the
    /// reference client (Library/DA/xflash/xflash_lib.py:590-596 and :687-695) and neither reply is
    /// interpreted there, so the bytes are returned unchanged and recorded as evidence.
    /// 只接受 <see cref="MtkXFlashRawControlCode"/> 取值。两者在参考客户端里都是纯查询
    /// （Library/DA/xflash/xflash_lib.py:590-596 与 :687-695），且都不解释其回复，因此字节原样返回并
    /// 记录为证据。
    /// </remarks>
    /// <param name="code">The documented read-only control code. 已记载的只读控制码。</param>
    /// <param name="maximumPayload">The receive ceiling checked before the reply is buffered.
    /// <para>在缓存回复之前校验的接收上限。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The exact reply bytes. 精确的回复字节。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="code"/> is undefined or
    /// <paramref name="maximumPayload"/> is not positive.
    /// <para><paramref name="code"/> 未定义或 <paramref name="maximumPayload"/> 不是正数。</para></exception>
    /// <exception cref="InvalidOperationException">No XFlash stage-one agent is attached.
    /// <para>未附加 XFlash 一阶段代理。</para></exception>
    /// <exception cref="MtkProtocolException">A status or frame is malformed; the XFlash family faults.
    /// <para>某个状态或帧格式错误；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<byte[]> QueryXFlashSecurityControlCodeAsync(MtkXFlashRawControlCode code,
        int maximumPayload = 64 * 1024, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayload);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireXFlashSecurityControlStage();
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reply = await QueryXFlashControlAsync(channel, (uint)code, cancellationToken, maximumPayload)
                    .ConfigureAwait(false);
                RecordSecurityEvidence("xflash-security-control", CommandOutcomeKind.Completed,
                    $"Control code 0x{(uint)code:X8} returned {reply.Length} opaque bytes.",
                    Convert.ToHexString(reply));
                return reply;
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

    /// <summary>Reads eFuse cells through the stage-two register window documented by the reference client.
    /// <para>通过参考客户端记载的二阶段寄存器窗口读取 eFuse 单元。</para></summary>
    /// <remarks>
    /// Every cell is resolved with <see cref="MtkEfuseFieldTable"/> and
    /// <see cref="MtkEfuseAccess.ResolveCell"/>; cells the documented reader treats as non-addresses are
    /// reported without a device read, exactly as the reference does
    /// (Library/DA/xflash/extension/xflash.py:638-650). Reads use the documented register-read code
    /// (Library/DA/xflash/extension/xflash.py:325-332) and write nothing.
    /// 每个单元都由 <see cref="MtkEfuseFieldTable"/> 与 <see cref="MtkEfuseAccess.ResolveCell"/> 解析；
    /// 已记载读取器视为非地址的单元在不读取设备的情况下上报，与参考实现一致
    /// （Library/DA/xflash/extension/xflash.py:638-650）。读取使用已记载的寄存器读码
    /// （Library/DA/xflash/extension/xflash.py:325-332），不执行任何写入。
    /// </remarks>
    /// <param name="hardwareCode">The hardware code selecting the cell table.
    /// <para>用于选择单元表的硬件代码。</para></param>
    /// <param name="efuseBase">The eFuse base address supplied by the device profile.
    /// <para>由设备配置提供的 eFuse 基址。</para></param>
    /// <param name="startIndex">The first cell index to read. 要读取的首个单元索引。</param>
    /// <param name="count">The number of cells to read, at most
    /// <see cref="MtkEfuseAccess.MaximumRequestedCells"/>.
    /// <para>要读取的单元数量，最多 <see cref="MtkEfuseAccess.MaximumRequestedCells"/>。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The per-cell readings in table order. 按表顺序排列的逐单元读取结果。</returns>
    /// <exception cref="NotSupportedException"><paramref name="hardwareCode"/> has no transcribed
    /// cell table; the nearest chip's table is not substituted.
    /// <para><paramref name="hardwareCode"/> 没有转录的单元表；不会用最近芯片的表替代。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The requested range is empty, oversized or
    /// outside the table. <para>请求范围为空、超限或不在表内。</para></exception>
    /// <exception cref="InvalidOperationException">DA2 has not started.
    /// <para>DA2 未启动。</para></exception>
    /// <exception cref="MtkProtocolException">A register reply is malformed; the XFlash family faults.
    /// <para>某个寄存器回复格式错误；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkEfuseReading> ReadXFlashEfuseCellsAsync(ushort hardwareCode, uint efuseBase,
        int startIndex, int count, CancellationToken cancellationToken = default)
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
            var channel = RequireXFlashSecurityCustomStage();
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
                        : await ReadXFlashSecurityRegisterAsync(channel, cell.Address, cancellationToken).ConfigureAwait(false);
                    readings.Add(new MtkEfuseCellReading(index, cell, Convert.ToHexString(bytes)));
                }

                var reading = new MtkEfuseReading(hardwareCode, efuseBase, readings);
                RecordSecurityEvidence("xflash-efuse-read", CommandOutcomeKind.Completed,
                    $"{readings.Count} cells requested, {reading.ReadCellCount} read from the target.",
                    string.Concat(readings.Select(cell => cell.BytesHex)));
                return reading;
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

    /// <summary>Reads the boot public key window at the documented eFuse offset.
    /// <para>在已记载的 eFuse 偏移处读取引导公钥窗口。</para></summary>
    /// <remarks>
    /// The reference client peeks the 0x20 bytes starting at the eFuse base plus 0x90
    /// (Library/DA/xflash/extension/xflash.py:630-636). The window is fetched as eight little-endian
    /// register doublewords, which is how the documented memory read assembles any longer request
    /// (Library/DA/xflash/extension/xflash.py:353-370).
    /// 参考客户端窥探从 eFuse 基址加 0x90 开始的 0x20 字节
    /// （Library/DA/xflash/extension/xflash.py:630-636）。该窗口按 8 个小端寄存器双字取回，这正是已记载
    /// 内存读取拼装较长请求的方式（Library/DA/xflash/extension/xflash.py:353-370）。
    /// </remarks>
    /// <param name="efuseBase">The eFuse base address supplied by the device profile.
    /// <para>由设备配置提供的 eFuse 基址。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The exact key bytes. 精确的公钥字节。</returns>
    /// <exception cref="OverflowException">The base plus the documented offset would leave the 32-bit
    /// address space. <para>基址加上已记载偏移会超出 32 位地址空间。</para></exception>
    /// <exception cref="InvalidOperationException">DA2 has not started.
    /// <para>DA2 未启动。</para></exception>
    /// <exception cref="MtkProtocolException">A register reply is malformed; the XFlash family faults.
    /// <para>某个寄存器回复格式错误；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<byte[]> ReadXFlashBootPublicKeyAsync(uint efuseBase, CancellationToken cancellationToken = default)
    {
        var address = checked(efuseBase + MtkEfuseAccess.PublicKeyOffset);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = RequireXFlashSecurityCustomStage();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var key = new byte[MtkEfuseAccess.PublicKeyLength];
                for (var offset = 0; offset < key.Length; offset += MtkEfuseAccess.CellLength)
                {
                    var bytes = await ReadXFlashSecurityRegisterAsync(channel, checked((uint)(address + offset)), cancellationToken)
                        .ConfigureAwait(false);
                    bytes.CopyTo(key.AsSpan(offset));
                }

                RecordSecurityEvidence("xflash-public-key-read", CommandOutcomeKind.Completed,
                    $"0x{key.Length:X} bytes read from 0x{address:X8}.", Convert.ToHexString(key));
                return key;
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

    /// <summary>Reads and decodes the secure-boot configuration from the named GPT partition.
    /// <para>从指定 GPT 分区读取并解码安全启动配置。</para></summary>
    /// <remarks>
    /// The partition is located by the documented exact name and read in full, then decoded by
    /// <see cref="MtkSecCfg"/> (Library/DA/xflash/extension/xflash.py:564-594). The exchange writes
    /// nothing to the device, and an unrecognized header faults the family instead of being
    /// reinterpreted.
    /// 按已记载的精确名称定位分区并整体读取，随后由 <see cref="MtkSecCfg"/> 解码
    /// （Library/DA/xflash/extension/xflash.py:564-594）。本交互不向设备写入任何内容；无法识别的头部会使
    /// 家族进入 fault，而不会被重新解释。
    /// </remarks>
    /// <param name="partitionId">The discovered physical region holding the configuration GPT.
    /// <para>承载配置 GPT 的已发现物理区域。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The decoded view bound to the partition and the exact bytes read.
    /// <para>与分区及实际读取字节绑定的解码视图。</para></returns>
    /// <exception cref="InvalidOperationException">Storage has not been discovered or the region is
    /// unknown. <para>存储尚未发现或该区域未知。</para></exception>
    /// <exception cref="NotSupportedException">The located range exceeds the supported bound.
    /// <para>定位到的范围超过支持上限。</para></exception>
    /// <exception cref="MtkProtocolException">The image has no documented header or neither decoder
    /// accepts it; the XFlash family faults.
    /// <para>镜像不含已记载头部或两个解码器都不接受；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkSecCfgView> ReadXFlashSecCfgAsync(uint partitionId, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                return (await ReadXFlashSecCfgOwnedAsync(partitionId, cancellationToken).ConfigureAwait(false)).View;
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

    /// <summary>Closes or opens the secure-boot configuration on the located partition.
    /// <para>在定位到的分区上关闭或打开安全启动配置。</para></summary>
    /// <remarks>
    /// The current image is read, decoded, rewritten with the two documented state values and written
    /// back (Library/Hardware/seccfg.py:78-102 with Library/DA/xflash/extension/xflash.py:595-601).
    /// Writing is destructive and irreversible, so it only proceeds when
    /// <paramref name="confirmWrite"/> is <see langword="true"/>; a false value performs the read and
    /// the whole computation and then stops without touching the partition.
    /// 先读取当前镜像、解码、用两个已记载状态值重写，再写回
    /// （Library/Hardware/seccfg.py:78-102，配合 Library/DA/xflash/extension/xflash.py:595-601）。
    /// 写入是破坏性且不可逆的操作，因此仅在 <paramref name="confirmWrite"/> 为 <see langword="true"/> 时执行；
    /// 传入 false 时会完成读取与全部计算，然后停止而不触碰分区。
    /// </remarks>
    /// <param name="intent">The documented state transition. 已记载的状态转换。</param>
    /// <param name="partitionId">The discovered physical region holding the configuration GPT.
    /// <para>承载配置 GPT 的已发现物理区域。</para></param>
    /// <param name="confirmWrite">The explicit caller opt-in that authorizes the partition write.
    /// <para>授权分区写入的显式调用方选择。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The write receipt when authorized; a refusal throws instead of returning a partial
    /// result. <para>已授权时返回写入回执；拒绝会抛出异常，而不是返回部分结果。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="intent"/> is undefined.
    /// <para><paramref name="intent"/> 未定义。</para></exception>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmWrite"/> is
    /// <see langword="false"/>; no bytes were written and the session keeps its stage.
    /// <para><paramref name="confirmWrite"/> 为 <see langword="false"/>；未写入任何字节，会话保持原阶段。</para></exception>
    /// <exception cref="NotSupportedException">The layout needs a hardware integrity transform, the
    /// declared size contradicts the documented layout, or the located range exceeds the four MiB
    /// ceiling; the session keeps its stage in every case.
    /// <para>该布局需要硬件完整性变换、声明大小与已记载布局矛盾，或定位到的范围超过 4 MiB 上限；
    /// 每种情况下会话都保持原阶段。</para></exception>
    /// <exception cref="InvalidOperationException">The configuration already has the requested state.
    /// <para>配置已处于请求的状态。</para></exception>
    /// <exception cref="MtkProtocolException">Any exchange failed; the XFlash family faults.
    /// <para>任一交互失败；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkSecCfgWriteResult> ApplyXFlashSecCfgAsync(MtkSecCfgLockIntent intent, uint partitionId,
        bool confirmWrite, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(intent))
        {
            throw new ArgumentOutOfRangeException(nameof(intent));
        }

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var (view, image) = await ReadXFlashSecCfgOwnedAsync(partitionId, cancellationToken).ConfigureAwait(false);
                if (view.Version4 is null)
                {
                    throw new NotSupportedException(
                        "The version 3 layout cannot be rewritten off-device because its integrity transform lives in the target SEJ engine.");
                }

                var replacement = MtkSecCfg.CreateVersion4(view.Version4, intent);
                return await WriteXFlashSecCfgOwnedAsync(view, replacement, confirmWrite, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsSecurityPolicyRefusal(exception))
            {
                RecordSecurityEvidence("xflash-seccfg-apply", CommandOutcomeKind.Rejected,
                    $"The secure-boot configuration request was refused before any byte was sent: {exception.Message}");
                throw;
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

    /// <summary>Writes a caller-supplied secure-boot configuration image to the located partition.
    /// <para>把调用方提供的安全启动配置镜像写入定位到的分区。</para></summary>
    /// <remarks>
    /// This is the path for layouts that cannot be rewritten off-device, such as version 3 images
    /// whose body is protected by the target's SEJ engine. The image header and alignment are
    /// validated with <see cref="MtkSecCfg.ValidateWriteCandidate"/> and the partition location comes
    /// from the live GPT, but the body itself is used exactly as supplied: nothing is patched,
    /// re-hashed or verified against the previous content, and no read-back is implied
    /// (Library/DA/xflash/extension/xflash.py:598-601 performs the same unconditional write).
    /// 这是无法在设备外重写的布局所使用的路径，例如其主体由目标 SEJ 引擎保护的版本 3 镜像。镜像头部与
    /// 对齐会用 <see cref="MtkSecCfg.ValidateWriteCandidate"/> 校验，分区位置来自实时 GPT，但主体按所提供
    /// 的样子使用：不打补丁、不重新计算摘要、不与旧内容比较，也不隐含回读校验
    /// （Library/DA/xflash/extension/xflash.py:598-601 同样执行无条件写入）。
    /// </remarks>
    /// <param name="image">A readable stream positioned at the first image byte; it remains owned by
    /// the caller. <para>定位在首个镜像字节处的可读流；所有权仍属于调用方。</para></param>
    /// <param name="length">The positive image byte count, a multiple of 0x200 and no larger than the
    /// four MiB ceiling the reader applies to a secure-boot configuration partition.
    /// <para>正数镜像字节数，必须为 0x200 的整数倍，且不超过读取器对安全启动配置分区施用的 4 MiB 上限。</para></param>
    /// <param name="partitionId">The discovered physical region holding the configuration GPT.
    /// <para>承载配置 GPT 的已发现物理区域。</para></param>
    /// <param name="confirmWrite">The explicit caller opt-in that authorizes the partition write.
    /// <para>授权分区写入的显式调用方选择。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The write receipt when authorized; a refusal throws instead of returning a partial
    /// result. <para>已授权时返回写入回执；拒绝会抛出异常，而不是返回部分结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is null.
    /// <para><paramref name="image"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException">The source is unreadable or shorter than
    /// <paramref name="length"/>, or its header is not a documented layout.
    /// <para>源不可读或短于 <paramref name="length"/>，或其头部不属于已记载的布局。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not positive or
    /// exceeds the ceiling; no buffer is sized before that check.
    /// <para><paramref name="length"/> 不是正数或超过上限；校验之前不会分配任何缓冲区。</para></exception>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmWrite"/> is
    /// <see langword="false"/>; no bytes were written and the session keeps its stage.
    /// <para><paramref name="confirmWrite"/> 为 <see langword="false"/>；未写入任何字节，会话保持原阶段。</para></exception>
    /// <exception cref="MtkProtocolException">Any exchange failed; the XFlash family faults.
    /// <para>任一交互失败；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkSecCfgWriteResult> WriteXFlashSecCfgAsync(Stream image, int length, uint partitionId,
        bool confirmWrite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        MtkXFlashChannel.ValidateSource(image, length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaximumSecCfgBytes);
        var buffer = new byte[length];
        await image.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        var format = MtkSecCfg.ValidateWriteCandidate(buffer);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var view = (await ReadXFlashSecCfgOwnedAsync(partitionId, cancellationToken).ConfigureAwait(false)).View;
                if (view.Format != format)
                {
                    throw new ArgumentException(
                        $"The supplied image is a {format} layout but the partition holds {view.Format}.", nameof(image));
                }

                return await WriteXFlashSecCfgOwnedAsync(view, buffer, confirmWrite, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsSecurityPolicyRefusal(exception))
            {
                RecordSecurityEvidence("xflash-seccfg-write", CommandOutcomeKind.Rejected,
                    $"The supplied {format} image was refused before any byte was written.");
                throw;
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

    /// <summary>Hands one documented key class to the stage-two agent.
    /// <para>把一类已记载的密钥交给二阶段代理。</para></summary>
    /// <remarks>
    /// A software one-time-program key is written as eight little-endian doublewords through the
    /// register window at the SEJ base (Library/Hardware/hwcrypto_sej.py:66-73 and :613-622 with the
    /// register write framing at Library/DA/xflash/extension/xflash.py:344-351). A replay-protected
    /// key is submitted with the set-key control code and the agent must echo it back unchanged
    /// (Library/DA/xflash/extension/xflash.py:442-447). Both write device state, so both require
    /// <paramref name="confirmWrite"/>; without it the request is validated and refused before any
    /// byte is sent. Key material is never derived, padded or logged here.
    /// 软件一次性编程密钥按 8 个小端双字经由 SEJ 基址处的寄存器窗口写入
    /// （Library/Hardware/hwcrypto_sej.py:66-73 与 :613-622，寄存器写入帧结构见
    /// Library/DA/xflash/extension/xflash.py:344-351）。重放保护密钥用设置密钥控制码提交，且代理必须原样
    /// 回显（Library/DA/xflash/extension/xflash.py:442-447）。两者都会写入设备状态，因此都要求
    /// <paramref name="confirmWrite"/>；未授权时请求只会被校验并在发送任何字节前拒绝。此处绝不推导、补齐
    /// 或记录密钥材料。
    /// </remarks>
    /// <param name="request">The validated key class request. 已校验的密钥类别请求。</param>
    /// <param name="confirmWrite">The explicit caller opt-in that authorizes the device write.
    /// <para>授权设备写入的显式调用方选择。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A receipt naming the class, the key digest and what was sent.
    /// <para>写明类别、密钥摘要与所发送内容的回执。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.
    /// <para><paramref name="request"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException">The key length, class or base address does not match the
    /// documented class. <para>密钥长度、类别或基址与已记载类别不符。</para></exception>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmWrite"/> is
    /// <see langword="false"/>; no bytes were sent.
    /// <para><paramref name="confirmWrite"/> 为 <see langword="false"/>；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">DA2 has not started.
    /// <para>DA2 未启动。</para></exception>
    /// <exception cref="MtkProtocolException">A frame was rejected or the submitted key was not
    /// echoed; the XFlash family faults.
    /// <para>某个帧被拒绝或提交的密钥未被回显；XFlash 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkOemKeyReceipt> ProgramXFlashOemKeyAsync(MtkOemKeyRequest request, bool confirmWrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = request.DecodeKey();
        if (!confirmWrite)
        {
            throw new UnauthorizedAccessException(
                "Key programming changes device state and requires an explicit confirmation.");
        }

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = RequireXFlashSecurityCustomStage();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return request.Kind switch
                {
                    MtkOemKeyKind.SoftwareOneTimeProgramKey => await ProgramSejKeyOwnedAsync(
                        channel, request.SejBaseAddress!.Value, key, cancellationToken).ConfigureAwait(false),
                    _ => await ProgramRpmbKeyOwnedAsync(channel, key, cancellationToken).ConfigureAwait(false)
                };
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

    private async Task<MtkOemKeyReceipt> ProgramSejKeyOwnedAsync(MtkXFlashChannel channel, uint sejBase,
        byte[] key, CancellationToken cancellationToken)
    {
        var addresses = new List<uint>(MtkOemKeyRegisterWindow.SlotCount);
        for (var slot = 0; slot < MtkOemKeyRegisterWindow.SlotCount; slot++)
        {
            var address = checked(sejBase + MtkOemKeyRegisterWindow.Offsets[slot]);
            await WriteXFlashSecurityRegisterAsync(channel, address,
                BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(slot * MtkEfuseAccess.CellLength)), cancellationToken)
                .ConfigureAwait(false);
            addresses.Add(address);
        }

        var receipt = new MtkOemKeyReceipt(MtkOemKeyKind.SoftwareOneTimeProgramKey,
            Convert.ToHexString(SHA256.HashData(key)), addresses.Count, null);
        RecordSecurityEvidence("xflash-key-program", CommandOutcomeKind.Completed,
            $"Wrote {addresses.Count} SEJ registers from 0x{addresses[0]:X8}.",
            Convert.ToHexString([.. addresses.SelectMany(address => BitConverter.GetBytes(address))]));
        return receipt;
    }

    private async Task<MtkOemKeyReceipt> ProgramRpmbKeyOwnedAsync(MtkXFlashChannel channel, byte[] key,
        CancellationToken cancellationToken)
    {
        await SendXFlashSecurityControlCodeAsync(channel, XFlashSecurityCustomSetKey, cancellationToken).ConfigureAwait(false);
        await channel.SendAsync(1, key, cancellationToken).ConfigureAwait(false);
        var echoed = await ReadXFlashSecurityPayloadAsync(channel, MtkOemKeyRequest.KeyLength, cancellationToken)
            .ConfigureAwait(false);
        await ReadXFlashStatusAsync(channel, "key submission", cancellationToken).ConfigureAwait(false);
        if (!echoed.AsSpan().SequenceEqual(key))
        {
            RecordSecurityEvidence("xflash-key-program", CommandOutcomeKind.Rejected,
                "The agent did not echo the submitted replay-protected key unchanged.", Convert.ToHexString(echoed));
            throw new MtkProtocolException("The submitted key was not echoed unchanged; the program result is unknown.");
        }

        var receipt = new MtkOemKeyReceipt(MtkOemKeyKind.ReplayProtectedAreaKey,
            Convert.ToHexString(SHA256.HashData(key)), 1, Convert.ToHexString(echoed));
        RecordSecurityEvidence("xflash-key-program", CommandOutcomeKind.Completed,
            "The agent echoed the submitted replay-protected key unchanged.", Convert.ToHexString(echoed));
        return receipt;
    }

    private async Task<(MtkSecCfgView View, byte[] Image)> ReadXFlashSecCfgOwnedAsync(uint partitionId,
        CancellationToken cancellationToken)
    {
        var snapshot = await ReadXFlashGptOwnedAsync(partitionId, cancellationToken).ConfigureAwait(false);
        var entry = ResolveSecCfgPartition(snapshot.Table);
        using var buffer = new MemoryStream();
        await ReadXFlashStorageOwnedAsync(partitionId, entry.ByteOffset, entry.ByteLength, buffer, null,
            cancellationToken).ConfigureAwait(false);
        var image = buffer.ToArray();
        if (image.Length != entry.ByteLength)
        {
            throw new MtkProtocolException("The secure-boot configuration read was incomplete.");
        }

        var view = DescribeSecCfgImage(image, entry, partitionId);
        RecordSecurityEvidence("xflash-seccfg-read", CommandOutcomeKind.Completed,
            $"{view.Format} configuration read from partition {view.PartitionName} (GPT entry {view.PartitionIndex}).",
            view.ImageSha256Hex);
        return (view, image);
    }

    private async Task<MtkSecCfgWriteResult> WriteXFlashSecCfgOwnedAsync(MtkSecCfgView view, byte[] image,
        bool confirmWrite, CancellationToken cancellationToken)
    {
        if (!confirmWrite)
        {
            throw new UnauthorizedAccessException(
                "Writing a secure-boot configuration changes device state and requires an explicit confirmation.");
        }

        RequireState(MtkSessionState.XFlashStorageReady);
        var storage = XFlashStorage ?? throw new InvalidOperationException("Storage has not been discovered.");
        var offset = view.ByteOffset;
        var length = image.LongLength;
        storage.ValidateRange(view.PartitionId, offset, length);
        await WriteXFlashRangeAsync(CreateXFlashStorageParameters(storage, view.PartitionId, offset, length),
            storage.WritePacketLength, length, new MemoryStream(image, writable: false), null, cancellationToken)
            .ConfigureAwait(false);
        var result = new MtkSecCfgWriteResult(view.PartitionName, view.PartitionIndex, offset, length, view.Format,
            Convert.ToHexString(SHA256.HashData(image)));
        RecordSecurityEvidence("xflash-seccfg-write", CommandOutcomeKind.Completed,
            $"Wrote 0x{length:X} bytes to {view.PartitionName} at 0x{offset:X}.", result.ImageSha256Hex);
        return result;
    }

    private static MtkSecCfgView DescribeSecCfgImage(byte[] image, GptPartition entry, uint partitionId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(image));
        var format = MtkSecCfg.DetectFormat(image) ?? throw new MtkProtocolException(
            "The seccfg partition carries neither documented secure-boot configuration header.");
        if (format == MtkSecCfgFormat.Version4)
        {
            if (!MtkSecCfg.TryReadVersion4(image, out var version4) || version4 is null)
            {
                throw new MtkProtocolException("The version 4 secure-boot configuration failed its documented checks.");
            }

            return new MtkSecCfgView(format, partitionId, entry.Name, entry.Index, entry.ByteOffset,
                entry.ByteLength, digest, version4, null);
        }

        if (!MtkSecCfg.TryReadVersion3(image, out var version3) || version3 is null)
        {
            throw new MtkProtocolException("The version 3 secure-boot configuration failed its documented checks.");
        }

        return new MtkSecCfgView(format, partitionId, entry.Name, entry.Index, entry.ByteOffset, entry.ByteLength,
            digest, null, version3);
    }
}