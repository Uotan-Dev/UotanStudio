using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Security;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private const int SecCfgTransferTimeoutMs = 120000;

    /// <summary>Reads eFuse cells through the Legacy agent register command.
    /// <para>通过 Legacy 代理寄存器命令读取 eFuse 单元。</para></summary>
    /// <remarks>
    /// The Legacy extension reads memory one doubleword at a time with the register-read command
    /// (Library/DA/legacy/extension/legacy.py:96-115) and the documented cell reader peeks four bytes
    /// per cell (Library/DA/xflash/extension/xflash.py:638-650), so every cell becomes one register
    /// read and is reported back as the little-endian bytes of that value
    /// (Library/DA/legacy/extension/legacy.py:111). Nothing is written.
    /// Legacy 扩展用寄存器读命令逐双字读取内存（Library/DA/legacy/extension/legacy.py:96-115），而已记载
    /// 的单元读取器每个单元窥探 4 字节（Library/DA/xflash/extension/xflash.py:638-650），因此每个单元对应
    /// 一次寄存器读取，并按该值的小端字节上报（Library/DA/legacy/extension/legacy.py:111）。不执行写入。
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
    /// cell table. <para><paramref name="hardwareCode"/> 没有转录的单元表。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The requested range is empty, oversized or
    /// outside the table. <para>请求范围为空、超限或不在表内。</para></exception>
    /// <exception cref="InvalidOperationException">The Legacy storage stage is not ready.
    /// <para>Legacy 存储阶段未就绪。</para></exception>
    /// <exception cref="MtkProtocolException">A register exchange was not acknowledged; the Legacy
    /// family faults. <para>某次寄存器交互未被确认；Legacy 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkEfuseReading> ReadLegacyEfuseCellsAsync(ushort hardwareCode, uint efuseBase,
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
            RequireState(MtkSessionState.LegacyStorageReady);
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
                        : MtkEfuseAccess.EncodeReportedValue(await ReadLegacySecurityRegisterAsync(cell.Address, cancellationToken)
                            .ConfigureAwait(false));
                    readings.Add(new MtkEfuseCellReading(index, cell, Convert.ToHexString(bytes)));
                }

                var reading = new MtkEfuseReading(hardwareCode, efuseBase, readings);
                RecordSecurityEvidence("legacy-efuse-read", CommandOutcomeKind.Completed,
                    $"{readings.Count} cells requested, {reading.ReadCellCount} read from the target.",
                    string.Concat(readings.Select(cell => cell.BytesHex)));
                return reading;
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

    /// <summary>Reads the boot public key window through the Legacy agent register command.
    /// <para>通过 Legacy 代理寄存器命令读取引导公钥窗口。</para></summary>
    /// <remarks>
    /// The Legacy extension peeks the 0x20 bytes starting at the eFuse base plus 0x90
    /// (Library/DA/legacy/extension/legacy.py:216-222); the documented Legacy memory reader assembles
    /// such a window from single big-endian register reads and re-emits each value in little-endian
    /// order (Library/DA/legacy/extension/legacy.py:96-115).
    /// Legacy 扩展窥探从 eFuse 基址加 0x90 开始的 0x20 字节
    /// （Library/DA/legacy/extension/legacy.py:216-222）；已记载的 Legacy 内存读取器由单次大端寄存器读取
    /// 拼装该窗口，并把每个取值按小端重新输出（Library/DA/legacy/extension/legacy.py:96-115）。
    /// </remarks>
    /// <param name="efuseBase">The eFuse base address supplied by the device profile.
    /// <para>由设备配置提供的 eFuse 基址。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The exact key bytes. 精确的公钥字节。</returns>
    /// <exception cref="OverflowException">The base plus the documented offset would leave the 32-bit
    /// address space. <para>基址加上已记载偏移会超出 32 位地址空间。</para></exception>
    /// <exception cref="InvalidOperationException">The Legacy storage stage is not ready.
    /// <para>Legacy 存储阶段未就绪。</para></exception>
    /// <exception cref="MtkProtocolException">A register exchange was not acknowledged; the Legacy
    /// family faults. <para>某次寄存器交互未被确认；Legacy 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<byte[]> ReadLegacyBootPublicKeyAsync(uint efuseBase, CancellationToken cancellationToken = default)
    {
        var address = checked(efuseBase + MtkEfuseAccess.PublicKeyOffset);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.LegacyStorageReady);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var key = new byte[MtkEfuseAccess.PublicKeyLength];
                for (var offset = 0; offset < key.Length; offset += MtkEfuseAccess.CellLength)
                {
                    MtkEfuseAccess.EncodeReportedValue(
                            await ReadLegacySecurityRegisterAsync(checked((uint)(address + offset)), cancellationToken)
                                .ConfigureAwait(false))
                        .CopyTo(key.AsSpan(offset));
                }

                RecordSecurityEvidence("legacy-public-key-read", CommandOutcomeKind.Completed,
                    $"0x{key.Length:X} bytes read from 0x{address:X8}.", Convert.ToHexString(key));
                return key;
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

    /// <summary>Reads and decodes the secure-boot configuration through the Legacy agent.
    /// <para>通过 Legacy 代理读取并解码安全启动配置。</para></summary>
    /// <remarks>
    /// The Legacy extension locates the <c>seccfg</c> GPT entry, reads the whole entry and decodes it
    /// with the same two layouts (Library/DA/legacy/extension/legacy.py:165-193). Nothing is written.
    /// Legacy 扩展定位 <c>seccfg</c> GPT 条目、读取整个条目并按同样的两种布局解码
    /// （Library/DA/legacy/extension/legacy.py:165-193）。不执行写入。
    /// </remarks>
    /// <param name="partitionId">The discovered Legacy physical region holding the configuration GPT.
    /// <para>承载配置 GPT 的已发现 Legacy 物理区域。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The decoded view bound to the partition and the exact bytes read.
    /// <para>与分区及实际读取字节绑定的解码视图。</para></returns>
    /// <exception cref="InvalidOperationException">Legacy storage has not been discovered.
    /// <para>Legacy 存储尚未发现。</para></exception>
    /// <exception cref="NotSupportedException">The located range exceeds the supported bound.
    /// <para>定位到的范围超过支持上限。</para></exception>
    /// <exception cref="MtkProtocolException">The image has no documented header; the Legacy family
    /// faults. <para>镜像不含已记载头部；Legacy 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkSecCfgView> ReadLegacySecCfgAsync(byte partitionId, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                return (await ReadLegacySecCfgOwnedAsync(partitionId, cancellationToken).ConfigureAwait(false)).View;
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

    /// <summary>Closes or opens the secure-boot configuration through the Legacy agent.
    /// <para>通过 Legacy 代理关闭或打开安全启动配置。</para></summary>
    /// <remarks>
    /// The read, decode and rewrite follow Library/Hardware/seccfg.py:78-102 and the write-back follows
    /// Library/DA/legacy/extension/legacy.py:193-200. Writing is destructive and irreversible, so a
    /// false <paramref name="confirmWrite"/> performs the read and the whole computation and then stops
    /// before any write command is sent.
    /// 读取与重写遵循 Library/Hardware/seccfg.py:78-102，写回遵循
    /// Library/DA/legacy/extension/legacy.py:193-200。写入是破坏性且不可逆的操作，因此 <paramref
    /// name="confirmWrite"/> 为 false 时会完成读取与全部计算，并在发送任何写入命令之前停止。
    /// </remarks>
    /// <param name="intent">The documented state transition. 已记载的状态转换。</param>
    /// <param name="partitionId">The discovered Legacy physical region holding the configuration GPT.
    /// <para>承载配置 GPT 的已发现 Legacy 物理区域。</para></param>
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
    /// <exception cref="NotSupportedException">The layout needs a hardware integrity transform, or
    /// the located range exceeds the four MiB ceiling; the session keeps its stage in every case.
    /// <para>该布局需要硬件完整性变换，或定位到的范围超过 4 MiB 上限；每种情况下会话都保持原阶段。</para></exception>
    /// <exception cref="InvalidOperationException">The configuration already has the requested state.
    /// <para>配置已处于请求的状态。</para></exception>
    /// <exception cref="MtkProtocolException">Any exchange failed; the Legacy family faults.
    /// <para>任一交互失败；Legacy 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkSecCfgWriteResult> ApplyLegacySecCfgAsync(MtkSecCfgLockIntent intent, byte partitionId,
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
                var (view, _) = await ReadLegacySecCfgOwnedAsync(partitionId, cancellationToken).ConfigureAwait(false);
                if (view.Version4 is null)
                {
                    throw new NotSupportedException(
                        "The version 3 layout cannot be rewritten off-device because its integrity transform lives in the target SEJ engine.");
                }

                var replacement = MtkSecCfg.CreateVersion4(view.Version4, intent);
                return await WriteLegacySecCfgOwnedAsync(view, replacement, confirmWrite, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsSecurityPolicyRefusal(exception))
            {
                RecordSecurityEvidence("legacy-seccfg-apply", CommandOutcomeKind.Rejected,
                    $"The secure-boot configuration request was refused before any byte was sent: {exception.Message}");
                throw;
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

    /// <summary>Writes a caller-supplied secure-boot configuration image through the Legacy agent.
    /// <para>通过 Legacy 代理写入调用方提供的安全启动配置镜像。</para></summary>
    /// <remarks>
    /// This is the off-device path for layouts whose body cannot be rewritten here, such as version 3
    /// images protected by the target SEJ engine. The header and alignment are validated and the
    /// partition comes from the live GPT, but the body is written exactly as supplied with no patching,
    /// re-hashing, comparison against the previous content or read-back
    /// (Library/DA/legacy/extension/legacy.py:196-200 performs the same unconditional write).
    /// 这是主体无法在此重写的布局所使用的设备外路径，例如其主体由目标 SEJ 引擎保护的版本 3 镜像。头部与
    /// 对齐会被校验，分区来自实时 GPT，但主体按所提供的样子写入：不打补丁、不重新计算摘要、不与旧内容比较，
    /// 也不回读（Library/DA/legacy/extension/legacy.py:196-200 同样执行无条件写入）。
    /// </remarks>
    /// <param name="image">A readable stream positioned at the first image byte.
    /// <para>定位在首个镜像字节处的可读流。</para></param>
    /// <param name="length">The positive image byte count, a multiple of 0x200 and no larger than the
    /// four MiB ceiling the reader applies to a secure-boot configuration partition.
    /// <para>正数镜像字节数，必须为 0x200 的整数倍，且不超过读取器对安全启动配置分区施用的 4 MiB 上限。</para></param>
    /// <param name="partitionId">The discovered Legacy physical region holding the configuration GPT.
    /// <para>承载配置 GPT 的已发现 Legacy 物理区域。</para></param>
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
    /// <exception cref="MtkProtocolException">Any exchange failed; the Legacy family faults.
    /// <para>任一交互失败；Legacy 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkSecCfgWriteResult> WriteLegacySecCfgAsync(Stream image, int length, byte partitionId,
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
                var view = (await ReadLegacySecCfgOwnedAsync(partitionId, cancellationToken).ConfigureAwait(false)).View;
                if (view.Format != format)
                {
                    throw new ArgumentException(
                        $"The supplied image is a {format} layout but the partition holds {view.Format}.", nameof(image));
                }

                return await WriteLegacySecCfgOwnedAsync(view, buffer, confirmWrite, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsSecurityPolicyRefusal(exception))
            {
                RecordSecurityEvidence("legacy-seccfg-write", CommandOutcomeKind.Rejected,
                    $"The supplied {format} image was refused before any byte was written.");
                throw;
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

    /// <summary>Loads one documented key class into the Legacy agent's SEJ registers.
    /// <para>把一类已记载的密钥加载到 Legacy 代理的 SEJ 寄存器。</para></summary>
    /// <remarks>
    /// The Legacy extension supplies the software one-time-program key to the SEJ engine through the
    /// register-write command, eight doublewords in little-endian byte order at the SEJ base offsets
    /// 0x60 through 0x7C (Library/Hardware/hwcrypto_sej.py:66-73 and :613-622 with
    /// Library/DA/legacy/extension/legacy.py:117-137 and Library/DA/legacy/dalegacy_lib.py:101-108).
    /// The replay-protected key class is refused on this family: the Legacy extension has no key
    /// submission command at all, only the host-side generator
    /// (Library/DA/legacy/extension/legacy.py:224-363), so framing one would require inventing bytes.
    /// Legacy 扩展通过寄存器写命令把软件一次性编程密钥交给 SEJ 引擎：按小端字节序的 8 个双字写入 SEJ 基址
    /// 偏移 0x60 至 0x7C（Library/Hardware/hwcrypto_sej.py:66-73 与 :613-622，配合
    /// Library/DA/legacy/extension/legacy.py:117-137 与
    /// Library/DA/legacy/dalegacy_lib.py:101-108）。本家族拒绝重放保护密钥类别：Legacy 扩展完全没有密钥
    /// 提交命令，只有主机侧生成器（Library/DA/legacy/extension/legacy.py:224-363），因此为其构造帧将需要
    /// 编造字节。
    /// </remarks>
    /// <param name="request">The key class request; only the software one-time-program class is
    /// accepted here. 密钥类别请求；此处只接受软件一次性编程类别。</param>
    /// <param name="confirmWrite">The explicit caller opt-in that authorizes the register writes.
    /// <para>授权寄存器写入的显式调用方选择。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A receipt naming the class, the key digest and the unit count.
    /// <para>写明类别、密钥摘要与单元数量的回执。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.
    /// <para><paramref name="request"/> 为 null。</para></exception>
    /// <exception cref="NotSupportedException">The request uses a key class the Legacy extension never
    /// submits. <para>请求使用 Legacy 扩展从不提交的密钥类别。</para></exception>
    /// <exception cref="ArgumentException">The key length, class or base address does not match the
    /// documented class. <para>密钥长度、类别或基址与已记载类别不符。</para></exception>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmWrite"/> is
    /// <see langword="false"/>; no bytes were sent.
    /// <para><paramref name="confirmWrite"/> 为 <see langword="false"/>；未发送任何字节。</para></exception>
    /// <exception cref="InvalidOperationException">Legacy storage is not ready.
    /// <para>Legacy 存储未就绪。</para></exception>
    /// <exception cref="MtkProtocolException">A register write was not acknowledged; the Legacy family
    /// faults. <para>某次寄存器写入未被确认；Legacy 家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkOemKeyReceipt> ProgramLegacyOemKeyAsync(MtkOemKeyRequest request, bool confirmWrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = request.DecodeKey();
        if (request.Kind != MtkOemKeyKind.SoftwareOneTimeProgramKey)
        {
            throw new NotSupportedException(
                "The Legacy extension submits no key material to the agent, so this key class has no documented framing.");
        }

        if (!confirmWrite)
        {
            throw new UnauthorizedAccessException(
                "Key programming changes device state and requires an explicit confirmation.");
        }

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.LegacyStorageReady);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var baseAddress = request.SejBaseAddress!.Value;
                var addresses = new List<uint>(MtkOemKeyRegisterWindow.SlotCount);
                for (var slot = 0; slot < MtkOemKeyRegisterWindow.SlotCount; slot++)
                {
                    var address = checked(baseAddress + MtkOemKeyRegisterWindow.Offsets[slot]);
                    await WriteLegacySecurityRegisterAsync(address,
                        BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(slot * MtkEfuseAccess.CellLength)),
                        cancellationToken).ConfigureAwait(false);
                    addresses.Add(address);
                }

                var receipt = new MtkOemKeyReceipt(MtkOemKeyKind.SoftwareOneTimeProgramKey,
                    Convert.ToHexString(SHA256.HashData(key)), addresses.Count, null);
                RecordSecurityEvidence("legacy-key-program", CommandOutcomeKind.Completed,
                    $"Wrote {addresses.Count} SEJ registers from 0x{addresses[0]:X8}.");
                return receipt;
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

    private async Task<(MtkSecCfgView View, byte[] Image)> ReadLegacySecCfgOwnedAsync(byte partitionId,
        CancellationToken cancellationToken)
    {
        var snapshot = await ReadLegacyGptOwnedAsync(partitionId, cancellationToken).ConfigureAwait(false);
        var entry = ResolveSecCfgPartition(snapshot.Table);
        using var buffer = new MemoryStream();
        await TransferLegacyStorageOwnedAsync(false, partitionId, entry.ByteOffset, entry.ByteLength, buffer,
            SecCfgTransferTimeoutMs, null, cancellationToken, nor: false).ConfigureAwait(false);
        var image = buffer.ToArray();
        if (image.LongLength != entry.ByteLength)
        {
            throw new MtkProtocolException("The secure-boot configuration read was incomplete.");
        }

        var view = DescribeSecCfgImage(image, entry, partitionId);
        RecordSecurityEvidence("legacy-seccfg-read", CommandOutcomeKind.Completed,
            $"{view.Format} configuration read from partition {view.PartitionName} (GPT entry {view.PartitionIndex}).",
            view.ImageSha256Hex);
        return (view, image);
    }

    private async Task<MtkSecCfgWriteResult> WriteLegacySecCfgOwnedAsync(MtkSecCfgView view, byte[] image,
        bool confirmWrite, CancellationToken cancellationToken)
    {
        if (!confirmWrite)
        {
            throw new UnauthorizedAccessException(
                "Writing a secure-boot configuration changes device state and requires an explicit confirmation.");
        }

        using var source = new MemoryStream(image, writable: false);
        await TransferLegacyStorageOwnedAsync(true, (byte)view.PartitionId, view.ByteOffset, image.LongLength, source,
            SecCfgTransferTimeoutMs, null, cancellationToken, nor: false).ConfigureAwait(false);
        var result = new MtkSecCfgWriteResult(view.PartitionName, view.PartitionIndex, view.ByteOffset,
            image.LongLength, view.Format, Convert.ToHexString(SHA256.HashData(image)));
        RecordSecurityEvidence("legacy-seccfg-write", CommandOutcomeKind.Completed,
            $"Wrote 0x{image.LongLength:X} bytes to {view.PartitionName} at 0x{view.ByteOffset:X}.",
            result.ImageSha256Hex);
        return result;
    }
}