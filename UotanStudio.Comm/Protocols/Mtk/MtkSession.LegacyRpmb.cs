using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Reads a bounded byte range of the Legacy DA's RPMB partition.
    /// <para>读取 Legacy DA RPMB 分区中有界的字节范围。</para></summary>
    /// <remarks>The Legacy DA reaches RPMB by switching to the dedicated partition and then using its ordinary block read command: <c>mtkclient/Library/DA/legacy/dalegacy_lib.py:1014-1015</c> maps the partition name to the RPMB identifier from <c>mtkclient/Library/DA/daconfig.py:39</c>, and <c>mtkclient/Library/DA/legacy/dalegacy_lib.py:1027-1055</c> switches partitions with <c>mtkclient/Library/DA/legacy/dalegacy_lib.py:824-833</c> before sending the read header of one command byte, one host byte, one storage-type byte and big-endian address, length and packet fields. That framing carries byte counts, not frame indices, and reports no per-frame authentication status, so the result here is raw partition bytes. This method does not weaken the ordinary path's refusal of RPMB: <see cref="MtkLegacyStorageInfo.ValidateRange"/> still rejects the region for normal block storage.
    /// <para>Legacy DA 通过切换到专用分区再使用其普通块读取命令来访问 RPMB： <c>mtkclient/Library/DA/legacy/dalegacy_lib.py:1014-1015</c> 将分区名映射为 <c>mtkclient/Library/DA/daconfig.py:39</c> 中的 RPMB 标识，随后 <c>mtkclient/Library/DA/legacy/dalegacy_lib.py:1027-1055</c> 先用 <c>mtkclient/Library/DA/legacy/dalegacy_lib.py:824-833</c> 切换分区，再发送由一个命令字节、一个主机字节、 一个存储类型字节以及大端地址、长度与包字段组成的读取头。该框架传递的是字节数而不是帧序号，也不上报 逐帧认证状态，因此本方法返回的是原始分区字节。本方法不会削弱普通路径对 RPMB 的拒绝： <see cref="MtkLegacyStorageInfo.ValidateRange"/> 仍会拒绝把该区域当作普通块存储。</para>The Legacy write framing (<c>mtkclient/Library/DA/legacy/dalegacy_lib.py:845-887</c>) is an ordinary partition write with no RPMB authentication frame semantics, so it is not exposed as an RPMB write path.<para>Legacy 写入框架（<c>mtkclient/Library/DA/legacy/dalegacy_lib.py:845-887</c>）是不带 RPMB 认证帧语义的普通分区写入，因此没有作为 RPMB 写入路径暴露。</para></remarks>
    /// <param name="offset">The byte offset inside the RPMB partition, aligned to the Legacy block size.
    /// <para>RPMB 分区内的字节偏移，必须按 Legacy 块大小对齐。</para></param>
    /// <param name="length">The positive byte count, aligned to the Legacy block size and bounded by both the reported RPMB capacity and the configured read ceiling.
    /// <para>正数字节数，必须按 Legacy 块大小对齐，并同时受上报容量与读取上限约束。</para></param>
    /// <param name="destination">A writable stream which remains caller-owned.
    /// <para>保持由调用方持有的可写流。</para></param>
    /// <param name="options">Optional bounds and deadline; <see langword="null"/> uses the read-only defaults. The frame ceiling is converted to bytes because this command family is byte-addressed.
    /// <para>可选的边界与截止时间；<see langword="null"/> 使用只读默认值。由于该命令族按字节寻址， 帧上限会被换算为字节。</para></param>
    /// <param name="progress">Optional cumulative bytes verified and stored.
    /// <para>可选的累计已校验并保存的字节数。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The delivered offset, byte count and reported RPMB capacity.
    /// <para>交付的偏移、字节数与上报的 RPMB 容量。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.
    /// <para><paramref name="destination"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not writable.
    /// <para><paramref name="destination"/> 不可写。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is unaligned, empty, oversized or outside the reported RPMB capacity.
    /// <para>范围未对齐、为空、过大或超出上报的 RPMB 容量。</para></exception>
    /// <exception cref="NotSupportedException">The Legacy session was initialized for NOR or NAND.
    /// <para>Legacy 会话是为 NOR 或 NAND 初始化的。</para></exception>
    /// <exception cref="InvalidOperationException">The Legacy agent is not storage-ready, the inventory reports no RPMB region, or that region is empty.
    /// <para>Legacy 代理未就绪、清单未上报 RPMB 区域，或该区域为空。</para></exception>
    /// <exception cref="MtkProtocolException">An acknowledgement, checksum or packet was wrong; the family faults.
    /// <para>某个确认、校验和或数据包不正确；该协议族进入 fault 状态。</para></exception>
    /// <exception cref="TimeoutException">The exchange deadline expired; the family faults.
    /// <para>交换截止时间到期；该协议族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkRpmbBlockReadResult> ReadLegacyRpmbAsync(long offset, long length, Stream destination,
        MtkRpmbOptions? options = null, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The RPMB destination is not writable.", nameof(destination));
        }
        var effective = ResolveRpmbOptions(options);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.LegacyStorageReady);
            cancellationToken.ThrowIfCancellationRequested();
            var storage = LegacyStorage ?? throw new InvalidOperationException("The Legacy storage inventory is unavailable.");
            if (storage.Kind != MtkXFlashStorageKind.Emmc)
            {
                throw new NotSupportedException("Only the Legacy eMMC inventory reports an RPMB partition.");
            }
            var region = storage.Regions.FirstOrDefault(region => region.RequiresAuthenticatedAccess)
                ?? throw new InvalidOperationException("The Legacy eMMC inventory has no RPMB region.");
            ValidateRpmbByteRange(region.CapacityBytes, offset, length, effective);
            var delivered = 0;
            await RunRpmbExchangeAsync(async token =>
            {
                delivered = await ReadLegacyRpmbOwnedAsync(region.PartitionId, offset, length,
                    destination, progress, token).ConfigureAwait(false);
            }, effective.OperationTimeoutMs, FaultLegacy, cancellationToken).ConfigureAwait(false);
            RecordRpmbEvidence("rpmb-legacy-read", CommandOutcomeKind.Completed,
                $"legacy rpmb partition {region.PartitionId} bytes {offset}+{delivered} of {region.CapacityBytes} reported.");
            return new MtkRpmbBlockReadResult
            {
                ByteOffset = offset,
                ByteLength = delivered,
                ReportedRpmbCapacityBytes = region.CapacityBytes
            };
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<int> ReadLegacyRpmbOwnedAsync(byte partitionId, long offset, long length,
        Stream destination, IProgress<long>? progress, CancellationToken token)
    {
        var agent = _legacyOptions ?? throw new InvalidOperationException("The Legacy agent options are unavailable.");
        _ = await QueryLegacyUsbSpeedOwnedAsync(token).ConfigureAwait(false);
        await SwitchLegacyPartitionOwnedAsync(partitionId, token).ConfigureAwait(false);
        var header = new byte[23];
        header[0] = 0xD6;
        header[1] = agent.HostOs == MtkXmlHostOs.Windows ? (byte)0x0B : (byte)0x0C;
        header[2] = 0x02;
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(3), (ulong)offset);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(11), (ulong)length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(19), (uint)agent.StoragePacketLength);
        await WriteAsync(header, _timeoutMs, token).ConfigureAwait(false);
        await RequireLegacyByteAsync(0x5A, "rpmb storage header", token).ConfigureAwait(false);
        long transferred = 0;
        while (transferred < length)
        {
            var count = (int)Math.Min(length - transferred, agent.StoragePacketLength);
            var data = await ReadAsync(count, _timeoutMs, token).ConfigureAwait(false);
            var checksum = BinaryPrimitives.ReadUInt16BigEndian(await ReadAsync(2, _timeoutMs, token).ConfigureAwait(false));
            if (checksum != LegacySum(data))
            {
                throw new MtkProtocolException("The Legacy RPMB packet checksum does not match.");
            }
            await destination.WriteAsync(data, token).ConfigureAwait(false);
            await WriteAsync([0x5A], _timeoutMs, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            transferred += count;
            progress?.Report(transferred);
        }
        return (int)transferred;
    }
}