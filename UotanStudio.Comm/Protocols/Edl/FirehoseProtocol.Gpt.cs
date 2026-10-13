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
        return await ReadGptOwnedAsync(physicalPartition, null, null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a GPT pair with operator-supplied partition-entry array dimensions, for a target whose primary
    /// header reports a damaged entry-array location, entry count or entry size.
    /// <para>为自身上报了损坏条目数组位置、条目数量或条目大小的主头部，按操作者指定的分区条目数组维度读取 GPT 主备对。</para>
    /// </summary>
    /// <remarks>
    /// The LBA override replaces only the primary array location: every other primary header field must still
    /// validate, and the array read from the overridden location must still match the declared entry-array CRC and
    /// be byte-identical to the backup copy. The count and size overrides are array-wide, so they apply to both
    /// copies and the declared CRC keeps gating the result. All three values are recorded on the snapshot, so named
    /// partition operations revalidate the observation with the same dimensions. The read itself
    /// issues no write command.
    /// <para>LBA 覆盖仅替换主数组位置：主头部其余字段仍须全部通过校验，且从覆盖位置读出的数组仍必须匹配头部声明的条目数组
    /// CRC，并与备份副本逐字节一致。条目数量与大小覆盖作用于整条数组，因此同时应用于主备两份，声明的 CRC 仍是放行条件。三个
    /// 取值都会记录在快照上，因此按分区名执行的操作会以同一维度复核该观测。读取本身不发出任何写命令。</para>
    /// </remarks>
    /// <param name="physicalPartition">
    /// The explicitly selected physical region or LUN.
    /// <para>显式选择的物理区域或 LUN。</para>
    /// </param>
    /// <param name="partitionEntryLba">
    /// The primary partition-entry array LBA to read from, or <see langword="null"/> to use the value the header
    /// reports itself.
    /// <para>要读取的主分区条目数组 LBA；为 <see langword="null"/> 时采用头部自身所报取值。</para>
    /// </param>
    /// <param name="partitionEntryCount">
    /// The partition-entry count to read, from 1 through 65536, or <see langword="null"/> to use the header's value.
    /// <para>要读取的分区条目数量，范围 1 至 65536；为 <see langword="null"/> 时采用头部自身所报取值。</para>
    /// </param>
    /// <param name="partitionEntrySize">
    /// The partition-entry size, a power of two from 128 through 4096, or <see langword="null"/> to use the
    /// header's value.
    /// <para>分区条目大小，须为 128 至 4096 之间 2 的幂；为 <see langword="null"/> 时采用头部自身所报取值。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation propagated to geometry and every block read.
    /// <para>取消信号会传播到几何查询与每一次块读取。</para>
    /// </param>
    /// <returns>
    /// A snapshot bound to this protocol instance and configured slot, carrying the overrides.
    /// <para>绑定到本协议实例与已配置槽位、并携带这些覆盖值的快照。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="physicalPartition"/> is negative, or an entry
    /// dimension or the entry-array LBA is outside the range the reader accepts for caller-supplied values.
    /// <para><paramref name="physicalPartition"/> 为负数，或某个数组维度、条目数组 LBA 超出读取器为调用方提供值所
    /// 接受的范围。</para></exception>
    /// <exception cref="OperationCanceledException">The operation lease wait or any read is cancelled.
    /// <para>等待操作租约或任一读取被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The protocol has been disposed.
    /// <para>协议已被释放。</para></exception>
    /// <exception cref="InvalidDataException">The target geometry or the GPT structure is invalid, the array at the
    /// overridden location does not match its declared CRC, or configuration changed mid-read.
    /// <para>目标几何信息或 GPT 结构无效、覆盖位置处的数组与声明的 CRC 不符，或配置在读取中途发生变化。</para></exception>
    /// <exception cref="FirehoseException">A GPT block read returned NAK or short data.
    /// <para>GPT 块读取返回 NAK 或数据不完整。</para></exception>
    public async Task<FirehoseGptSnapshot> ReadGptWithPartitionEntryOverridesAsync(int physicalPartition,
        ulong? partitionEntryLba, uint? partitionEntryCount, uint? partitionEntrySize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(physicalPartition);
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await ReadGptOwnedAsync(physicalPartition, partitionEntryLba, partitionEntryCount,
            partitionEntrySize, cancellationToken).ConfigureAwait(false);
    }

    private void ValidateGptBinding(FirehoseGptSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot.Owner, this) || snapshot.Slot != Configuration.Slot
            || snapshot.Geometry.SectorSize != Configuration.SectorSize)
        {
            throw new InvalidOperationException("The GPT snapshot belongs to a different protocol or storage configuration.");
        }
    }

    /// <summary>
    /// Detects the active A/B slot from an already-read GPT snapshot by testing the slot-active
    /// attribute bit (bit 6) of the <c>boot_a</c> and <c>boot_b</c> partitions
    /// <para>从已读取的 GPT 快照检测活动 A/B 槽位：测试 <c>boot_a</c> 与 <c>boot_b</c> 分区的
    /// 槽位活动属性位（第 6 位）。</para>
    /// </summary>
    /// <param name="snapshot">A snapshot previously read from this protocol.
    /// <para>先前从本协议读取的快照。</para></param>
    /// <returns><c>"a"</c> or <c>"b"</c>, or <see langword="null"/> when neither partition carries the
    /// active bit. <para><c>"a"</c> 或 <c>"b"</c>；两个分区都不带活动位时为 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.
    /// <para><paramref name="snapshot"/> 为 null。</para></exception>
    public static string? DetectActiveSlot(FirehoseGptSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        const ulong SlotActiveAttribute = 1UL << 6;
        foreach (var (name, slot) in new[] { ("boot_a", "a"), ("boot_b", "b") })
        {
            var partition = snapshot.Table.Partitions.FirstOrDefault(entry =>
                string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
            if (partition is not null && (partition.Attributes & SlotActiveAttribute) != 0)
            {
                return slot;
            }
        }

        return null;
    }

    private async Task RevalidateGptOwnedAsync(FirehoseGptSnapshot snapshot, CancellationToken cancellationToken)
    {
        ValidateGptBinding(snapshot);
        var current = await ReadGptOwnedAsync(snapshot.Geometry.PhysicalPartitionNumber, snapshot.PartitionEntryLba,
            snapshot.PartitionEntryCount, snapshot.PartitionEntrySize, cancellationToken).ConfigureAwait(false);
        if (current.Geometry != snapshot.Geometry || current.Slot != snapshot.Slot
            || current.Table.MetadataSha256 != snapshot.Table.MetadataSha256)
        {
            throw new InvalidDataException("The GPT observation changed; no named partition operation was started.");
        }
        ValidateGptBinding(snapshot);
    }

    private async Task<FirehoseGptSnapshot> ReadGptOwnedAsync(int physicalPartition, ulong? partitionEntryLba,
        uint? partitionEntryCount, uint? partitionEntrySize, CancellationToken cancellationToken)
    {
        var slot = Configuration.Slot;
        var geometry = await GetStorageGeometryOwnedAsync(physicalPartition, cancellationToken).ConfigureAwait(false);
        ValidateConfiguration();
        var table = await GptReader.ReadWithPartitionEntryOverridesAsync(ReadBlocksAsync,
            checked((int)geometry.SectorSize), (ulong)geometry.SectorCount, partitionEntryLba, partitionEntryCount,
            partitionEntrySize, cancellationToken).ConfigureAwait(false);
        ValidateConfiguration();
        return new FirehoseGptSnapshot(this, geometry, slot, table, partitionEntryLba, partitionEntryCount,
            partitionEntrySize);

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