using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Storage;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Captures device storage over absolute region-agnostic addresses: a walk of the reported
/// addressable area, a start-sector and count read, or a byte offset and length read.
/// <para>按与区域无关的绝对地址采集设备存储：遍历代理几何信息所上报的可寻址区域、按起始扇区与扇区数读取，或按字节偏移与长度读取。</para>
/// </summary>
internal static class MtkFlashAreaService
{
    /// <summary>
    /// Walks the whole ordinary storage area the family reported into one file, matching the reference tool's
    /// <c>rf</c>.
    /// <para>遍历该家族上报的整片普通存储区域并写入一个文件，对应参考工具的 <c>rf</c>。</para>
    /// </summary>
    /// <param name="session">A session whose geometry the named family already discovered. <para>已由该家族发现几何信息的会话。</para></param>
    /// <param name="family">The explicitly selected agent family. <para>显式选定的代理家族，不回退到其他家族。</para></param>
    /// <param name="destinationPath">A destination file inside an existing directory. <para>位于已存在目录中的目标文件。</para></param>
    /// <param name="progress">Optional cumulative byte progress. <para>可选的累计字节进度。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The published image's family, regions, byte count, digest and alignment bounds. <para>已发布镜像的家族、区域、字节数、摘要与对齐界限。</para></returns>
    internal static Task<MtkAreaDumpRecord> CaptureEntireAsync(MtkSession session, MtkDaProtocol family,
        string destinationPath, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var map = ResolveMap(session, family);
        return CaptureAsync(session, family, map, 0, map.TotalBytes, destinationPath, progress, ct);
    }

    /// <summary>
    /// Reads an arbitrary start sector and sector count, matching the reference tool's <c>rs</c>; the sector
    /// numbers are multiplied by the transfer unit the family reported.
    /// <para>读取任意起始扇区与扇区数，对应参考工具的 <c>rs</c>；扇区数乘以该家族上报的传输单位。</para>
    /// </summary>
    /// <param name="session">A session whose geometry the named family already discovered. <para>已由该家族发现几何信息的会话。</para></param>
    /// <param name="family">The explicitly selected agent family. <para>显式选定的代理家族。</para></param>
    /// <param name="startSector">The zero-based start sector. <para>零基起始扇区号。</para></param>
    /// <param name="sectorCount">The positive sector count. <para>正数扇区数。</para></param>
    /// <param name="destinationPath">The destination file path. <para>目标文件路径。</para></param>
    /// <param name="progress">Optional cumulative byte progress. <para>可选的累计字节进度。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The record of the published image. <para>已发布镜像的记录。</para></returns>
    internal static Task<MtkAreaDumpRecord> CaptureSectorsAsync(MtkSession session, MtkDaProtocol family,
        long startSector, long sectorCount, string destinationPath, IProgress<FlashProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var map = ResolveMap(session, family);
        var (offset, length) = map.ResolveSectorRange(startSector, sectorCount);
        return CaptureAsync(session, family, map, offset, length, destinationPath, progress, ct);
    }

    /// <summary>
    /// Reads by absolute byte offset and length, matching the reference tool's <c>ro</c>; the offset and length
    /// still have to satisfy the family's transfer alignment.
    /// <para>按绝对字节偏移与长度读取，对应参考工具的 <c>ro</c>；偏移与长度仍须满足该家族的传输对齐。</para>
    /// </summary>
    /// <param name="session">A session whose geometry the named family already discovered. <para>已由该家族发现几何信息的会话。</para></param>
    /// <param name="family">The explicitly selected agent family. <para>显式选定的代理家族。</para></param>
    /// <param name="offset">The absolute byte offset. <para>绝对字节偏移。</para></param>
    /// <param name="length">The positive byte length. <para>正数字节长度。</para></param>
    /// <param name="destinationPath">The destination file path. <para>目标文件路径。</para></param>
    /// <param name="progress">Optional cumulative byte progress. <para>可选的累计字节进度。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The record of the published image. <para>已发布镜像的记录。</para></returns>
    internal static Task<MtkAreaDumpRecord> CaptureBytesAsync(MtkSession session, MtkDaProtocol family, long offset,
        long length, string destinationPath, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var map = ResolveMap(session, family);
        return CaptureAsync(session, family, map, offset, length, destinationPath, progress, ct);
    }

    private static async Task<MtkAreaDumpRecord> CaptureAsync(MtkSession session, MtkDaProtocol family,
        MtkFlashAreaMap map, long offset, long length, string destinationPath, IProgress<FlashProgress>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ct.ThrowIfCancellationRequested();
        var segments = map.Plan(offset, length);
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new DirectoryNotFoundException($"The dump destination '{destination}' has no parent directory.");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The dump destination directory '{directory}' does not exist.");
        }

        var stagingPath = destination + ".uotan-" + Guid.NewGuid().ToString("N");
        long captured;
        string sha256Hex;
        var reporter = new AreaProgress(family, length, progress);
        try
        {
            progress?.Report(new FlashProgress(family.ToString(), 0,
                $"Reading {length} bytes from absolute offset {offset} across {segments.Count} region(s) " +
                $"[{string.Join(", ", map.Regions.Select(region => region.RegionName))}] in " +
                $"{map.ChunkBytes}-byte chunks aligned to {map.AlignmentBytes} bytes..."));
            await using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hashing = new HashingWriteStream(staging, leaveOpen: true);
                switch (family)
                {
                    case MtkDaProtocol.XFlash:
                        await session.ReadXFlashAreaAsync(map, offset, length, hashing, reporter, ct)
                            .ConfigureAwait(false);
                        break;
                    case MtkDaProtocol.Legacy:
                        await session.ReadLegacyAreaAsync(map, offset, length, hashing, progress: reporter,
                            cancellationToken: ct).ConfigureAwait(false);
                        break;
                    default:
                        await session.ReadXmlAreaAsync(map, offset, length, hashing, progress: reporter,
                            cancellationToken: ct).ConfigureAwait(false);
                        break;
                }

                await staging.FlushAsync(ct).ConfigureAwait(false);
                captured = staging.Length;
                if (captured != length)
                {
                    throw new IOException(
                        $"The staged {family} area image holds {captured} bytes but {length} were requested, so " +
                        "the destination was left untouched.");
                }

                sha256Hex = hashing.GetHashHex();
            }

            ct.ThrowIfCancellationRequested();
            File.Move(stagingPath, destination, overwrite: true);
        }
        finally
        {
            reporter.Flush();
            DiscardStaging(stagingPath);
        }

        progress?.Report(new FlashProgress(family.ToString(), 100,
            $"Published {captured} bytes from absolute offset {offset} with SHA-256 {sha256Hex}."));
        return new MtkAreaDumpRecord(family, map.Source, offset, captured, sha256Hex, map.ChunkBytes,
            map.AlignmentBytes, map.TotalBytes, [.. map.Regions]);
    }

    private static MtkFlashAreaMap ResolveMap(MtkSession session, MtkDaProtocol family) => family switch
    {
        MtkDaProtocol.XFlash => MtkFlashAreaMap.FromXFlash(session.XFlashStorage ?? throw new InvalidOperationException(
            "The XFlash download agent has not reported storage geometry, so no region-agnostic area can be " +
            "sized for it; no other family is substituted.")),
        MtkDaProtocol.Legacy => MtkFlashAreaMap.FromLegacy(session.LegacyStorage ?? throw new InvalidOperationException(
            "The Legacy download agent has not reported storage geometry, so no region-agnostic area can be sized " +
            "for it; no other family is substituted.")),
        MtkDaProtocol.Xml => MtkFlashAreaMap.FromXml(session.XmlStorage ?? throw new InvalidOperationException(
            "The XML download agent has not reported storage geometry, so no region-agnostic area can be sized for " +
            "it; no other family is substituted.")),
        _ => throw new ArgumentOutOfRangeException(nameof(family),
            "The region-agnostic area read supports the XFlash, XML and Legacy download agent families.")
    };

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

    /// <summary>
    /// Turns the per-chunk delivered byte totals the walk reports into flashing progress, publishing at most one
    /// status per window so a multi-gigabyte read cannot queue millions of records; <see cref="Flush"/> still
    /// hands the newest total to the sink when the walk ends, faults or is cancelled.
    /// <para>把遍历逐块上报的已交付字节总数转成刷写进度，每个窗口最多发布一条状态，因此数 GB 的读取不会排队
    /// 上百万条记录；遍历结束、出错或被取消时，<see cref="Flush"/> 仍会把最新的总数交给接收方。</para>
    /// </summary>
    private sealed class AreaProgress(MtkDaProtocol family, long totalBytes, IProgress<FlashProgress>? sink)
        : IProgress<long>
    {
        private readonly ThrottledFlashByteProgress throttle = new(captured => sink?.Report(new FlashProgress(
            family.ToString(),
            totalBytes <= 0 ? 0 : (int)Math.Clamp(captured * 100 / totalBytes, 0, 100),
            $"Read {captured} of {totalBytes} bytes of the {family} region-agnostic area...")));

        public void Report(long value) => throttle.Report(value);

        /// <summary>Publishes the newest delivered total the window suppressed.
        /// <para>发布窗口所抑制的最新已交付总数。</para></summary>
        internal void Flush() => throttle.Flush();
    }
}

/// <summary>
/// The result of one region-agnostic absolute-range capture: the family, the requested range, the published
/// bytes, the SHA-256 and the walk bounds.
/// <para>一次与区域无关的绝对范围采集结果：家族、请求范围、已发布字节、SHA-256 与遍历界限。</para>
/// </summary>
/// <param name="Family">The agent family that served the read. <para>提供本次读取的代理家族。</para></param>
/// <param name="Source">The discovery path that reported the address space. <para>上报该地址空间的发现路径。</para></param>
/// <param name="AbsoluteOffset">The requested absolute start byte. <para>请求的绝对起始字节。</para></param>
/// <param name="ByteLength">The published byte count. <para>已发布的字节数。</para></param>
/// <param name="Sha256Hex">The lowercase hexadecimal SHA-256 of the published bytes. <para>已发布字节的小写十六进制 SHA-256。</para></param>
/// <param name="ChunkBytes">The per-transfer bound except a shorter tail. <para>除较短尾块外每次传输的上限。</para></param>
/// <param name="AlignmentBytes">The transfer alignment the request had to satisfy. <para>请求所满足的传输对齐。</para></param>
/// <param name="TotalAddressableBytes">The total ordinary address space the family reported. <para>该家族上报的普通地址空间总大小。</para></param>
/// <param name="Regions">The regions forming the space with their absolute base offsets. <para>构成该地址空间的区域及其绝对基准偏移。</para></param>
internal sealed record MtkAreaDumpRecord(MtkDaProtocol Family, MtkStorageDiscoverySource Source, long AbsoluteOffset,
    long ByteLength, string Sha256Hex, int ChunkBytes, int AlignmentBytes, long TotalAddressableBytes,
    IReadOnlyList<MtkFlashAreaRegion> Regions);