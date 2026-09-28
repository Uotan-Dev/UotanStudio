using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Firehose protocol GPT observation operations.
/// <para>Firehose 协议的 GPT 观测操作。</para>
/// </summary>
public sealed partial class FirehoseProtocol
{
    /// <summary>
    /// Queries physical geometry and validates both GPT copies under one operation lease.
    /// <para>在单个操作租约下查询物理几何信息，并校验两份 GPT 副本。</para>
    /// </summary>
    /// <remarks>
    /// The configured sector size must match the queried region. No geometry fallback,
    /// GPT repair or primary-only acceptance is performed. Configuration must not be mutated
    /// during operations. An observation does not freeze device contents or authorize writes.
    /// <para>配置的扇区大小必须与所查询的区域一致。不会进行几何回退、GPT 修复或仅接受主副本。
    /// 操作期间不得修改配置。一次观测既不会冻结设备内容，也不授权写入。</para>
    /// </remarks>
    /// <param name="physicalPartition">
    /// The explicitly selected physical region or LUN.
    /// <para>显式选择的物理区域或 LUN。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation propagated to geometry and every block read.
    /// <para>取消信号会传播到几何查询与每一次块读取。</para>
    /// </param>
    /// <returns>
    /// A snapshot bound to this protocol instance and configured slot.
    /// <para>绑定到本协议实例与已配置槽位的快照。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="physicalPartition"/> is negative.
    /// <para><paramref name="physicalPartition"/> 为负数。</para></exception>
    /// <exception cref="OperationCanceledException">The operation lease wait or any read is cancelled.
    /// <para>等待操作租约或任一读取被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The protocol has been disposed.
    /// <para>协议已被释放。</para></exception>
    /// <exception cref="InvalidDataException">The target geometry or the GPT structure is invalid, or configuration
    /// changed mid-read.
    /// <para>目标几何信息或 GPT 结构无效，或配置在读取中途发生变化。</para></exception>
    /// <exception cref="FirehoseException">A GPT block read returned NAK or short data.
    /// <para>GPT 块读取返回 NAK 或数据不完整。</para></exception>
    public async Task<FirehoseGptSnapshot> ReadGptAsync(int physicalPartition = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(physicalPartition);
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await ReadGptOwnedAsync(physicalPartition, cancellationToken).ConfigureAwait(false);
    }

    private void ValidateGptBinding(FirehoseGptSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot.Owner, this) || snapshot.Slot != Configuration.Slot
            || snapshot.Geometry.SectorSize != Configuration.SectorSize)
        {
            throw new InvalidOperationException("The GPT snapshot belongs to a different protocol or storage configuration.");
        }
    }

    private async Task RevalidateGptOwnedAsync(FirehoseGptSnapshot snapshot, CancellationToken cancellationToken)
    {
        ValidateGptBinding(snapshot);
        var current = await ReadGptOwnedAsync(snapshot.Geometry.PhysicalPartitionNumber, cancellationToken).ConfigureAwait(false);
        if (current.Geometry != snapshot.Geometry || current.Slot != snapshot.Slot
            || current.Table.MetadataSha256 != snapshot.Table.MetadataSha256)
        {
            throw new InvalidDataException("The GPT observation changed; no named partition operation was started.");
        }
        ValidateGptBinding(snapshot);
    }

    private async Task<FirehoseGptSnapshot> ReadGptOwnedAsync(int physicalPartition, CancellationToken cancellationToken)
    {
        var slot = Configuration.Slot;
        var geometry = await GetStorageGeometryOwnedAsync(physicalPartition, cancellationToken).ConfigureAwait(false);
        ValidateConfiguration();
        var table = await GptReader.ReadAsync(ReadBlocksAsync, checked((int)geometry.SectorSize),
            (ulong)geometry.SectorCount, cancellationToken).ConfigureAwait(false);
        ValidateConfiguration();
        return new FirehoseGptSnapshot(this, geometry, slot, table);

        void ValidateConfiguration()
        {
            if (Configuration.SectorSize != geometry.SectorSize || Configuration.Slot != slot)
            {
                throw new InvalidDataException("Firehose configuration does not match the GPT storage observation.");
            }
        }

        async ValueTask ReadBlocksAsync(long offset, Memory<byte> destination, CancellationToken token)
        {
            ValidateConfiguration();
            using var output = new FixedBufferWriteStream(destination);
            var response = await ReadToStreamCoreAsync(output, offset / geometry.SectorSize,
                destination.Length / geometry.SectorSize, "gpt.bin", physicalPartition, token).ConfigureAwait(false);
            if (!response.IsAck || output.Written != destination.Length)
            {
                throw new FirehoseException("The GPT block read did not complete successfully.");
            }
        }
    }
}