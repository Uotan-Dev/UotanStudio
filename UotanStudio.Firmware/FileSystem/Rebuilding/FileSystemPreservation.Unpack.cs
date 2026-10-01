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
    /// <summary>
    /// Parses a private frozen source so extraction, inspection and the retained image have identical input bytes.
    /// <para>对源镜像做私有冻结副本解析，确保解包、检查与保留镜像使用完全一致的输入字节。</para>
    /// </summary>
    /// <param name="handler">Read-only format handler whose metadata describes the source.
    /// <para>元数据描述该源镜像的只读格式处理器。</para></param>
    /// <param name="sourcePath">Source image file; copied byte-for-byte into a temporary snapshot and verified unchanged.
    /// <para>源镜像文件；按字节复制到临时快照并校验未被改动。</para></param>
    /// <param name="outputDirectory">Directory that receives the extraction output. <para>接收解包输出的目录。</para></param>
    /// <param name="options">Unpack options; <c>FileSystemProgress</c> drives byte progress and <c>ErofsExtraDevicePaths</c>
    /// supplies EROFS external devices (frozen alongside the source). May be <see langword="null"/>.
    /// <para>解包选项；<c>FileSystemProgress</c> 驱动字节进度，<c>ErofsExtraDevicePaths</c> 提供 EROFS 外部设备
    /// （与源镜像一同冻结）。可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels snapshot copying and extraction; cancellation propagates and the
    /// temporary snapshot directory is removed. <para>取消快照复制与解包；取消会向外传播并删除临时快照目录。</para></param>
    /// <param name="unpack">Extraction delegate invoked with the frozen copy path instead of the original.
    /// <para>以冻结副本路径而非原始路径调用的解包委托。</para></param>
    /// <returns>The delegate's result with the temporary snapshot path replaced by the original source path in the
    /// summary; any non-cancellation failure becomes a fail result.
    /// <para>委托结果，其摘要中的临时快照路径会被替换为原始源路径；除取消外的失败均转为失败结果。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled during snapshotting or extraction.
    /// <para>快照或解包期间令牌被取消。</para></exception>
    public static async Task<FirmwareOpResult> UnpackAsync(IReadonlyFormatHandler handler, string sourcePath,
        string outputDirectory, UnpackOptions? options, CancellationToken cancellationToken,
        Func<string, Task<FirmwareOpResult>> unpack)
    {
        var context = RepackMetadataContext.Unspecified.Capture();
        var previousProgress = ActiveProgress.Value;
        var previousDevices = FrozenErofsDevices.Value;
        ActiveProgress.Value = options?.FileSystemProgress;
        CapturedPaths.Value = new(StringComparer.Ordinal);
        var temporary = Path.Combine(Path.GetTempPath(), "uotan-filesystem-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            var frozen = Path.Combine(temporary, Path.GetFileName(sourcePath));
            var before = new FileInfo(sourcePath);
            var sourceLength = before.Length;
            var sourceTime = before.LastWriteTimeUtc;
            var external = options?.ErofsExtraDevicePaths;
            if (external is { Count: > 0 } && handler.FormatName != "EROFS")
                throw new ArgumentException("External device images are supported only for EROFS.");
            if (external is { Count: > ushort.MaxValue })
                throw new FileSystemCorruptedException("EROFS external device count exceeds the on-disk limit.");
            List<string> originals = [Path.GetFullPath(sourcePath)];
            var frozenDevices = new List<string>();
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
            var total = originals.Aggregate(0L, (sum, path) => checked(sum + new FileInfo(path).Length));
            var bytes = new ExtractionByteCounter(total,
                FileSystemByteProgress.ForStage(ActiveProgress.Value, FileSystemWorkStage.Preparing), cancellationToken);
            for (var i = 0; i < originals.Count; i++)
            {
                var original = originals[i];
                var target = i == 0 ? frozen : Path.Combine(temporary, $"device-{i}.img");
                var info = new FileInfo(original);
                var length = info.Length;
                var time = info.LastWriteTimeUtc;
                await using (var input = File.OpenRead(original))
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    using var counted = bytes.Wrap(output);
                    await StreamCopy.CopyExactlyAsync(input, counted, length, cancellationToken).ConfigureAwait(false);
                    if (input.Length != length)
                        throw new IOException("EROFS device changed during snapshot preparation.");
                }
                info.Refresh();
                if (info.Length != length || info.LastWriteTimeUtc != time)
                    throw new IOException("EROFS device changed during snapshot preparation.");
                if (i != 0) frozenDevices.Add(target);
            }
            bytes.Complete();
            FrozenErofsDevices.Value = external is null ? null : frozenDevices;
            var after = new FileInfo(sourcePath);
            if (after.Length != sourceLength || after.LastWriteTimeUtc != sourceTime)
                throw new IOException("Source image changed while preparing its private snapshot.");
            using var scope = RepackMetadata.UseContext(context);
            var result = await RepackMetadata.UnpackAsync(handler, frozen, outputDirectory, options, cancellationToken,
                async () =>
                {
                    var extracted = await unpack(frozen).ConfigureAwait(false);
                    ActiveProgress.Value?.Report(new(FileSystemWorkStage.ReadingMetadata, 0, 0));
                    return extracted;
                }).ConfigureAwait(false);
            return new FirmwareOpResult
            {
                Success = result.Success,
                Summary = result.Summary.Replace(frozen, sourcePath, StringComparison.Ordinal),
                Entries = result.Entries,
                Metadata = result.Metadata,
                Components = result.Components
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return FirmwareOpResult.Fail(ex.Message); }
        finally
        {
            ActiveProgress.Value = previousProgress;
            FrozenErofsDevices.Value = previousDevices;
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }
}