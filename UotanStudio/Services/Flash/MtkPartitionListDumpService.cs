using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Reads every partition the device lists by walking the entries of its own GPT, publishing one file
/// per partition with a SHA-256 record plus the raw primary and backup table images.
/// <para>遍历设备自身 GPT 的条目读取全部已列出的分区，逐个分区落盘并记录 SHA-256，同时转储主、备分区表镜像。</para>
/// </summary>
internal static class MtkPartitionListDumpService
{
    private const string GptFileName = "gpt.bin";
    private const string BackupGptFileName = "gpt_backup.bin";

    /// <summary>
    /// Resolves the GPT one agent family currently reports and builds the partition read list against the skip
    /// names, without reading any partition payload.
    /// <para>解析某一代理家族当前上报的 GPT，并按跳过名单构建待读取的分区列表；不读取任何分区数据。</para>
    /// </summary>
    /// <param name="session">A session whose storage geometry the named family already discovered. <para>已由该家族发现存储几何信息的会话。</para></param>
    /// <param name="family">The explicitly selected agent family. <para>显式选定的代理家族，不回退到其他家族。</para></param>
    /// <param name="kind">The storage family the session reported, used to resolve the region identifier. <para>该会话上报的存储家族，用于解析区域标识。</para></param>
    /// <param name="skipPartitionNames">Exact, case-sensitive names to skip, which may be empty. <para>精确且区分大小写的跳过名单，可为空。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A plan holding the observation and one entry per partition. <para>持有该次观测与逐分区条目的计划。</para></returns>
    internal static async Task<MtkPartitionListDumpPlan> ResolveAsync(MtkSession session, MtkDaProtocol family,
        MtkXFlashStorageKind kind, IReadOnlyList<string> skipPartitionNames, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(skipPartitionNames);
        ct.ThrowIfCancellationRequested();
        object observation;
        GptTable table;
        if (family == MtkDaProtocol.XFlash)
        {
            var snapshot = await session.ReadXFlashGptAsync(MtkScatterPackage.GetPhysicalPartition(kind), ct)
                .ConfigureAwait(false);
            observation = snapshot;
            table = snapshot.Table;
        }
        else if (family == MtkDaProtocol.Legacy)
        {
            var snapshot = await session
                .ReadLegacyGptAsync(MtkScatterPackage.GetLegacyPhysicalPartition(kind), ct).ConfigureAwait(false);
            observation = snapshot;
            table = snapshot.Table;
        }
        else if (family == MtkDaProtocol.Xml)
        {
            var snapshot = await session
                .ReadXmlGptAsync(MtkScatterPackage.GetXmlRegionName(kind), cancellationToken: ct)
                .ConfigureAwait(false);
            observation = snapshot;
            table = snapshot.Table;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(family),
                "The partition-list read supports the XFlash, XML and Legacy download agent families.");
        }

        var skip = new HashSet<string>(skipPartitionNames, StringComparer.Ordinal);
        var entries = new List<MtkPartitionListDumpEntry>(table.Partitions.Count);
        var total = 0L;
        foreach (var partition in table.Partitions)
        {
            ct.ThrowIfCancellationRequested();
            if (skip.Contains(partition.Name))
            {
                entries.Add(new MtkPartitionListDumpEntry(partition.Name, string.Empty, partition.ByteLength,
                    "listed in the caller's skip list"));
                continue;
            }

            if (partition.ByteLength <= 0)
            {
                entries.Add(new MtkPartitionListDumpEntry(partition.Name, string.Empty, partition.ByteLength,
                    "the GPT entry declares no blocks"));
                continue;
            }

            if (!IsSafeSingleFileName(partition.Name))
            {
                entries.Add(new MtkPartitionListDumpEntry(partition.Name, string.Empty, partition.ByteLength,
                    "the GPT entry name is not a single safe file name"));
                continue;
            }

            total = checked(total + partition.ByteLength);
            entries.Add(new MtkPartitionListDumpEntry(partition.Name, $"{partition.Name}.bin", partition.ByteLength,
                null));
        }

        return new MtkPartitionListDumpPlan(family, observation, table, entries, total);
    }

    /// <summary>
    /// Executes a resolved partition-list read: dumps the raw primary and backup partition-table bytes first, then
    /// publishes every partition file and records its SHA-256.
    /// <para>执行已解析的分区列表读取：先转储主、备分区表原始字节，再逐个分区落盘并记录各自的 SHA-256。</para>
    /// </summary>
    /// <param name="session">The same session the plan was resolved on. <para>解析计划时使用的同一会话。</para></param>
    /// <param name="plan">The resolved read plan. <para>已解析的读取计划。</para></param>
    /// <param name="destinationDirectory">An existing output directory. <para>必须已存在的输出目录。</para></param>
    /// <param name="progress">Per-partition phase progress. <para>逐分区的阶段进度。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Per-partition records, the partition-table image paths and the totals. <para>逐分区记录、分区表镜像路径与总量。</para></returns>
    internal static async Task<MtkPartitionListDumpResult> CaptureAsync(MtkSession session,
        MtkPartitionListDumpPlan plan, string destinationDirectory, IProgress<FlashProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var directory = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The read-back directory '{directory}' does not exist.");
        }

        ct.ThrowIfCancellationRequested();
        var extents = plan.Observation switch
        {
            MtkGptSnapshot xflash => MtkGptWritePlanner.Plan(xflash),
            MtkLegacyGptSnapshot legacy => MtkGptWritePlanner.Plan(legacy),
            MtkXmlGptSnapshot xml => MtkGptWritePlanner.Plan(xml),
            _ => throw new InvalidOperationException("The partition-list plan holds no GPT observation.")
        };

        var primary = extents.First(extent => extent.Copy == MtkGptCopy.Primary);
        var backup = extents.First(extent => extent.Copy == MtkGptCopy.Backup);
        var primaryPath = Path.Combine(directory, GptFileName);
        var backupPath = Path.Combine(directory, BackupGptFileName);
        progress?.Report(new FlashProgress("gpt", 0,
            $"Reading the primary and backup partition-table extents ({primary.Length} and {backup.Length} bytes) " +
            $"from the {primary.RegionName} region..."));
        await ReadExtentAsync(session, plan.Family, primary, primaryPath, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await ReadExtentAsync(session, plan.Family, backup, backupPath, ct).ConfigureAwait(false);

        var records = new List<MtkPartitionDumpRecord>(plan.Entries.Count);
        var published = 0L;
        var index = 0;
        foreach (var entry in plan.Entries)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            if (entry.UnavailableReason is not null)
            {
                records.Add(new MtkPartitionDumpRecord(entry.PartitionName, string.Empty, entry.ByteLength, null,
                    entry.UnavailableReason));
                continue;
            }

            var destination = Path.Combine(directory, entry.FileName);
            progress?.Report(new FlashProgress(entry.PartitionName, Percent(published, plan.TotalBytes),
                $"Reading partition {index} of {plan.Entries.Count}: {entry.PartitionName} " +
                $"({entry.ByteLength} bytes)..."));
            var reporter = new PartitionProgress(entry.PartitionName, entry.ByteLength, progress);
            await ReadPartitionAsync(session, plan, entry, destination, reporter, ct).ConfigureAwait(false);
            var digest = await HashFileAsync(destination, ct).ConfigureAwait(false);
            records.Add(new MtkPartitionDumpRecord(entry.PartitionName, destination, entry.ByteLength, digest, null));
            published = checked(published + entry.ByteLength);
        }

        progress?.Report(new FlashProgress("rl", 100,
            $"Published {records.Count(record => record.Sha256Hex is not null)} partition images and " +
            $"{records.Count(record => record.Sha256Hex is null)} skipped entries into '{directory}'."));
        return new MtkPartitionListDumpResult(plan.Family, records, primaryPath, backupPath, published,
            plan.Table.MetadataSha256);
    }

    private static async Task ReadExtentAsync(MtkSession session, MtkDaProtocol family, MtkGptWriteExtent extent,
        string destination, CancellationToken ct)
    {
        switch (family)
        {
            case MtkDaProtocol.XFlash:
                await session.ReadXFlashStorageToFileAsync(extent.PhysicalIdentifier!.Value, extent.Offset,
                    extent.Length, destination, cancellationToken: ct).ConfigureAwait(false);
                break;
            case MtkDaProtocol.Legacy:
                await session.ReadLegacyStorageToFileAsync((byte)extent.PhysicalIdentifier!.Value, extent.Offset,
                    extent.Length, destination, cancellationToken: ct).ConfigureAwait(false);
                break;
            default:
                await session.ReadXmlStorageToFileAsync(extent.RegionName, extent.Offset, extent.Length, destination,
                    cancellationToken: ct).ConfigureAwait(false);
                break;
        }
    }

    private static async Task ReadPartitionAsync(MtkSession session, MtkPartitionListDumpPlan plan,
        MtkPartitionListDumpEntry entry, string destination, IProgress<long> progress, CancellationToken ct)
    {
        if (plan.XFlashSnapshot is { } xflash)
        {
            await session.ReadXFlashPartitionToFileAsync(xflash, entry.PartitionName, destination, progress, ct)
                .ConfigureAwait(false);
        }
        else if (plan.LegacySnapshot is { } legacy)
        {
            await session.ReadLegacyPartitionToFileAsync(legacy, entry.PartitionName, destination, progress, ct)
                .ConfigureAwait(false);
        }
        else
        {
            await session.ReadXmlPartitionToFileAsync(plan.XmlSnapshot ?? throw new InvalidOperationException(
                    "The partition-list plan holds no XML GPT observation."), entry.PartitionName, destination,
                progress, ct).ConfigureAwait(false);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sink = new HashingWriteStream(Stream.Null);
        await input.CopyToAsync(sink, 65536, ct).ConfigureAwait(false);
        return sink.GetHashHex();
    }

    private static bool IsSafeSingleFileName(string name) =>
        name.Length is > 0 and <= 126
        && name is not ("." or "..")
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static int Percent(long completed, long total) =>
        total <= 0 ? 100 : (int)Math.Clamp(Math.Round(completed / (double)total * 100,
            MidpointRounding.AwayFromZero), 0, 100);

    private sealed class PartitionProgress(string partition, long totalBytes, IProgress<FlashProgress>? sink)
        : IProgress<long>
    {
        public void Report(long value) => sink?.Report(new FlashProgress(partition,
            totalBytes <= 0 ? 0 : (int)Math.Clamp(value * 100 / totalBytes, 0, 100),
            $"Read {value} of {totalBytes} bytes of {partition}..."));
    }
}

/// <summary>
/// One entry of a partition-list read: the partition name, its destination file, the byte count and, when the
/// entry cannot be read, the reason.
/// <para>分区列表读取中的一项：分区名、目标文件名、字节数，以及不可读取时的原因。</para>
/// </summary>
/// <param name="PartitionName">The exact GPT partition name. <para>GPT 中的精确分区名。</para></param>
/// <param name="FileName">The destination file name, empty when the entry cannot be read. <para>目标文件名，不可读取时为空。</para></param>
/// <param name="ByteLength">The partition's byte length. <para>该分区的字节长度。</para></param>
/// <param name="UnavailableReason">The skip reason, null when the entry is readable. <para>跳过原因，可读取时为 null。</para></param>
internal sealed record MtkPartitionListDumpEntry(string PartitionName, string FileName, long ByteLength,
    string? UnavailableReason);

/// <summary>
/// A resolved partition-list read plan: the GPT observation it was built from, the per-partition entries and the
/// byte total.
/// <para>已解析的分区列表读取计划：持有构建时的那份 GPT 观测、逐分区条目与字节总量。</para>
/// </summary>
internal sealed class MtkPartitionListDumpPlan
{
    internal MtkPartitionListDumpPlan(MtkDaProtocol family, object observation, GptTable table,
        IReadOnlyList<MtkPartitionListDumpEntry> entries, long totalBytes)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(table);
        Family = family;
        Observation = observation;
        Table = table;
        Entries = entries;
        TotalBytes = totalBytes;
        XFlashSnapshot = observation as MtkGptSnapshot;
        LegacySnapshot = observation as MtkLegacyGptSnapshot;
        XmlSnapshot = observation as MtkXmlGptSnapshot;
    }

    internal MtkDaProtocol Family { get; }
    internal object Observation { get; }
    internal GptTable Table { get; }
    internal IReadOnlyList<MtkPartitionListDumpEntry> Entries { get; }
    internal long TotalBytes { get; }
    internal MtkGptSnapshot? XFlashSnapshot { get; }
    internal MtkLegacyGptSnapshot? LegacySnapshot { get; }
    internal MtkXmlGptSnapshot? XmlSnapshot { get; }
}

/// <summary>
/// The record of one published partition image: its path, byte count, SHA-256 and skip reason.
/// <para>一个已发布分区镜像的记录：路径、字节数、SHA-256 与跳过原因。</para>
/// </summary>
/// <param name="PartitionName">The exact GPT partition name. <para>GPT 中的精确分区名。</para></param>
/// <param name="DestinationPath">The published file path, empty when the entry was skipped. <para>已发布文件路径，跳过时为空。</para></param>
/// <param name="ByteLength">The planned byte count. <para>计划的字节数。</para></param>
/// <param name="Sha256Hex">The published file's lowercase hexadecimal SHA-256, null when skipped. <para>已发布文件的小写十六进制 SHA-256，跳过时为 null。</para></param>
/// <param name="SkippedReason">The skip reason, null when the entry was read. <para>跳过原因，已读取时为 null。</para></param>
internal sealed record MtkPartitionDumpRecord(string PartitionName, string DestinationPath, long ByteLength,
    string? Sha256Hex, string? SkippedReason);

/// <summary>
/// The aggregate result of a partition-list read: the per-partition records, the partition-table image paths and
/// the table fingerprint from the same observation.
/// <para>分区列表读取的总结果：逐分区记录、分区表镜像路径与来自同一次观测的分区表摘要。</para>
/// </summary>
/// <param name="Family">The agent family that served the read. <para>提供本次读取的代理家族。</para></param>
/// <param name="Partitions">Per-partition records in GPT order. <para>按 GPT 顺序的逐分区记录。</para></param>
/// <param name="PrimaryTablePath">The primary partition-table image path. <para>主分区表镜像路径。</para></param>
/// <param name="BackupTablePath">The backup partition-table image path. <para>备分区表镜像路径。</para></param>
/// <param name="PublishedBytes">The byte total of the partitions read. <para>已读取分区的字节总数。</para></param>
/// <param name="TableMetadataSha256">The metadata digest of the observed table. <para>该次观测的分区表元数据摘要。</para></param>
internal sealed record MtkPartitionListDumpResult(MtkDaProtocol Family,
    IReadOnlyList<MtkPartitionDumpRecord> Partitions, string PrimaryTablePath, string BackupTablePath,
    long PublishedBytes, string TableMetadataSha256);