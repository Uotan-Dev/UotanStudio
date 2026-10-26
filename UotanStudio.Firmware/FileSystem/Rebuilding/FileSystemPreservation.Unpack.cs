using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>
/// Retains source semantics and resolves edited host files into filesystem builders with original or inherited Linux attributes.
/// <para>保留源镜像语义，并把编辑后的宿主文件树解析为使用原始或继承 Linux 属性的文件系统构建器。</para>
/// </summary>
public static partial class FileSystemPreservation
{
    /// <summary>Parses a source that extraction, inspection and the retained image all observe as one version.
    /// The source file is opened with a shared reader that denies writers, so a regular file is extracted in
    /// place while the operating system holds it stable; only a non-regular source or a source that still
    /// changed falls back to a private frozen temporary snapshot.
    /// <para>以解包、检查与保留镜像都视为同一版本的方式解析源。源文件以拒绝写者的共享读句柄打开，
    /// 因此常规文件可以在操作系统保持其稳定期间就地解包；只有非常规源，或仍然发生变化的源，才回退到私有的
    /// 冻结临时快照。</para>
    /// </summary>
    /// <param name="handler">Read-only format handler whose metadata describes the source.
    /// <para>元数据描述该源镜像的只读格式处理器。</para></param>
    /// <param name="sourcePath">Source image file. A regular file is read in place under a shared reader; a
    /// non-regular source is copied byte-for-byte into a temporary snapshot and verified unchanged.
    /// <para>源镜像文件。常规文件在共享读句柄下就地读取；非常规源按字节复制到临时快照并校验未被改动。</para></param>
    /// <param name="outputDirectory">Directory that receives the extraction output.
    /// <para>接收解包输出的目录。</para></param>
    /// <param name="options">Unpack options; <c>FileSystemProgress</c> drives byte progress and <c>ErofsExtraDevicePaths</c> supplies EROFS external devices (always frozen). May be <see langword="null"/>.
    /// <para>解包选项；<c>FileSystemProgress</c> 驱动字节进度，<c>ErofsExtraDevicePaths</c> 提供 EROFS 外部设备（始终冻结）。可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels snapshot copying and extraction; cancellation propagates and the temporary snapshot directory is removed.
    /// <para>取消快照复制与解包；取消会向外传播并删除临时快照目录。</para></param>
    /// <param name="unpack">Extraction delegate invoked with the path the extraction must read: the original when
    /// it stayed stable, otherwise the frozen copy.
    /// <para>以解包必须读取的路径调用的解包委托：源保持稳定时为原始路径，否则为冻结副本。</para></param>
    /// <returns>The delegate's result with a frozen-copy path replaced by the original source path in the
    /// summary, plus a <c>SymlinkFallbacks</c> metadata entry when extraction had to materialize symlinks as
    /// text files; any non-cancellation failure becomes a fail result.
    /// <para>委托结果；冻结副本路径在摘要中替换为原始源路径，且解包不得不把符号链接物化为文本文件时附加
    /// <c>SymlinkFallbacks</c> 元数据项；除取消外的失败均转为失败结果。</para></returns>
    /// <remarks>A snapshot retry temporarily enables <see cref="UnpackOptions.Overwrite"/> so bytes written by
    /// the rejected in-place pass cannot survive, then restores the caller's value.
    /// <para>快照重试会临时开启 <see cref="UnpackOptions.Overwrite"/>，使被否定的就地解包写出的字节无法残留，
    /// 随后恢复调用方的取值。</para></remarks>
    /// <exception cref="OperationCanceledException">The token was cancelled during snapshotting or extraction.
    /// <para>快照或解包期间令牌被取消。</para></exception>
    public static async Task<FirmwareOpResult> UnpackAsync(IReadonlyFormatHandler handler, string sourcePath,
        string outputDirectory, UnpackOptions? options, CancellationToken cancellationToken,
        Func<string, Task<FirmwareOpResult>> unpack)
    {
        var context = RepackMetadataContext.Unspecified.Capture();
        var previousProgress = ActiveProgress.Value;
        var previousDevices = FrozenErofsDevices.Value;
        var previousNotes = ExtractionNotes.Value;
        var previousOutputBudget = ActiveMaximumOutputBytes.Value;
        ActiveProgress.Value = options?.FileSystemProgress;
        ActiveMaximumOutputBytes.Value = options?.MaximumOutputBytes;
        CapturedPaths.Value = new(StringComparer.Ordinal);
        var notes = new ExtractionFallbackNotes();
        ExtractionNotes.Value = notes;
        var temporary = Path.Combine(Path.GetTempPath(), "uotan-filesystem-" + Guid.NewGuid().ToString("N"));
        FileStream? sourceGuard = null;
        var rejectedInPlace = false;
        try
        {
            var external = options?.ErofsExtraDevicePaths;
            if (external is { Count: > 0 } && handler.FormatName != "EROFS")
                throw new ArgumentException("External device images are supported only for EROFS.");
            if (external is { Count: > ushort.MaxValue })
                throw new FileSystemCorruptedException("EROFS external device count exceeds the on-disk limit.");
            List<string> originals = [Path.GetFullPath(sourcePath)];
            if (external is not null)
            {
                foreach (var path in external)
                {
                    if (string.IsNullOrWhiteSpace(path))
                        throw new FileSystemCorruptedException("EROFS external device path is empty.");
                    originals.Add(Path.GetFullPath(path));
                }
            }
            if (originals.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .Count() != originals.Count)
                throw new FileSystemCorruptedException("EROFS device images must be distinct.");

            var preparationTotal = 0L;
            foreach (var original in originals)
            {
                preparationTotal = checked(preparationTotal + new FileInfo(original).Length);
            }

            var preparation = new ExtractionByteCounter(preparationTotal,
                FileSystemByteProgress.ForStage(ActiveProgress.Value, FileSystemWorkStage.Preparing),
                cancellationToken);
            sourceGuard = TryOpenSourceGuard(sourcePath, out var sourceLength, out var sourceTime);
            if (sourceGuard is not null)
            {
                preparation.Add(sourceLength);
                if (external is { Count: > 0 })
                {
                    Directory.CreateDirectory(temporary);
                    await SnapshotAsync(originals, startIndex: 1, temporary, preparation, cancellationToken)
                        .ConfigureAwait(false);
                    FrozenErofsDevices.Value = FrozenDevicePaths(temporary, originals.Count);
                }
                else
                {
                    FrozenErofsDevices.Value = null;
                }

                preparation.Complete();
                using var scope = RepackMetadata.UseContext(context);
                var inPlace = await RepackMetadata.UnpackAsync(handler, sourcePath, outputDirectory, options,
                    cancellationToken, () => RunExtractionAsync(unpack, sourcePath)).ConfigureAwait(false);
                if (SourceStillMatches(sourcePath, sourceGuard, sourceLength, sourceTime))
                {
                    return FinalizeResult(inPlace, sourcePath, sourcePath, notes);
                }

                sourceGuard.Dispose();
                sourceGuard = null;
                rejectedInPlace = true;
                if (Directory.Exists(temporary))
                {
                    Directory.Delete(temporary, recursive: true);
                }
            }

            if (rejectedInPlace)
            {
                preparation.Add(-sourceLength);
            }

            Directory.CreateDirectory(temporary);
            await SnapshotAsync(originals, startIndex: 0, temporary, preparation, cancellationToken)
                .ConfigureAwait(false);
            preparation.Complete();
            var frozen = Path.Combine(temporary, Path.GetFileName(sourcePath));
            FrozenErofsDevices.Value = external is null ? null : FrozenDevicePaths(temporary, originals.Count);
            var previousOverwrite = options?.Overwrite;
            if (rejectedInPlace && options is { Overwrite: false })
            {
                options.Overwrite = true;
            }

            try
            {
                using var snapshotScope = RepackMetadata.UseContext(context);
                var snapshot = await RepackMetadata.UnpackAsync(handler, frozen, outputDirectory, options,
                    cancellationToken, () => RunExtractionAsync(unpack, frozen)).ConfigureAwait(false);
                return FinalizeResult(snapshot, frozen, sourcePath, notes);
            }
            finally
            {
                if (rejectedInPlace && options is not null && previousOverwrite is { } overwrite)
                {
                    options.Overwrite = overwrite;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return FirmwareOpResult.Fail(ex.Message); }
        finally
        {
            sourceGuard?.Dispose();
            ActiveProgress.Value = previousProgress;
            FrozenErofsDevices.Value = previousDevices;
            ExtractionNotes.Value = previousNotes;
            ActiveMaximumOutputBytes.Value = previousOutputBudget;
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    /// <summary>
    /// Runs the caller's extraction delegate and marks the transition to metadata reading, keeping both
    /// phases identical regardless of which path the source is read from.
    /// <para>运行调用方的解包委托并标记转入元数据读取，使两种源读取路径下这两个阶段完全一致。</para>
    /// </summary>
    private static async Task<FirmwareOpResult> RunExtractionAsync(Func<string, Task<FirmwareOpResult>> unpack,
        string path)
    {
        var extracted = await unpack(path).ConfigureAwait(false);
        ActiveProgress.Value?.Report(new(FileSystemWorkStage.ReadingMetadata, 0, 0));
        return extracted;
    }

    /// <summary>
    /// Opens the shared reader that pins a regular source file for the duration of extraction. The handle
    /// denies writers and deleters, so the bytes cannot change underneath the in-place extraction; a source
    /// that is not a regular file, or cannot be opened this way, reports <see langword="null"/> and takes the
    /// frozen-snapshot path instead.
    /// <para>打开在解包期间钉住常规源文件的共享读句柄。该句柄拒绝写入者与删除者，因此就地解包期间字节不会改变；
    /// 非常规源，或无法以此方式打开的源，返回 <see langword="null"/> 并改走冻结快照路径。</para>
    /// </summary>
    /// <param name="path">Source path to pin. <para>待钉住的源路径。</para></param>
    /// <param name="length">Source length read through the pinned handle. <para>经钉住句柄读取的源长度。</para></param>
    /// <param name="lastWriteUtc">Source last-write time observed before extraction.
    /// <para>解包前观测到的源最后写入时间。</para></param>
    /// <returns>The pinned handle, or <see langword="null"/> when a snapshot is required.
    /// <para>钉住的句柄；需要快照时为 <see langword="null"/>。</para></returns>
    private static FileStream? TryOpenSourceGuard(string path, out long length, out DateTime lastWriteUtc)
    {
        length = 0;
        lastWriteUtc = default;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
            {
                return null;
            }

            var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.SequentialScan);
            try
            {
                length = guard.Length;
                lastWriteUtc = info.LastWriteTimeUtc;
                if (info.Length != length)
                {
                    guard.Dispose();
                    return null;
                }

                return guard;
            }
            catch
            {
                guard.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Re-reads the source after extraction and reports whether it is still the version the pinned handle was
    /// opened on. A changed length or modification time means the in-place pass mixed two versions, so the
    /// caller discards it and repeats the extraction from a frozen copy.
    /// <para>解包后重读源，报告它是否仍是钉住句柄打开时的版本。长度或修改时间发生变化意味着就地解包混用了两个
    /// 版本，调用方会丢弃该结果并从冻结副本重新解包。</para>
    /// </summary>
    /// <param name="path">Source path to re-check. <para>待复查的源路径。</para></param>
    /// <param name="guard">Handle held for the whole extraction. <para>解包全程持有的句柄。</para></param>
    /// <param name="length">Length observed before extraction. <para>解包前观测到的长度。</para></param>
    /// <param name="lastWriteUtc">Last-write time observed before extraction.
    /// <para>解包前观测到的最后写入时间。</para></param>
    /// <returns><see langword="true"/> when the source still matches.
    /// <para>源仍然匹配时为 <see langword="true"/>。</para></returns>
    private static bool SourceStillMatches(string path, FileStream guard, long length, DateTime lastWriteUtc)
    {
        try
        {
            if (guard.Length != length)
            {
                return false;
            }

            var info = new FileInfo(path);
            info.Refresh();
            return info.Exists && info.Length == length && info.LastWriteTimeUtc == lastWriteUtc;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies every file from <paramref name="startIndex"/> onwards into <paramref name="temporary"/> with the
    /// historical length/time verification, charging the bytes to the shared preparation counter. The caller
    /// charges the pinned source itself when it takes the in-place path, so the preparation stage always
    /// reports the full source size instead of zero.
    /// <para>把 <paramref name="startIndex"/> 起的每个文件按既有的长度/时间校验复制到
    /// <paramref name="temporary"/>，并把这批字节计入共享的准备计数器。调用方走就地路径时会自行计入被钉住的源，
    /// 因此准备阶段始终上报完整源大小而不是零。</para>
    /// </summary>
    /// <param name="originals">Absolute source paths; index zero is the primary image.
    /// <para>绝对源路径；索引零为主镜像。</para></param>
    /// <param name="startIndex">First index to copy; <c>1</c> freezes only the external devices.
    /// <para>开始复制的索引；为 <c>1</c> 时只冻结外部设备。</para></param>
    /// <param name="temporary">Directory receiving the frozen copies. <para>接收冻结副本的目录。</para></param>
    /// <param name="bytes">Shared preparation counter that also receives the pinned source bytes on the in-place
    /// path. <para>共享准备计数器；就地路径下还接收被钉住源的字节。</para></param>
    /// <param name="cancellationToken">Cancels the copies. <para>取消复制。</para></param>
    /// <exception cref="IOException">A source changed while it was being copied.
    /// <para>某个源在复制过程中发生变化。</para></exception>
    private static async Task SnapshotAsync(IReadOnlyList<string> originals, int startIndex, string temporary,
        ExtractionByteCounter bytes, CancellationToken cancellationToken)
    {
        for (var i = startIndex; i < originals.Count; i++)
        {
            var original = originals[i];
            var target = i == 0
                ? Path.Combine(temporary, Path.GetFileName(original))
                : Path.Combine(temporary, $"device-{i}.img");
            var info = new FileInfo(original);
            var length = info.Length;
            var time = info.LastWriteTimeUtc;
            await using (var input = File.OpenRead(original))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, true))
            {
                using var counted = bytes.Wrap(output);
                await StreamCopy.CopyExactlyAsync(input, counted, length, cancellationToken).ConfigureAwait(false);
                if (input.Length != length)
                {
                    throw new IOException("EROFS device changed during snapshot preparation.");
                }
            }

            info.Refresh();
            if (info.Length != length || info.LastWriteTimeUtc != time)
            {
                throw new IOException("EROFS device changed during snapshot preparation.");
            }
        }

        bytes.Complete();
    }

    /// <summary>
    /// Builds the frozen external-device path list in device-table order.
    /// <para>按设备表顺序构建冻结外部设备的路径列表。</para>
    /// </summary>
    private static List<string> FrozenDevicePaths(string temporary, int originalCount)
    {
        var frozen = new List<string>(Math.Max(0, originalCount - 1));
        for (var i = 1; i < originalCount; i++)
        {
            frozen.Add(Path.Combine(temporary, $"device-{i}.img"));
        }

        return frozen;
    }

    /// <summary>
    /// Rewrites the frozen path in the summary back to the original source path and merges the extraction
    /// notes into the metadata, leaving every other result field untouched.
    /// <para>把摘要中的冻结路径改写回原始源路径，并把解包备注合并进元数据，其余结果字段保持不变。</para>
    /// </summary>
    private static FirmwareOpResult FinalizeResult(FirmwareOpResult result, string frozenPath, string sourcePath,
        ExtractionFallbackNotes notes)
    {
        var metadata = result.Metadata;
        if (notes.SymlinkFallbacks > 0)
        {
            var merged = new Dictionary<string, string>(metadata.Count + 1, StringComparer.Ordinal);
            foreach (var pair in metadata)
            {
                merged[pair.Key] = pair.Value;
            }

            merged["SymlinkFallbacks"] =
                notes.SymlinkFallbacks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            metadata = merged;
        }

        return new FirmwareOpResult
        {
            Success = result.Success,
            Summary = result.Summary.Replace(frozenPath, sourcePath, StringComparison.Ordinal),
            Entries = result.Entries,
            Metadata = metadata,
            Components = result.Components
        };
    }

    /// <summary>
    /// Counters an extraction records for warnings the caller must see, such as symlinks that could only be
    /// materialized as text files. The instance is ambient so a nested parser does not need a result handle.
    /// <para>解包为调用方必须看到的警告（例如只能用文本文件物化的符号链接）记录的计数器。实例是环境值，
    /// 使嵌套的解析器无需结果句柄即可上报。</para>
    /// </summary>
    private sealed class ExtractionFallbackNotes
    {
        internal int SymlinkFallbacks;
    }

    private static readonly AsyncLocal<ExtractionFallbackNotes?> ExtractionNotes = new();
    private static readonly AsyncLocal<long?> ActiveMaximumOutputBytes = new();
    private static readonly long DefaultMaximumOutputBytes = new UnpackOptions().MaximumOutputBytes;

    /// <summary>Budget a parser created on the current unpack flow must apply to one logical payload.
    /// <para>当前解包流上创建的解析器必须应用到单个逻辑负载的预算。</para>
    /// </summary>
    /// <value>A positive byte ceiling; a non-positive caller value falls back to the option default so the
    /// bound can never be disabled by a zero budget.
    /// <para>正的字节上限；调用方给出非正值时回落到选项默认值，使该上限不会因零预算而失效。</para></value>
    internal static long RequestedMaximumOutputBytes =>
        ActiveMaximumOutputBytes.Value is { } requested && requested > 0 ? requested : DefaultMaximumOutputBytes;

    /// <summary>Records that one symlink entry had to be materialized as a text file by the active unpack.
    /// <para>记录活动解包中有一个符号链接条目不得不物化为文本文件。</para>
    /// </summary>
    internal static void RecordSymlinkFallback()
    {
        if (ExtractionNotes.Value is { } notes)
        {
            Interlocked.Increment(ref notes.SymlinkFallbacks);
        }
    }
}