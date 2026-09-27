using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat.Models;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.Bsdiff;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.ImgDiff;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.IO;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.IO;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat.Reader;

/// <summary>Reconstructs Android block OTA images with bounded streams. <para>通过有界流重建 Android 块 OTA 镜像。</para></summary>
public static class DatReader
{
    /// <summary>Gets the Android transfer block size. <para>获取 Android 传输块大小。</para></summary>
    public const int BlockSize = 4096;

    /// <summary>Unpacks a full OTA image. <para>解包全量 OTA 镜像。</para></summary>
    /// <param name="transferListPath">Path to the <c>.transfer.list</c> command file. <para>指向 <c>.transfer.list</c> 命令文件的路径。</para></param>
    /// <param name="newDataPath">Path to the raw new data stream. <para>指向原始新数据流的路径。</para></param>
    /// <param name="outputImagePath">Destination image path written atomically. <para>以原子方式写入的目标镜像路径。</para></param>
    /// <param name="progress">Receives completion fractions in <c>0</c>..&lt;<c>1</c>&gt;, or <see langword="null"/> to omit reporting. <para>接收 <c>0</c>..&lt;<c>1</c>&gt; 的完成进度；为 <see langword="null"/> 时不报告。</para></param>
    public static void Unpack(string transferListPath, string newDataPath, string outputImagePath, IProgress<double>? progress = null)
        => Unpack(transferListPath, newDataPath, null, null, outputImagePath, false, progress);
    /// <summary>Unpacks a full OTA with optional Brotli data. <para>解包可选 Brotli 数据的全量 OTA。</para></summary>
    /// <param name="transferListPath">Path to the <c>.transfer.list</c> command file. <para>指向 <c>.transfer.list</c> 命令文件的路径。</para></param>
    /// <param name="newDataPath">Path to the new data stream, Brotli-compressed when <paramref name="brotliCompressed"/> is set. <para>指向新数据流的路径；当 <paramref name="brotliCompressed"/> 置位时为 Brotli 压缩。</para></param>
    /// <param name="outputImagePath">Destination image path written atomically. <para>以原子方式写入的目标镜像路径。</para></param>
    /// <param name="brotliCompressed">Forces Brotli decoding of <paramref name="newDataPath"/> regardless of its extension. <para>无论扩展名如何，都强制对 <paramref name="newDataPath"/> 进行 Brotli 解码。</para></param>
    /// <param name="progress">Receives completion fractions in <c>0</c>..&lt;<c>1</c>&gt;, or <see langword="null"/> to omit reporting. <para>接收 <c>0</c>..&lt;<c>1</c>&gt; 的完成进度；为 <see langword="null"/> 时不报告。</para></param>
    public static void Unpack(string transferListPath, string newDataPath, string outputImagePath, bool brotliCompressed, IProgress<double>? progress = null)
        => Unpack(transferListPath, newDataPath, null, null, outputImagePath, brotliCompressed, progress);
    /// <summary>Unpacks a full or incremental block OTA. <para>解包全量或增量块 OTA。</para></summary>
    /// <param name="transferListPath">Path to the <c>.transfer.list</c> command file. <para>指向 <c>.transfer.list</c> 命令文件的路径。</para></param>
    /// <param name="newDataPath">Path to the raw new data stream. <para>指向原始新数据流的路径。</para></param>
    /// <param name="patchDataPath">Path to <c>.patch.dat</c> for diff/imgdiff commands, or <see langword="null"/> for a full OTA. <para>diff/imgdiff 命令对应的 <c>.patch.dat</c> 路径；全量 OTA 时为 <see langword="null"/>。</para></param>
    /// <param name="sourceImagePath">Path to the source image required by incremental commands, or <see langword="null"/> for a full OTA. <para>增量命令所需源镜像的路径；全量 OTA 时为 <see langword="null"/>。</para></param>
    /// <param name="outputImagePath">Destination image path written atomically. <para>以原子方式写入的目标镜像路径。</para></param>
    /// <param name="progress">Receives completion fractions in <c>0</c>..&lt;<c>1</c>&gt;, or <see langword="null"/> to omit reporting. <para>接收 <c>0</c>..&lt;<c>1</c>&gt; 的完成进度；为 <see langword="null"/> 时不报告。</para></param>
    public static void Unpack(string transferListPath, string newDataPath, string? patchDataPath, string? sourceImagePath, string outputImagePath, IProgress<double>? progress = null)
        => Unpack(transferListPath, newDataPath, patchDataPath, sourceImagePath, outputImagePath, false, progress);
    /// <summary>Unpacks an OTA and commits its output only after all commands pass. <para>解包 OTA，并在全部命令验证通过后提交输出。</para></summary>
    /// <param name="transferListPath">Path to the <c>.transfer.list</c> command file. <para>指向 <c>.transfer.list</c> 命令文件的路径。</para></param>
    /// <param name="newDataPath">Path to the new data stream, Brotli-compressed when <paramref name="brotliCompressed"/> is set. <para>指向新数据流的路径；当 <paramref name="brotliCompressed"/> 置位时为 Brotli 压缩。</para></param>
    /// <param name="patchDataPath">Path to <c>.patch.dat</c> for diff/imgdiff commands, or <see langword="null"/> when none apply. <para>diff/imgdiff 命令对应的 <c>.patch.dat</c> 路径；无此类命令时为 <see langword="null"/>。</para></param>
    /// <param name="sourceImagePath">Path to the source image required by move/stash/diff/imgdiff commands, or <see langword="null"/>. <para>move/stash/diff/imgdiff 命令所需源镜像的路径；可为 <see langword="null"/>。</para></param>
    /// <param name="outputImagePath">Destination image path; the existing file is replaced atomically only on success. <para>目标镜像路径；仅在成功时原子替换现有文件。</para></param>
    /// <param name="brotliCompressed">Forces Brotli decoding of <paramref name="newDataPath"/> regardless of its extension. <para>无论扩展名如何，都强制对 <paramref name="newDataPath"/> 进行 Brotli 解码。</para></param>
    /// <param name="progress">Receives completion fractions in <c>0</c>..&lt;<c>1</c>&gt;, or <see langword="null"/> to omit reporting. <para>接收 <c>0</c>..&lt;<c>1</c>&gt; 的完成进度；为 <see langword="null"/> 时不报告。</para></param>
    /// <exception cref="ArgumentException">The transfer list needs a source image or patch data that was not supplied. <para>传输清单需要源镜像或补丁数据，但未提供。</para></exception>
    /// <exception cref="InvalidDataException">A command's ranges, stash, hash or patch output fails validation. <para>某个命令的范围、暂存、哈希或补丁输出未通过校验。</para></exception>
    public static void Unpack(string transferListPath, string newDataPath, string? patchDataPath, string? sourceImagePath, string outputImagePath, bool brotliCompressed, IProgress<double>? progress = null)
        => Unpack(transferListPath, newDataPath, patchDataPath, sourceImagePath, outputImagePath, brotliCompressed, progress, CancellationToken.None);

    /// <summary>Unpacks an OTA with cancellation and atomic output publication. <para>支持取消及输出原子发布地解包 OTA。</para></summary>
    /// <param name="transferListPath">Path to the transfer list. <para>传输清单的路径。</para></param>
    /// <param name="newDataPath">Path to the new data stream. <para>新数据流的路径。</para></param>
    /// <param name="patchDataPath">Optional incremental patch data path. <para>可选的增量补丁数据路径。</para></param>
    /// <param name="sourceImagePath">Optional incremental source image path. <para>可选的增量源镜像路径。</para></param>
    /// <param name="outputImagePath">Destination image path. <para>目标镜像路径。</para></param>
    /// <param name="brotliCompressed">Forces Brotli decoding of new data. <para>强制对新数据进行 Brotli 解码。</para></param>
    /// <param name="progress">Optional completion fraction sink. <para>可选的完成进度比例接收器。</para></param>
    /// <param name="cancellationToken">Cancels copying before the output is published. <para>在输出发布前取消复制。</para></param>
    /// <exception cref="ArgumentException">The transfer list needs a source image or patch data that was not supplied. <para>传输清单需要源镜像或补丁数据，但未提供。</para></exception>
    /// <exception cref="InvalidDataException">A command's ranges, stash, hash or patch output fails validation. <para>某个命令的范围、暂存、哈希或补丁输出未通过校验。</para></exception>
    public static void Unpack(string transferListPath, string newDataPath, string? patchDataPath, string? sourceImagePath, string outputImagePath, bool brotliCompressed, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = TransferListParser.Parse(transferListPath);
        var needsSource = list.Commands.Any(command => command.Kind is TransferListCommandKind.Move or TransferListCommandKind.Stash or TransferListCommandKind.Diff or TransferListCommandKind.Imgdiff);
        var needsPatch = list.Commands.Any(command => command.Kind is TransferListCommandKind.Diff or TransferListCommandKind.Imgdiff);
        if (needsSource && string.IsNullOrWhiteSpace(sourceImagePath)) throw new ArgumentException("An incremental OTA requires a source image.", nameof(sourceImagePath));
        if (needsPatch && string.IsNullOrWhiteSpace(patchDataPath)) throw new ArgumentException("An incremental diff requires .patch.dat.", nameof(patchDataPath));
        using var newData = OpenData(newDataPath, brotliCompressed, checked(list.NewDataBlocks * BlockSize), cancellationToken);
        var patchLimit = list.Commands.Aggregate(0L, (limit, command) => Math.Max(limit, checked(command.PatchOffset + command.PatchLength)));
        using var patch = needsPatch ? OpenData(patchDataPath!, false, patchLimit, cancellationToken) : null;
        var destination = Path.GetFullPath(outputImagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var source = needsSource ? File.OpenRead(sourceImagePath!) : null)
                    if (source is not null) BinaryDeltaPatch.CopyExactly(source, output, source.Length, cancellationToken);
                output.SetLength(Math.Max(output.Length, checked(list.TotalBlocks * BlockSize)));
                Execute(list, newData, patch, output, progress, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static void Execute(TransferList list, Stream newData, Stream? patch, FileStream output, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var stashes = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        long stashBlocks = 0;
        var index = 0;
        try
        {
            foreach (var command in list.Commands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (command.Kind)
                {
                    case TransferListCommandKind.New:
                        using (var target = WriteRanges(output, command.Ranges))
                        {
                            BinaryDeltaPatch.CopyExactly(newData, target, target.Length, cancellationToken);
                            target.Complete();
                        }
                        break;
                    case TransferListCommandKind.Zero:
                    case TransferListCommandKind.Erase:
                        using (var target = WriteRanges(output, command.Ranges))
                        {
                            var zeros = new byte[64 * 1024];
                            while (target.Position < target.Length)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                target.Write(zeros, 0, (int)Math.Min(zeros.Length, target.Length - target.Position));
                            }
                            target.Complete();
                        }
                        break;
                    case TransferListCommandKind.Stash:
                        {
                            using var ranges = ReadRanges(output, command.Ranges);
                            var id = command.StashId!;
                            if (stashes.ContainsKey(id)) throw new InvalidDataException("Duplicate live stash identifier.");
                            var nextStashBlocks = checked(stashBlocks + ranges.Length / BlockSize);
                            if (nextStashBlocks > list.MaxStashedBlocks) throw new InvalidDataException("Stash block limit exceeded.");
                            var saved = BinaryDeltaPatch.CreateTemporaryStream();
                            try
                            {
                                BinaryDeltaPatch.CopyExactly(ranges, saved, ranges.Length);
                                if (list.Version >= 3) VerifyHash(saved, id);
                                stashes.Add(id, saved); stashBlocks = nextStashBlocks;
                            }
                            catch { saved.Dispose(); throw; }
                            break;
                        }
                    case TransferListCommandKind.Free:
                        if (!stashes.Remove(command.StashId!, out var released)) throw new InvalidDataException("Unknown stash identifier.");
                        stashBlocks -= released.Length / BlockSize; released.Dispose();
                        break;
                    case TransferListCommandKind.Move:
                    case TransferListCommandKind.Diff:
                    case TransferListCommandKind.Imgdiff:
                        {
                            using var assembled = AssembleSource(command, output, stashes);
                            VerifyHash(assembled, command.SourceHash);
                            using var result = BinaryDeltaPatch.CreateTemporaryStream();
                            var expected = checked(command.Ranges.Sum(range => range.BlockCount) * BlockSize);
                            if (command.Kind == TransferListCommandKind.Move)
                            {
                                if (assembled.Length != expected) throw new InvalidDataException("MOVE source and target lengths differ.");
                                BinaryDeltaPatch.CopyExactly(assembled, result, expected);
                            }
                            else
                            {
                                using var patchRange = new BoundedReadStream(patch!, command.PatchOffset, command.PatchLength, leaveOpen: true);
                                if (command.Kind == TransferListCommandKind.Imgdiff)
                                    ImageDeltaPatch.Apply(assembled, patchRange, result, expected);
                                else BinaryDeltaPatch.Apply(assembled, patchRange, result, expected);
                                if (result.Length != expected) throw new InvalidDataException("Patch output does not match target ranges.");
                            }
                            VerifyHash(result, command.TargetHash);
                            result.Position = 0;
                            using var target = WriteRanges(output, command.Ranges);
                            BinaryDeltaPatch.CopyExactly(result, target, expected); target.Complete();
                            break;
                        }
                }
                progress?.Report((double)++index / list.Commands.Count);
            }
            if (newData.ReadByte() != -1) throw new InvalidDataException("Unexpected trailing new.dat data.");
        }
        finally { foreach (var stash in stashes.Values) stash.Dispose(); }
    }

    private static FileStream AssembleSource(TransferListCommand command, Stream image, Dictionary<string, FileStream> stashes)
    {
        var assembled = BinaryDeltaPatch.CreateTemporaryStream();
        try
        {
            assembled.SetLength(checked(command.SourceBlockCount * BlockSize));
            using (var disk = ReadRanges(image, command.SourceRanges))
            using (var target = WriteRanges(assembled, command.SourceLocations))
            { BinaryDeltaPatch.CopyExactly(disk, target, disk.Length); target.Complete(); }
            foreach (var (id, locations) in command.StashReferences)
            {
                if (!stashes.TryGetValue(id, out var stash)) throw new InvalidDataException($"Missing stash: {id}");
                stash.Position = 0;
                using var target = WriteRanges(assembled, locations);
                BinaryDeltaPatch.CopyExactly(stash, target, stash.Length); target.Complete();
            }
            assembled.Position = 0;
            return assembled;
        }
        catch { assembled.Dispose(); throw; }
    }

    private static IEnumerable<Extent> Extents(IEnumerable<BlockRange> ranges) => ranges.Select(range => new Extent
    { StartBlock = checked((ulong)range.Start), NumBlocks = checked((ulong)range.BlockCount) });
    private static ExtentReadStream ReadRanges(Stream source, IEnumerable<BlockRange> ranges) => new(source, Extents(ranges), BlockSize);
    private static ExtentWriteStream WriteRanges(Stream output, IEnumerable<BlockRange> ranges) => new(output, Extents(ranges), BlockSize, requireExactLength: true);
    private static void VerifyHash(Stream stream, string? expected)
    {
        stream.Position = 0;
        if (expected is not null && (expected.Length != 40 || !expected.All(Uri.IsHexDigit) ||
            !CryptographicOperations.FixedTimeEquals(SHA1.HashData(stream), Convert.FromHexString(expected))))
            throw new InvalidDataException("Transfer command SHA-1 mismatch.");
        stream.Position = 0;
    }
    private static Stream OpenData(string path, bool forceBrotli, long maximumLength, CancellationToken cancellationToken)
    {
        var file = File.OpenRead(path);
        if (!forceBrotli && !path.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
        {
            Span<byte> magic = stackalloc byte[4];
            var isSparse = file.Read(magic) == 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(magic) == SparseFormat.SparseHeaderMagic;
            file.Position = 0;
            if (!isSparse) return file;
            file.Dispose();
            var logical = new SparseReadStream(SparseReader.FromImageFile(path, validateCrc: true));
            if (logical.Length > maximumLength) { logical.Dispose(); throw new InvalidDataException("Sparse DAT data exceeds the declared output length."); }
            return logical;
        }
        var temporary = BinaryDeltaPatch.CreateTemporaryStream();
        try
        {
            using (file)
            using (var decoder = new StrictBrotliReadStream(file))
            using (var bounded = new CountingWriteStream(temporary, maximumLength, leaveOpen: true))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = decoder.Read(buffer);
                    if (read == 0)
                    {
                        break;
                    }

                    bounded.Write(buffer, 0, read);
                }
            }
            temporary.Position = 0;
            return temporary;
        }
        catch { temporary.Dispose(); file.Dispose(); throw; }
    }

}