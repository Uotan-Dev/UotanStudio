using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Writes a bootloader control block to the misc partition and resets, which is the only way a Qualcomm
/// download-mode target can be steered into recovery or the userspace fastboot daemon: the <c>&lt;power&gt;</c>
/// command can only reset, so the target mode is chosen by the block the bootloader reads on the next boot.
/// <para>把引导控制块写入 misc 分区再复位，这是让高通下载模式目标进入 recovery 或用户态 fastboot 守护进程的唯一
/// 途径：<c>&lt;power&gt;</c> 命令只能复位，目标模式由 bootloader 在下次启动时读取的控制块决定。</para>
/// </summary>
/// <remarks>
/// Both reference images are 8192-byte blocks whose first command string is all that matters, so this writer
/// accepts a caller-supplied image rather than embedding a copy, and it addresses the partition by name so a
/// multi-region UFS device resolves the right logical unit instead of assuming LUN zero.
/// <para>两份参考镜像都是 8192 字节块，其中只有开头那条命令串有意义，因此本写入方接受调用方提供的镜像而不是内嵌
/// 副本，并按分区名定位，使多区域 UFS 设备解析到正确的逻辑单元，而不是假定 LUN 0。</para>
/// </remarks>
internal static class EdlBootloaderControlWriter
{
    /// <summary>The partition that carries the bootloader control block.
    /// <para>承载引导控制块的分区。</para>
    /// </summary>
    internal const string MiscPartitionName = "misc";

    /// <summary>The highest logical unit scanned when the caller names no explicit region.
    /// <para>调用方未指定区域时扫描的最高逻辑单元。</para>
    /// </summary>
    private const int MaximumScannedLun = 7;

    /// <summary>
    /// Locates the misc partition across the readable logical units and writes the supplied control image.
    /// <para>在可读的逻辑单元中定位 misc 分区并写入给定的控制镜像。</para>
    /// </summary>
    /// <param name="firehose">The configured Firehose protocol. <para>已配置的 Firehose 协议实例。</para></param>
    /// <param name="image">The control image; its length must not exceed the partition.
    /// <para>控制镜像；其长度不得超过该分区容量。</para></param>
    /// <param name="explicitLun">The region to use, or <see langword="null"/> to scan by name.
    /// <para>要使用的区域；为空时按分区名扫描。</para></param>
    /// <param name="allowWrite">Confirmation that the caller intends a device write; <see langword="false"/> fails
    /// before any byte is sent.
    /// <para>调用方确认将写入设备；为 <see langword="false"/> 时在任何字节上线前失败。</para></param>
    /// <param name="cancellationToken">Cancels the read and the program.
    /// <para>取消读取与编程。</para></param>
    /// <returns>The logical unit and the sector range that were written.
    /// <para>已写入的逻辑单元与扇区范围。</para></returns>
    /// <exception cref="TimeoutException"><paramref name="image"/> is empty.
    /// <para><paramref name="image"/> 为空。</para></exception>
    /// <exception cref="InvalidOperationException">The confirmation flag is unset, no readable region carries
    /// <c>misc</c>, or the image does not fit.
    /// <para>确认标志未置位、没有任何可读区域包含 <c>misc</c>，或镜像放不下。</para></exception>
    internal static async Task<(int Lun, long StartSector, long SectorCount)> WriteMiscAsync(
        FirehoseProtocol firehose, ReadOnlyMemory<byte> image, int? explicitLun, bool allowWrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(firehose);
        if (!allowWrite)
        {
            throw new InvalidOperationException(
                "Writing the bootloader control block requires explicit caller confirmation.");
        }

        if (image.Length == 0)
        {
            throw new ArgumentException("The bootloader control image is empty.", nameof(image));
        }

        var (snapshot, entry) = await ResolveMiscAsync(firehose, explicitLun, cancellationToken).ConfigureAwait(false);
        var sectorSize = snapshot.Geometry.SectorSize;
        var capacityBytes = (long)entry.ByteLength;
        if (image.Length > capacityBytes)
        {
            throw new InvalidOperationException(
                $"The bootloader control image is {image.Length} bytes, which exceeds the {capacityBytes}-byte misc partition.");
        }

        var sectorCount = (image.Length + sectorSize - 1) / sectorSize;
        using var stream = new MemoryStream(image.ToArray(), writable: false);
        var response = await firehose.ProgramAsync(stream, (long)entry.FirstLba, sectorCount, MiscPartitionName,
            snapshot.Geometry.PhysicalPartitionNumber, cancellationToken).ConfigureAwait(false);
        if (!response.IsAck)
        {
            throw new InvalidOperationException(
                $"The target did not acknowledge the misc write: {EdlFailureText.Describe(response)}");
        }

        return (snapshot.Geometry.PhysicalPartitionNumber, (long)entry.FirstLba, sectorCount);
    }

    private static async Task<(FirehoseGptSnapshot Snapshot, GptPartition Entry)> ResolveMiscAsync(
        FirehoseProtocol firehose, int? explicitLun, CancellationToken cancellationToken)
    {
        if (explicitLun is { } lun)
        {
            var explicitSnapshot = await firehose.ReadGptAsync(lun, cancellationToken).ConfigureAwait(false);
            var located = explicitSnapshot.Table.Partitions.FirstOrDefault(entry =>
                string.Equals(entry.Name, MiscPartitionName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Logical unit {lun} carries no {MiscPartitionName} partition.");
            return (explicitSnapshot, located);
        }

        Exception? firstFailure = null;
        for (var candidate = 0; candidate <= MaximumScannedLun; candidate++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FirehoseGptSnapshot scanned;
            try
            {
                scanned = await firehose.ReadGptAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException
                and not ObjectDisposedException)
            {
                firstFailure ??= failure;
                continue;
            }

            if (scanned.Table.Partitions.FirstOrDefault(candidateEntry =>
                    string.Equals(candidateEntry.Name, MiscPartitionName, StringComparison.Ordinal)) is { } entry)
            {
                return (scanned, entry);
            }
        }

        throw new InvalidOperationException(firstFailure is null
            ? "No readable logical unit carries the misc partition."
            : $"No readable logical unit carries the misc partition; the first read failure was: {firstFailure.Message}");
    }
}