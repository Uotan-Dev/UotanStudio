using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Services.Flash;

/// <summary>Reads whole-flash regions and raw sector windows over one connected MediaTek download-agent session.
/// <para>在单个已连接的联发科下载代理会话上读取整片闪存区域与原始扇区窗口。</para></summary>
internal static class MtkBulkReadService
{
    private const int MaximumChunkBytes = 1024 * 1024;
    private const int MaximumWholeFlashRegions = 64;
    private const int LegacySectorBytes = 512;

    /// <summary>Hard cap on one raw-sector extent, applied before any buffer is allocated.
    /// <para>单段原始扇区范围的硬性上限，在任何缓冲区分配之前生效。</para></summary>
    /// <value>One TiB, above every region a phone download agent reports.
    /// <para>1 TiB，高于手机下载代理上报的任何区域。</para></value>
    internal const long MaximumExtentBytes = 1L << 40;

    private const string WholeFlashVerb = "rf";
    private const string RawSectorVerb = "rs";
    private const string NorRegionName = "NOR-DEVICE";

    /// <summary>Reads every ordinary region the running agent reported, from address zero to its reported
    /// capacity, publishing one image per region into an existing directory.
    /// <para>读取运行中代理上报的每个普通区域，从零地址到其上报容量，每个区域向已存在目录发布一份镜像。</para></summary>
    /// <param name="session">A session whose storage geometry the named family already discovered.
    /// <para>已由该命名家族发现存储几何信息的会话。</para></param>
    /// <param name="family">The download agent family that serves every region; no fallback occurs.
    /// <para>为每个区域提供服务的下载代理家族；不发生回退。</para></param>
    /// <param name="directoryPath">Existing directory that receives one binary image file per region.
    /// <para>接收每个区域一份二进制镜像文件的已存在目录。</para></param>
    /// <param name="progress">Optional per-chunk progress; one hundred percent is not publication success.
    /// <para>可选的分块进度；跑满 100% 不等于发布成功。</para></param>
    /// <param name="ct">Cancellation honoured before the first device byte and between every chunk.
    /// <para>在首个设备字节之前以及每块之间都会响应的取消令牌。</para></param>
    /// <returns>One record per published region plus the checked total published byte count.
    /// <para>每个已发布区域一条记录，并附经溢出检查的发布总字节数。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="directoryPath"/> is empty or whitespace.
    /// <para><paramref name="directoryPath"/> 为空或仅空白。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="family"/> is not a defined agent family.
    /// <para><paramref name="family"/> 不是已定义的代理家族。</para></exception>
    /// <exception cref="InvalidOperationException">The family has not reported geometry, or reports no ordinary
    /// region with a positive capacity. <para>该家族未上报几何信息，或没有容量为正的普通区域。</para></exception>
    /// <exception cref="NotSupportedException">The family's read path cannot address storage in bytes, or too
    /// many regions are reported. <para>该家族读取路径无法按字节寻址存储，或上报的区域过多。</para></exception>
    /// <exception cref="DirectoryNotFoundException">The output directory does not exist.
    /// <para>输出目录不存在。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at any cooperative point.
    /// <para>在任一协作点请求了取消。</para></exception>
    /// <exception cref="IOException">A staged image did not match its requested length or digest, or
    /// publication failed. <para>暂存镜像与请求长度或摘要不符，或发布失败。</para></exception>
    internal static async Task<MtkBulkReadResult> ReadWholeFlashAsync(MtkSession session, MtkDaProtocol family,
        string directoryPath, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ct.ThrowIfCancellationRequested();
        var directory = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"The whole-flash output directory '{directory}' does not exist.");
        }

        var regions = ResolveRegions(session, family);
        var records = new List<MtkBulkReadRecord>(regions.Count);
        foreach (var region in regions)
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(directory, SafeRegionFileName(region.Name));
            records.Add(await CaptureAsync(session, family, WholeFlashVerb, region, destination, 0,
                region.CapacityBytes, expectedSha256Hex: null, progress, ct).ConfigureAwait(false));
        }

        var total = 0L;
        foreach (var record in records)
        {
            total = checked(total + record.ByteLength);
        }

        progress?.Report(new FlashProgress(WholeFlashVerb, 100,
            $"Published {records.Count} {family} whole-flash region images totalling {total} bytes into '{directory}'."));
        return new MtkBulkReadResult(family, WholeFlashVerb, records, total);
    }

    /// <summary>Reads a raw sector window out of one selected physical region and publishes exactly one file.
    /// <para>从某个选定物理区域读取一段原始扇区窗口，并恰好发布一份文件。</para></summary>
    /// <param name="session">A session whose storage geometry the named family already discovered.
    /// <para>已由该命名家族发现存储几何信息的会话。</para></param>
    /// <param name="family">The download agent family that serves the read; no fallback occurs.
    /// <para>提供本次读取的下载代理家族；不发生回退。</para></param>
    /// <param name="region">The physical region: a decimal XFlash or Legacy partition identifier, or an exact
    /// XML region token. <para>物理区域：十进制 XFlash/Legacy 分区标识符，或精确的 XML 区域标记。</para></param>
    /// <param name="startSector">The zero-based region-relative sector index.
    /// <para>从零开始的区域相对扇区序号。</para></param>
    /// <param name="sectorCount">The positive number of sectors to read. <para>要读取的正数扇区数量。</para></param>
    /// <param name="destinationPath">Destination file inside an existing directory; replaced only on success.
    /// <para>位于已存在目录中的目标文件；仅在成功时被替换。</para></param>
    /// <param name="expectedSha256Hex">Optional digest the published bytes must match; compared before
    /// publication. <para>可选的必须匹配摘要；在发布之前比对。</para></param>
    /// <param name="progress">Optional per-chunk progress; one hundred percent is not publication success.
    /// <para>可选的分块进度；跑满 100% 不等于发布成功。</para></param>
    /// <param name="ct">Cancellation honoured before the first device byte and between every chunk.
    /// <para>在首个设备字节之前以及每块之间都会响应的取消令牌。</para></param>
    /// <returns>The published image's family, region, byte range, SHA-256 and chunk bound.
    /// <para>已发布镜像的家族、区域、字节范围、SHA-256 与分块上限。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="region"/> is null.
    /// <para><paramref name="session"/> 或 <paramref name="region"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException">The region or destination path is blank or misnamed for a
    /// block-addressed family. <para>区域或目标路径为空白，或对按块寻址家族命名无效。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startSector"/> is negative,
    /// <paramref name="sectorCount"/> is not positive, or the window leaves the reported region.
    /// <para><paramref name="startSector"/> 为负、<paramref name="sectorCount"/> 非正，或窗口超出上报区域。</para></exception>
    /// <exception cref="InvalidOperationException">The family has not reported geometry.
    /// <para>该家族未上报几何信息。</para></exception>
    /// <exception cref="NotSupportedException">The family cannot address that region in bytes, or the region
    /// was not reported. <para>该家族无法按字节寻址该区域，或该区域未被上报。</para></exception>
    /// <exception cref="DirectoryNotFoundException">The destination directory does not exist.
    /// <para>目标目录不存在。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at any cooperative point.
    /// <para>在任一协作点请求了取消。</para></exception>
    /// <exception cref="IOException">The staged image mismatched its requested length or digest, or
    /// publication failed. <para>暂存镜像与请求长度或摘要不符，或发布失败。</para></exception>
    internal static async Task<MtkBulkReadRecord> ReadSectorsAsync(MtkSession session, MtkDaProtocol family,
        string region, long startSector, long sectorCount, string destinationPath, string? expectedSha256Hex = null,
        IProgress<FlashProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentOutOfRangeException.ThrowIfNegative(startSector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectorCount);
        ct.ThrowIfCancellationRequested();
        var resolved = ResolveSectorRegion(session, family, region);
        var sectorBytes = resolved.SectorBytes;
        if (startSector > long.MaxValue / sectorBytes || sectorCount > long.MaxValue / sectorBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorCount),
                $"The requested {sectorBytes}-byte sector window overflows the addressable byte range.");
        }

        var offset = startSector * sectorBytes;
        var length = sectorCount * sectorBytes;
        if (length > MaximumExtentBytes || offset > resolved.CapacityBytes || length > resolved.CapacityBytes - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorCount),
                $"The {family} region '{resolved.Name}' reports {resolved.CapacityBytes} bytes, so sectors " +
                $"{startSector}..{checked(startSector + sectorCount - 1)} are outside it.");
        }

        return await CaptureAsync(session, family, RawSectorVerb, resolved, Path.GetFullPath(destinationPath),
            offset, length, expectedSha256Hex, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Reads one sized range into a staging file, hashes it while writing, verifies the staged bytes
    /// and only then replaces the destination.
    /// <para>读取一段定长范围到暂存文件，写入时哈希，校验暂存字节后才替换目标文件。</para></summary>
    /// <param name="session">The live session serving the range read. <para>提供本次范围读取的活动会话。</para></param>
    /// <param name="family">The agent family recorded on the result. <para>记录到结果中的代理家族。</para></param>
    /// <param name="verb">The verb label recorded on the result. <para>记录到结果中的动词标签。</para></param>
    /// <param name="region">The resolved region to read. <para>要读取的已解析区域。</para></param>
    /// <param name="destinationPath">Final destination file inside an existing directory.
    /// <para>位于已存在目录中的最终目标文件。</para></param>
    /// <param name="offset">The region-relative start byte. <para>区域相对的起始字节。</para></param>
    /// <param name="length">The byte count to capture. <para>要采集的字节数。</para></param>
    /// <param name="expectedSha256Hex">Optional digest the captured bytes must match.
    /// <para>可选的、捕获字节必须匹配的摘要。</para></param>
    /// <param name="progress">Optional per-chunk progress reporter. <para>可选的分块进度报告器。</para></param>
    /// <param name="ct">Cancellation honoured between chunks. <para>在块之间响应的取消令牌。</para></param>
    /// <returns>The published record with length, digest and chunk bound.
    /// <para>含长度、摘要与分块上限的已发布记录。</para></returns>
    private static async Task<MtkBulkReadRecord> CaptureAsync(MtkSession session, MtkDaProtocol family, string verb,
        MtkBulkReadRegion region, string destinationPath, long offset, long length, string? expectedSha256Hex,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new DirectoryNotFoundException($"The dump destination '{destination}' has no parent directory.");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The dump destination directory '{directory}' does not exist.");
        }

        var stagingPath = destination + ".uotan-" + Guid.NewGuid().ToString("N");
        string sha256Hex;
        try
        {
            progress?.Report(new FlashProgress(region.Name, 0,
                $"Reading {length} bytes of the {family} region '{region.Name}' from byte {offset} in " +
                $"{region.ChunkBytes}-byte chunks..."));
            await using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hashing = new HashingWriteStream(staging, leaveOpen: true);
                for (var completed = 0L; completed < length;)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = (int)Math.Min(region.ChunkBytes, length - completed);
                    await region.Read(session, offset + completed, chunk, hashing, ct).ConfigureAwait(false);
                    completed += chunk;
                    progress?.Report(new FlashProgress(region.Name, Percent(completed, length),
                        $"Read {completed} of {length} bytes of the {family} region '{region.Name}'..."));
                }

                if (staging.Length != length)
                {
                    throw new IOException(
                        $"The staged {family} region '{region.Name}' holds {staging.Length} bytes but {length} " +
                        "were requested, so the destination was left untouched.");
                }

                sha256Hex = Convert.ToHexString(hashing.GetHashAndReset());
            }

            var readbackHex = await HashFileAsync(stagingPath, ct).ConfigureAwait(false);
            if (!string.Equals(readbackHex, sha256Hex, StringComparison.Ordinal))
            {
                throw new IOException(
                    $"The staged {family} region '{region.Name}' reads back as SHA-256 {readbackHex} instead of " +
                    $"the streamed {sha256Hex}, so the destination was left untouched.");
            }

            if (expectedSha256Hex is not null
                && !string.Equals(NormalizeExpectedDigest(expectedSha256Hex, nameof(expectedSha256Hex)), sha256Hex,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    $"The {family} region '{region.Name}' read back as SHA-256 {sha256Hex} but the caller " +
                    $"required {NormalizeExpectedDigest(expectedSha256Hex, nameof(expectedSha256Hex))}, so the " +
                    "destination was left untouched.");
            }

            ct.ThrowIfCancellationRequested();
            File.Move(stagingPath, destination, overwrite: true);
            progress?.Report(new FlashProgress(region.Name, 100,
                $"Published {length} bytes of the {family} region '{region.Name}' with SHA-256 {sha256Hex}."));
            return new MtkBulkReadRecord(family, verb, region.Name, destination, length, sha256Hex,
                region.ChunkBytes);
        }
        finally
        {
            DiscardStaging(stagingPath);
        }
    }

    /// <summary>Resolves every ordinary, positively sized region one family reported for the whole-flash walk.
    /// <para>解析某家族为整片读取上报的每个普通且容量为正的区域。</para></summary>
    /// <param name="session">A session whose storage geometry the named family already discovered.
    /// <para>已由该命名家族发现存储几何信息的会话。</para></param>
    /// <param name="family">The download agent family whose regions are resolved.
    /// <para>要解析其区域的下载代理家族。</para></param>
    /// <returns>The resolved regions for the walk. <para>整片遍历所解析出的区域列表。</para></returns>
    private static IReadOnlyList<MtkBulkReadRegion> ResolveRegions(MtkSession session, MtkDaProtocol family)
    {
        var regions = family switch
        {
            MtkDaProtocol.XFlash => ResolveXFlashRegions(session),
            MtkDaProtocol.Xml => ResolveXmlRegions(session),
            MtkDaProtocol.Legacy => ResolveLegacyRegions(session),
            _ => throw new ArgumentOutOfRangeException(nameof(family),
                "The bulk read supports the XFlash, XML and Legacy download agent families.")
        };
        if (regions.Count == 0)
        {
            throw new InvalidOperationException(
                $"The {family} download agent reports no ordinary region with a positive capacity, so there is " +
                "no whole-flash area to read.");
        }

        if (regions.Count > MaximumWholeFlashRegions)
        {
            throw new NotSupportedException(
                $"The {family} download agent reports {regions.Count} regions; the host reads at most " +
                $"{MaximumWholeFlashRegions} per whole-flash dump.");
        }

        return regions;
    }

    private static List<MtkBulkReadRegion> ResolveXFlashRegions(MtkSession session)
    {
        var storage = session.XFlashStorage ?? throw new InvalidOperationException(
            "The XFlash download agent has not reported storage geometry, so its flash area cannot be sized.");
        RequireByteAddressable(storage.Kind, "XFlash");
        var regions = new List<MtkBulkReadRegion>();
        foreach (var region in storage.Regions)
        {
            if (region.RequiresAuthenticatedAccess || region.CapacityBytes <= 0)
            {
                continue;
            }

            var partitionId = region.PartitionId;
            regions.Add(new MtkBulkReadRegion(XFlashRegionName(storage.Kind, partitionId), region.CapacityBytes,
                storage.BlockSize, BoundChunk(storage.ReadPacketLength, storage.BlockSize),
                (value, offset, length, destination, token) => value.ReadXFlashStorageAsync(partitionId, offset,
                    length, destination, progress: null, cancellationToken: token)));
        }

        return regions;
    }

    private static List<MtkBulkReadRegion> ResolveXmlRegions(MtkSession session)
    {
        var storage = session.XmlStorage ?? throw new InvalidOperationException(
            "The XML download agent has not reported storage geometry, so its flash area cannot be sized.");
        var regions = new List<MtkBulkReadRegion>();
        foreach (var region in storage.Regions)
        {
            if (region.RequiresAuthenticatedAccess || region.CapacityBytes <= 0)
            {
                continue;
            }

            var regionName = region.Name;
            regions.Add(new MtkBulkReadRegion(regionName, region.CapacityBytes, storage.BlockSize,
                BoundChunk(MaximumChunkBytes, storage.BlockSize),
                (value, offset, length, destination, token) => value.ReadXmlStorageAsync(regionName, offset, length,
                    destination, progress: null, cancellationToken: token)));
        }

        return regions;
    }

    private static List<MtkBulkReadRegion> ResolveLegacyRegions(MtkSession session)
    {
        var storage = session.LegacyStorage ?? throw new InvalidOperationException(
            "The Legacy download agent has not reported storage geometry, so its flash area cannot be sized.");
        RequireByteAddressable(storage.Kind, "Legacy");
        if (storage.Kind == MtkXFlashStorageKind.Nor)
        {
            var nor = storage.Nor ?? throw new InvalidOperationException(
                "The Legacy download agent reports NOR storage without a NOR inventory.");
            return
            [
                new MtkBulkReadRegion(NorRegionName, nor.CapacityBytes, 1, BoundChunk(MaximumChunkBytes, 1),
                    (value, offset, length, destination, token) => value.ReadLegacyNorStorageAsync(offset, length,
                        destination, progress: null, cancellationToken: token))
            ];
        }

        var regions = new List<MtkBulkReadRegion>();
        foreach (var region in storage.Regions)
        {
            if (region.RequiresAuthenticatedAccess || region.CapacityBytes <= 0)
            {
                continue;
            }

            var partitionId = region.PartitionId;
            regions.Add(new MtkBulkReadRegion(LegacyRegionName(storage.Kind, partitionId), region.CapacityBytes,
                LegacySectorBytes, BoundChunk(MaximumChunkBytes, LegacySectorBytes),
                (value, offset, length, destination, token) => value.ReadLegacyStorageAsync(partitionId, offset,
                    length, destination, progress: null, cancellationToken: token)));
        }

        return regions;
    }

    /// <summary>Resolves one region for a raw-sector window, naming the family that cannot express the read.
    /// <para>为原始扇区窗口解析单个区域，并点名无法表达该读取的家族。</para></summary>
    /// <param name="session">A session whose storage geometry the named family already discovered.
    /// <para>已由该命名家族发现存储几何信息的会话。</para></param>
    /// <param name="family">The download agent family that serves the read; no fallback occurs.
    /// <para>提供本次读取的下载代理家族；不发生回退。</para></param>
    /// <param name="region">A decimal partition identifier or an exact XML region token.
    /// <para>十进制分区标识符或精确的 XML 区域标记。</para></param>
    /// <returns>The resolved region for the window. <para>为该窗口解析出的区域。</para></returns>
    private static MtkBulkReadRegion ResolveSectorRegion(MtkSession session, MtkDaProtocol family, string region)
    {
        switch (family)
        {
            case MtkDaProtocol.XFlash:
                {
                    var storage = session.XFlashStorage ?? throw new InvalidOperationException(
                        "The XFlash download agent has not reported storage geometry.");
                    RequireByteAddressable(storage.Kind, "XFlash");
                    var partitionId = ParsePartitionId(region, family);
                    var match = storage.Regions.FirstOrDefault(candidate => candidate.PartitionId == partitionId)
                        ?? throw new NotSupportedException(
                            $"The XFlash download agent does not report physical partition {partitionId}.");
                    if (match.RequiresAuthenticatedAccess)
                    {
                        throw new NotSupportedException(
                            $"The XFlash download agent reserves physical partition {partitionId} for a separate authenticated protocol.");
                    }

                    return new MtkBulkReadRegion(XFlashRegionName(storage.Kind, partitionId), match.CapacityBytes,
                        storage.BlockSize, BoundChunk(storage.ReadPacketLength, storage.BlockSize),
                        (value, offset, length, destination, token) => value.ReadXFlashStorageAsync(partitionId, offset,
                            length, destination, progress: null, cancellationToken: token));
                }
            case MtkDaProtocol.Legacy:
                {
                    var storage = session.LegacyStorage ?? throw new InvalidOperationException(
                        "The Legacy download agent has not reported storage geometry.");
                    RequireByteAddressable(storage.Kind, "Legacy");
                    if (storage.Kind == MtkXFlashStorageKind.Nor)
                    {
                        if (!string.Equals(region, NorRegionName, StringComparison.Ordinal))
                        {
                            throw new NotSupportedException(
                                $"The Legacy download agent exposes NOR flash as the single '{NorRegionName}' region, not '{region}'.");
                        }

                        var nor = storage.Nor ?? throw new InvalidOperationException(
                            "The Legacy download agent reports NOR storage without a NOR inventory.");
                        return new MtkBulkReadRegion(NorRegionName, nor.CapacityBytes, 1, BoundChunk(MaximumChunkBytes, 1),
                            (value, offset, length, destination, token) => value.ReadLegacyNorStorageAsync(offset, length,
                                destination, progress: null, cancellationToken: token));
                    }

                    var partitionId = (byte)ParsePartitionId(region, family);
                    var match = storage.Regions.FirstOrDefault(candidate => candidate.PartitionId == partitionId)
                        ?? throw new NotSupportedException(
                            $"The Legacy download agent does not report physical partition {partitionId}.");
                    if (match.RequiresAuthenticatedAccess)
                    {
                        throw new NotSupportedException(
                            $"The Legacy download agent reserves physical partition {partitionId} for a separate authenticated protocol.");
                    }

                    return new MtkBulkReadRegion(LegacyRegionName(storage.Kind, partitionId), match.CapacityBytes,
                        LegacySectorBytes, BoundChunk(MaximumChunkBytes, LegacySectorBytes),
                        (value, offset, length, destination, token) => value.ReadLegacyStorageAsync(partitionId, offset,
                            length, destination, progress: null, cancellationToken: token));
                }
            case MtkDaProtocol.Xml:
                {
                    var storage = session.XmlStorage ?? throw new InvalidOperationException(
                        "The XML download agent has not reported storage geometry.");
                    var match = storage.Regions.FirstOrDefault(candidate => candidate.Name == region)
                        ?? throw new NotSupportedException(
                            $"The XML download agent does not report the region '{region}'.");
                    if (match.RequiresAuthenticatedAccess)
                    {
                        throw new NotSupportedException(
                            $"The XML download agent reserves {region} for a separate authenticated protocol.");
                    }

                    return new MtkBulkReadRegion(region, match.CapacityBytes, storage.BlockSize,
                        BoundChunk(MaximumChunkBytes, storage.BlockSize),
                        (value, offset, length, destination, token) => value.ReadXmlStorageAsync(region, offset, length,
                            destination, progress: null, cancellationToken: token));
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(family),
                    "The bulk read supports the XFlash, XML and Legacy download agent families.");
        }
    }

    /// <summary>Refuses a storage family whose read path has no contiguous byte addressing.
    /// <para>拒绝读取路径不具备连续字节寻址能力的存储家族。</para></summary>
    /// <param name="kind">The reported storage family kind. <para>上报的存储家族类型。</para></param>
    /// <param name="family">The family name used in the refusal. <para>拒绝时使用的家族名称。</para></param>
    private static void RequireByteAddressable(MtkXFlashStorageKind kind, string family)
    {
        if (kind == MtkXFlashStorageKind.Nand)
        {
            throw new NotSupportedException(
                $"The {family} download agent addresses NAND through dedicated page-stream methods, so it cannot " +
                "express a byte-range whole-flash or raw-sector read; no other family is substituted.");
        }
    }

    private static uint ParsePartitionId(string region, MtkDaProtocol family) => uint.TryParse(region,
        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
        ? value
        : throw new ArgumentException(
            $"The {family} raw-sector read addresses a physical region by its decimal partition identifier, not '{region}'.",
            nameof(region));

    /// <summary>Names one XFlash physical region with the canonical user-area token where the family has one.
    /// <para>为 XFlash 物理区域命名；在家族存在用户区时使用规范用户区标记。</para></summary>
    /// <param name="kind">The reported storage family kind. <para>上报的存储家族类型。</para></param>
    /// <param name="partitionId">The physical partition identifier. <para>物理分区标识符。</para></param>
    /// <returns>The canonical region token. <para>规范区域标记。</para></returns>
    private static string XFlashRegionName(MtkXFlashStorageKind kind, uint partitionId) => kind switch
    {
        MtkXFlashStorageKind.Emmc or MtkXFlashStorageKind.Ufs when
            partitionId == MtkScatterPackage.GetPhysicalPartition(kind)
            => MtkScatterPackage.GetXmlRegionName(kind),
        _ => $"{kind}-PARTITION-{partitionId}"
    };

    private static string LegacyRegionName(MtkXFlashStorageKind kind, byte partitionId) =>
        kind == MtkXFlashStorageKind.Emmc && partitionId == 8
            ? MtkScatterPackage.GetXmlRegionName(kind)
            : $"{kind}-PARTITION-{partitionId}";

    /// <summary>Turns a device-reported region token into a file name, refusing anything that could escape the
    /// output directory. <para>将设备上报的区域标记转为文件名，并拒绝任何可能逃出输出目录的标记。</para></summary>
    /// <param name="regionName">The device-reported region token. <para>设备上报的区域标记。</para></param>
    /// <returns>The region token with a bin extension appended. <para>附加 bin 扩展名后的区域标记。</para></returns>
    private static string SafeRegionFileName(string regionName)
    {
        if (regionName.Length is 0 or > 126 || regionName is "." or ".."
            || regionName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new NotSupportedException(
                $"The download agent reported the region name '{regionName}', which is not a usable file name.");
        }

        return regionName + ".bin";
    }

    private static int BoundChunk(int reportedPacketBytes, int alignmentBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reportedPacketBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignmentBytes);
        var candidate = Math.Min(reportedPacketBytes, MaximumChunkBytes);
        var aligned = candidate - candidate % alignmentBytes;
        return aligned > 0 ? aligned : alignmentBytes;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sink = new HashingWriteStream(Stream.Null);
        await input.CopyToAsync(sink, 65536, ct).ConfigureAwait(false);
        return Convert.ToHexString(sink.GetHashAndReset());
    }

    private static string NormalizeExpectedDigest(string value, string parameterName)
    {
        var text = value.Trim();
        if (text.Length != 64 || !text.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("An expected SHA-256 must be exactly 64 hexadecimal characters.",
                parameterName);
        }

        return text.ToUpperInvariant();
    }

    private static int Percent(long completed, long total) =>
        (int)Math.Clamp(Math.Round(completed / (double)total * 100, MidpointRounding.AwayFromZero), 0, 100);

    private static void DiscardStaging(string stagingPath)
    {
        try
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>One sized, chunked and byte-addressed region read scoped to a single agent family.
    /// <para>已定容量、已分块且可按字节寻址的单一代理家族区域读取。</para></summary>
    /// <param name="Name">The canonical region token used for naming and progress.
    /// <para>用于命名与进度显示的规范区域标记。</para></param>
    /// <param name="CapacityBytes">The capacity the agent reported for that region.
    /// <para>代理为该区域上报的容量。</para></param>
    /// <param name="SectorBytes">The family's own sector or page size behind the raw-sector conversion.
    /// <para>原始扇区换算所依据的家族扇区或页大小。</para></param>
    /// <param name="ChunkBytes">The chunk bound used for every chunk except a shorter tail.
    /// <para>除较短尾块外每块使用的上限。</para></param>
    /// <param name="Read">The family's range read into the supplied destination stream.
    /// <para>该家族写入传入目标流的范围读取。</para></param>
    private sealed record MtkBulkReadRegion(string Name, long CapacityBytes, int SectorBytes, int ChunkBytes,
        Func<MtkSession, long, int, Stream, CancellationToken, Task> Read);
}

/// <summary>Describes one published bulk-read image: the verb that asked for it, the region it came from,
/// its destination, byte count, SHA-256 and the chunk bound the agent was asked for.
/// <para>描述一份已发布的批量读取镜像：请求它的动词、来源区域、目标路径、字节数、SHA-256 以及向代理请求的分块上限。</para></summary>
/// <param name="Family">The agent family that served the read. <para>提供本次读取的代理家族。</para></param>
/// <param name="Verb">The requesting verb, whole-flash or raw-sector. <para>发起请求的整片或扇区读取动词。</para></param>
/// <param name="RegionName">The canonical region token that was read. <para>被读取的规范区域标记。</para></param>
/// <param name="DestinationPath">The published file path. <para>已发布文件的路径。</para></param>
/// <param name="ByteLength">The published byte count. <para>已发布的字节数。</para></param>
/// <param name="Sha256Hex">The upper-case SHA-256 of the published bytes. <para>已发布字节的大写 SHA-256。</para></param>
/// <param name="ChunkBytes">The chunk bound asked of the agent. <para>向代理请求的分块上限。</para></param>
internal sealed record MtkBulkReadRecord(MtkDaProtocol Family, string Verb, string RegionName,
    string DestinationPath, long ByteLength, string Sha256Hex, int ChunkBytes);

/// <summary>Aggregates one whole-flash dump, which publishes one image per reported region.
/// <para>汇总一次整片闪存读取；每个上报区域发布一份镜像。</para></summary>
/// <param name="Family">The agent family that served the read. <para>提供本次读取的代理家族。</para></param>
/// <param name="Verb">The whole-flash verb that produced the dump. <para>产生本次转储的整片读取动词。</para></param>
/// <param name="Regions">One record per published region. <para>每个已发布区域一条记录。</para></param>
/// <param name="TotalBytes">The checked total of published bytes. <para>经溢出检查的已发布总字节数。</para></param>
internal sealed record MtkBulkReadResult(MtkDaProtocol Family, string Verb,
    IReadOnlyList<MtkBulkReadRecord> Regions, long TotalBytes);