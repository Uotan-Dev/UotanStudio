using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Services.Flash;

/// <summary>Reads one MediaTek download agent's whole user data area into a single contiguous image.
/// <para>将联发科下载代理的整个用户数据区读取为一份连续镜像。</para></summary>
/// <remarks>
/// <para>This is the host counterpart of the reference DA handler's whole-user-area read: it walks the byte
/// range the agent reported for that family's user region in chunks bounded by the family's own reported
/// packet or block limits, appends every chunk to a staging file, and replaces the destination only after the
/// staged byte count equals the requested length. The image is never buffered in memory, so a device capacity
/// is treated strictly as an addressing bound and never as a buffer size.</para>
/// <para>这是参考 DA 处理程序“整用户区读取”在宿主机侧的对应实现：按该家族用户区域上报的字节范围，以家族自报
/// 的包长或块大小为上限分块追加到暂存文件，只有暂存字节数与请求长度相等时才替换目标。镜像绝不在内存中整体缓冲，
/// 因此设备容量只是寻址界限，绝不是缓冲区大小。</para>
/// <para>The path is read-only: no erase, write, padding, retry, session startup, geometry discovery or
/// authorization is performed. The caller supplies a session whose storage geometry was already discovered by
/// the matching agent family, and every refusal happens before the first device byte is requested.</para>
/// <para>本路径为只读：不擦除、不写入、不填充、不重试，也不启动会话、发现几何信息或授予授权。调用方提供已由
/// 对应代理家族发现存储几何信息的会话，所有拒绝都发生在请求第一个设备字节之前。</para>
/// </remarks>
internal static class MtkFullDumpService
{
    private const int MaximumChunkBytes = 1024 * 1024;
    private const string NorRegionName = "NOR-DEVICE";

    /// <summary>Captures the whole reported user area of one download agent family to a file.
    /// <para>将某个下载代理家族上报的整个用户区采集到文件。</para></summary>
    /// <param name="session">A session whose storage geometry the named family already discovered.
    /// <para>已由该命名家族发现存储几何信息的会话。</para></param>
    /// <param name="family">The download agent family whose user area is read; no fallback occurs.
    /// <para>要读取用户区的下载代理家族；不发生回退。</para></param>
    /// <param name="destinationPath">Destination file inside an existing directory; replaced only on success.
    /// <para>位于已存在目录中的目标文件；仅在成功时被替换。</para></param>
    /// <param name="progress">Optional per-chunk progress; reaching one hundred percent is not publication.
    /// <para>可选的分块进度；达到 100% 并不等于发布成功。</para></param>
    /// <param name="ct">Cancellation honoured before any device byte and between every chunk.
    /// <para>在请求任何设备字节之前以及每块之间都会响应的取消令牌。</para></param>
    /// <returns>The published image's family, region, byte count, SHA-256, chunk bound and capacity fidelity.
    /// <para>已发布镜像的家族、区域、字节数、SHA-256、分块上限与容量是否按原值使用。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="destinationPath"/> is empty or whitespace.
    /// <para><paramref name="destinationPath"/> 为空或仅空白。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="family"/> is not a defined agent family.
    /// <para><paramref name="family"/> 不是已定义的代理家族。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before the first device byte or
    /// at any cooperative point inside a chunk. <para>在首个设备字节之前或块内任一协作点请求了取消。</para></exception>
    /// <exception cref="InvalidOperationException">The family has not reported geometry, or its user region
    /// reports no capacity or a non-positive one. <para>该家族未上报几何信息，或其用户区域未上报容量或容量非正。</para></exception>
    /// <exception cref="NotSupportedException">The family's read path cannot address its user region.
    /// <para>该家族的读取路径无法寻址其用户区域。</para></exception>
    /// <exception cref="DirectoryNotFoundException">The destination directory does not exist.
    /// <para>目标目录不存在。</para></exception>
    /// <exception cref="IOException">The staged image length did not match the requested length, or publication
    /// failed. <para>暂存镜像长度与请求长度不符，或发布失败。</para></exception>
    internal static async Task<MtkFullDumpRecord> CaptureAsync(MtkSession session, MtkDaProtocol family,
        string destinationPath, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ct.ThrowIfCancellationRequested();
        var target = Resolve(session, family);
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new DirectoryNotFoundException($"The dump destination '{destination}' has no parent directory.");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"The dump destination directory '{directory}' does not exist.");
        }

        ct.ThrowIfCancellationRequested();
        var stagingPath = destination + ".uotan-" + Guid.NewGuid().ToString("N");
        long captured;
        string sha256Hex;
        try
        {
            progress?.Report(new FlashProgress(target.RegionName, 0,
                $"Reading {target.RequestedBytes} bytes of the {family} user area '{target.RegionName}' " +
                $"in {target.ChunkBytes}-byte chunks..."));
            await using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hashingDestination = new HashingWriteStream(staging, leaveOpen: true);
                for (var offset = 0L; offset < target.RequestedBytes;)
                {
                    ct.ThrowIfCancellationRequested();
                    var length = (int)Math.Min(target.ChunkBytes, target.RequestedBytes - offset);
                    await target.Read(session, offset, length, hashingDestination, ct).ConfigureAwait(false);
                    offset += length;
                    progress?.Report(new FlashProgress(target.RegionName, Percent(offset, target.RequestedBytes),
                        $"Read {offset} of {target.RequestedBytes} bytes of the {family} user area..."));
                }

                captured = staging.Length;
                if (captured != target.RequestedBytes)
                {
                    throw new IOException(
                        $"The staged {family} user-area image holds {captured} bytes but {target.RequestedBytes} " +
                        "were reported, so the destination was left untouched.");
                }

                sha256Hex = Convert.ToHexString(hashingDestination.GetHashAndReset());
            }

            ct.ThrowIfCancellationRequested();
            File.Move(stagingPath, destination, overwrite: true);
        }
        finally
        {
            DiscardStaging(stagingPath);
        }

        progress?.Report(new FlashProgress(target.RegionName, 100,
            $"Published {captured} bytes of the {family} user area with SHA-256 {sha256Hex}."));
        return new MtkFullDumpRecord(family, target.RegionName, captured, sha256Hex, target.ChunkBytes,
            target.RequestedBytes == target.ReportedCapacityBytes);
    }

    /// <summary>Resolves one family's user region from geometry the session already holds, without device I/O.
    /// <para>从会话已持有的几何信息解析该家族的用户区域，不进行设备 I/O。</para></summary>
    /// <param name="session">The session whose discovered geometry is inspected.
    /// <para>被检查已发现几何信息的会话。</para></param>
    /// <param name="family">The explicitly selected agent family. <para>显式选定的代理家族。</para></param>
    /// <returns>The sized, chunked and addressed read target. <para>已定容量、已分块且已寻址的读取目标。</para></returns>
    private static MtkDumpTarget Resolve(MtkSession session, MtkDaProtocol family) => family switch
    {
        MtkDaProtocol.Xml => ResolveXml(session),
        MtkDaProtocol.XFlash => ResolveXFlash(session),
        MtkDaProtocol.Legacy => ResolveLegacy(session),
        _ => throw new ArgumentOutOfRangeException(nameof(family),
            "The full dump supports the XFlash, XML and Legacy download agent families.")
    };

    private static MtkDumpTarget ResolveXml(MtkSession session)
    {
        var storage = session.XmlStorage ?? throw new InvalidOperationException(
            "The XML download agent has not reported storage geometry, so its user area cannot be sized.");
        var regionName = storage.UserDataRegionName;
        var region = storage.Regions.FirstOrDefault(candidate => candidate.Name == regionName)
            ?? throw new NotSupportedException(
                $"The XML download agent does not report the {regionName} user-data region.");
        if (region.RequiresAuthenticatedAccess)
        {
            throw new NotSupportedException(
                $"The XML download agent reserves {regionName} for a separate authenticated protocol.");
        }

        return MtkDumpTarget.Create(regionName, region.CapacityBytes, storage.BlockSize,
            BoundChunk(MaximumChunkBytes, storage.BlockSize),
            (value, offset, length, destination, token) => value.ReadXmlStorageAsync(regionName, offset, length,
                destination, progress: null, cancellationToken: token));
    }

    private static MtkDumpTarget ResolveXFlash(MtkSession session)
    {
        var storage = session.XFlashStorage ?? throw new InvalidOperationException(
            "The XFlash download agent has not reported storage geometry, so its user area cannot be sized.");
        if (storage.Kind == MtkXFlashStorageKind.Nand)
        {
            throw new NotSupportedException(
                "The XFlash download agent addresses NAND through dedicated page-stream methods, so it cannot " +
                "read a contiguous user-area image.");
        }

        var partitionId = storage.Kind switch
        {
            MtkXFlashStorageKind.Ufs => 3u,
            _ => 8u
        };
        var region = storage.Regions.FirstOrDefault(candidate => candidate.PartitionId == partitionId)
            ?? throw new NotSupportedException(
                $"The XFlash download agent does not report physical partition {partitionId}.");
        if (region.RequiresAuthenticatedAccess)
        {
            throw new NotSupportedException(
                $"The XFlash download agent reserves physical partition {partitionId} for authenticated access.");
        }

        var regionName = UserAreaName(storage.Kind);
        var chunk = BoundChunk(storage.ReadPacketLength, storage.BlockSize);
        return MtkDumpTarget.Create(regionName, region.CapacityBytes, storage.BlockSize, chunk,
            (value, offset, length, destination, token) => value.ReadXFlashStorageAsync(partitionId, offset, length,
                destination, progress: null, cancellationToken: token));
    }

    private static MtkDumpTarget ResolveLegacy(MtkSession session)
    {
        var storage = session.LegacyStorage ?? throw new InvalidOperationException(
            "The Legacy download agent has not reported storage geometry, so its user area cannot be sized.");
        if (storage.Kind == MtkXFlashStorageKind.Nand)
        {
            throw new NotSupportedException(
                "The Legacy download agent exposes NAND through dedicated page methods, so it cannot read a " +
                "contiguous user-area image.");
        }

        if (storage.Kind == MtkXFlashStorageKind.Nor)
        {
            var nor = storage.Nor ?? throw new InvalidOperationException(
                "The Legacy download agent reports NOR storage without a NOR inventory.");
            return MtkDumpTarget.Create(NorRegionName, nor.CapacityBytes, 1,
                BoundChunk(MaximumChunkBytes, 1),
                (value, offset, length, destination, token) => value.ReadLegacyNorStorageAsync(offset, length,
                    destination, progress: null, cancellationToken: token));
        }

        const byte partitionId = 8;
        var region = storage.Regions.FirstOrDefault(candidate => candidate.PartitionId == partitionId)
            ?? throw new NotSupportedException(
                $"The Legacy download agent does not report physical partition {partitionId}.");
        if (region.RequiresAuthenticatedAccess)
        {
            throw new NotSupportedException(
                $"The Legacy download agent reserves physical partition {partitionId} for authenticated access.");
        }

        const int alignment = 512;
        return MtkDumpTarget.Create(UserAreaName(storage.Kind), region.CapacityBytes, alignment,
            BoundChunk(MaximumChunkBytes, alignment),
            (value, offset, length, destination, token) => value.ReadLegacyStorageAsync(partitionId, offset, length,
                destination, progress: null, cancellationToken: token));
    }

    /// <summary>Names the user area the reference tool addresses for one observed block-storage family.
    /// <para>为某个已观测块存储家族命名参考工具所寻址的用户区。</para></summary>
    /// <param name="kind">The storage family reported by the agent. <para>代理所上报的存储家族。</para></param>
    /// <returns>The canonical region token. <para>规范区域标记。</para></returns>
    private static string UserAreaName(MtkXFlashStorageKind kind) => kind == MtkXFlashStorageKind.Nor
        ? NorRegionName
        : MtkScatterPackage.GetXmlRegionName(kind);

    /// <summary>Bounds one chunk by the family's reported packet limit and its own transfer alignment.
    /// <para>以家族自报的包长度上限与其传输对齐约束单次分块。</para></summary>
    /// <param name="reportedPacketBytes">The packet bound the agent reported, or the host bound when the agent
    /// reports none. <para>代理上报的包上限；代理未上报时使用宿主机上限。</para></param>
    /// <param name="alignmentBytes">The alignment the family's read path enforces.
    /// <para>该家族读取路径所强制的对齐。</para></param>
    /// <returns>A positive, alignment-respecting chunk size. <para>正的、满足对齐的分块大小。</para></returns>
    private static int BoundChunk(int reportedPacketBytes, int alignmentBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reportedPacketBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignmentBytes);
        var candidate = Math.Min(reportedPacketBytes, MaximumChunkBytes);
        var aligned = candidate - candidate % alignmentBytes;
        return aligned > 0 ? aligned : alignmentBytes;
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

    /// <summary>One sized and addressed user-area read target.
    /// <para>一个已定容量且已寻址的用户区读取目标。</para></summary>
    /// <param name="RegionName">The canonical user-region or whole-device token.
    /// <para>规范的用户区或整片设备标记。</para></param>
    /// <param name="ReportedCapacityBytes">The capacity the agent reported for that region.
    /// <para>代理为该区域上报的容量。</para></param>
    /// <param name="RequestedBytes">The addressable byte count actually read.
    /// <para>实际读取的可寻址字节数。</para></param>
    /// <param name="ChunkBytes">The chunk bound used for every chunk except a shorter tail.
    /// <para>除较短尾块外每块使用的上限。</para></param>
    /// <param name="Read">The family's own range read into the caller-supplied stream.
    /// <para>该家族自身的范围读取，写入调用方提供的流。</para></param>
    private sealed record MtkDumpTarget(string RegionName, long ReportedCapacityBytes, long RequestedBytes,
        int ChunkBytes, Func<MtkSession, long, int, Stream, CancellationToken, Task> Read)
    {
        /// <summary>Sizes a target from a reported capacity, refusing absent and non-positive capacities.
        /// <para>按上报容量确定目标大小，并拒绝缺失或非正的容量。</para></summary>
        /// <param name="regionName">The canonical user-region token. <para>规范用户区标记。</para></param>
        /// <param name="reportedCapacityBytes">The capacity the agent reported. <para>代理上报的容量。</para></param>
        /// <param name="alignmentBytes">The alignment the family enforces. <para>该家族强制的对齐。</para></param>
        /// <param name="chunkBytes">The planned chunk bound. <para>计划的分块上限。</para></param>
        /// <param name="read">The family's range read. <para>该家族的范围读取。</para></param>
        /// <returns>The sized target. <para>已定大小的目标。</para></returns>
        internal static MtkDumpTarget Create(string regionName, long reportedCapacityBytes, int alignmentBytes,
            int chunkBytes, Func<MtkSession, long, int, Stream, CancellationToken, Task> read)
        {
            var requested = reportedCapacityBytes - reportedCapacityBytes % alignmentBytes;
            if (requested <= 0)
            {
                throw new InvalidOperationException(
                    $"The download agent reports {reportedCapacityBytes} bytes for the {regionName} user area; " +
                    "a full dump needs a positive reported capacity aligned to the family transfer size.");
            }

            return new MtkDumpTarget(regionName, reportedCapacityBytes, requested, chunkBytes, read);
        }
    }
}

/// <summary>Describes one published user-area image.
/// <para>描述一份已发布的用户区镜像。</para></summary>
/// <param name="Family">The download agent family that served the read.
/// <para>提供本次读取的下载代理家族。</para></param>
/// <param name="RegionName">The canonical user-region or whole-device token that was read.
/// <para>被读取的规范用户区或整片设备标记。</para></param>
/// <param name="ByteLength">The published byte count. <para>已发布的字节数。</para></param>
/// <param name="Sha256Hex">The upper-case SHA-256 of the published bytes.
/// <para>已发布字节的大写 SHA-256。</para></param>
/// <param name="ChunkBytes">The chunk bound the agent was asked for per transfer.
/// <para>每次传输向代理请求的分块上限。</para></param>
/// <param name="CapacityUsedAsReported">Whether the reported capacity was read in full, or clamped down to
/// the family's transfer alignment. <para>上报容量是整体读取，还是被下调到该家族的对齐界限。</para></param>
internal sealed record MtkFullDumpRecord(MtkDaProtocol Family, string RegionName, long ByteLength,
    string Sha256Hex, int ChunkBytes, bool CapacityUsedAsReported);