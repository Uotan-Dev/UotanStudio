using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Security;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Queries the chip identity reported by the initialized XFlash download agent.
    /// <para>查询已初始化 XFlash 下载代理上报的芯片身份。</para></summary>
    /// <remarks>This is a read-only device-control query; it writes no register, unlocks no zone and
    /// changes no lock state. The reply is decoded only as far as the documented five-word layout
    /// allows and is also recorded verbatim as session evidence.
    /// 这是只读的器件控制查询：不写寄存器、不解锁任何区域、不更改锁定状态。回复仅按已记载的 5 个字 布局解码，并同时以原文记录为会话证据。</remarks>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The decoded identity together with the exact reply bytes.
    /// <para>解码后的身份信息及其精确回复字节。</para></returns>
    /// <exception cref="InvalidOperationException">No XFlash stage-one agent is attached or the transport is disconnected.
    /// <para>未附加 XFlash 一阶段代理或传输已断开。</para></exception>
    /// <exception cref="MtkProtocolException">The reply length or a status frame is wrong; the XFlash family faults.
    /// <para>回复长度或某个状态帧不正确；XFlash 家族进入 fault 状态。</para></exception>
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

    /// <summary>Reads eFuse cells through the stage-two register window documented by the reference client.
    /// <para>通过参考客户端记载的二阶段寄存器窗口读取 eFuse 单元。</para></summary>
    /// <remarks>Every cell is resolved with <see cref="MtkEfuseFieldTable"/>and
    /// <see cref="MtkEfuseAccess.ResolveCell"/>; cells the documented reader treats as non-addresses
    /// are reported without a device read, exactly as the reference does
    /// (Library/DA/xflash/extension/xflash.py:638-650). Reads use the documented register-read code
    /// (Library/DA/xflash/extension/xflash.py:325-332) and write nothing. 每个单元都由
    /// <see cref="MtkEfuseFieldTable"/>与 <see cref="MtkEfuseAccess.ResolveCell"/>解析；
    /// 已记载读取器视为非地址的单元在不读取设备的情况下上报，与参考实现一致 （Library/DA/xflash/extension/xflash.py:638-650）。读取使用已记载的寄存器读码
    /// （Library/DA/xflash/extension/xflash.py:325-332），不执行任何写入。</remarks>
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
    /// <remarks>The reference client peeks the 0x30 bytes starting at the eFuse base plus 0x90
    /// (Library/DA/xflash/extension/xflash.py:861-867). The window is fetched as twelve little-endian
    /// register doublewords, which is how the documented memory read assembles any longer request
    /// (Library/DA/xflash/extension/xflash.py:353-370). 参考客户端窥探从 eFuse 基址加 0x90 开始的 0x30 字节
    /// （Library/DA/xflash/extension/xflash.py:861-867）。该窗口按 12 个小端寄存器双字取回，这正是已记载
    /// 内存读取拼装较长请求的方式（Library/DA/xflash/extension/xflash.py:353-370）。</remarks>
    /// <param name="efuseBase">The eFuse base address supplied by the device profile.
    /// <para>由设备配置提供的 eFuse 基址。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The exact key bytes. 精确的公钥字节。</returns>
    /// <exception cref="OverflowException">The base plus the documented offset would leave the 32-bit address space.
    /// <para>基址加上已记载偏移会超出 32 位地址空间。</para></exception>
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
    /// <remarks>The partition is located by the documented exact name and read in full, then decoded by
    /// <see cref="MtkSecCfg"/>(Library/DA/xflash/extension/xflash.py:564-594). The exchange writes
    /// nothing to the device, and an unrecognized header faults the family instead of being
    /// reinterpreted. 按已记载的精确名称定位分区并整体读取，随后由 <see cref="MtkSecCfg"/>解码
    /// （Library/DA/xflash/extension/xflash.py:564-594）。本交互不向设备写入任何内容；无法识别的头部会使 家族进入 fault，而不会被重新解释。</remarks>
    /// <param name="partitionId">The discovered physical region holding the configuration GPT.
    /// <para>承载配置 GPT 的已发现物理区域。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The decoded view bound to the partition and the exact bytes read.
    /// <para>与分区及实际读取字节绑定的解码视图。</para></returns>
    /// <exception cref="InvalidOperationException">Storage has not been discovered or the region is unknown.
    /// <para>存储尚未发现或该区域未知。</para></exception>
    /// <exception cref="NotSupportedException">The located range exceeds the supported bound.
    /// <para>定位到的范围超过支持上限。</para></exception>
    /// <exception cref="MtkProtocolException">The image has no documented header or neither decoder accepts it; the XFlash family faults.
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


    private static MtkSecCfgView DescribeSecCfgImage(byte[] image, GptPartition entry, uint partitionId)
    {
        var digest = Convert.ToHexString(Sha256.ComputeHash(image));
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