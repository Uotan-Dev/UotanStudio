using System.IO.Compression;
using System.Security.Cryptography;
using UotanStudio.Firmware.Common.Streams;
using UotanStudio.Firmware.Diff;
using UotanStudio.Firmware.Payload.Core;
using UotanStudio.Firmware.Payload.Proto;
using UotanStudio.Firmware.Sparse;

namespace UotanStudio.Firmware.Dat;

/// <summary>Reconstructs Android block OTA images with bounded streams. <para>通过有界流重建 Android 块 OTA 镜像。</para></summary>
public static class DatReader
{
    /// <summary>Gets the Android transfer block size. <para>获取 Android 传输块大小。</para></summary>
    public const int BlockSize = 4096;

    /// <summary>Unpacks a full OTA image. <para>解包全量 OTA 镜像。</para></summary>
    public static void Unpack(string transferListPath, string newDataPath, string outputImagePath, IProgress<double>? progress = null)
        => Unpack(transferListPath, newDataPath, null, null, outputImagePath, false, progress);
    /// <summary>Unpacks a full OTA with optional Brotli data. <para>解包可选 Brotli 数据的全量 OTA。</para></summary>
    public static void Unpack(string transferListPath, string newDataPath, string outputImagePath, bool brotliCompressed, IProgress<double>? progress = null)
        => Unpack(transferListPath, newDataPath, null, null, outputImagePath, brotliCompressed, progress);
    /// <summary>Unpacks a full or incremental block OTA. <para>解包全量或增量块 OTA。</para></summary>
    public static void Unpack(string transferListPath, string newDataPath, string? patchDataPath, string? sourceImagePath, string outputImagePath, IProgress<double>? progress = null)
        => Unpack(transferListPath, newDataPath, patchDataPath, sourceImagePath, outputImagePath, false, progress);
    /// <summary>Unpacks an OTA and commits its output only after all commands pass. <para>解包 OTA，并在全部命令验证通过后提交输出。</para></summary>
    public static void Unpack(string transferListPath, string newDataPath, string? patchDataPath, string? sourceImagePath, string outputImagePath, bool brotliCompressed, IProgress<double>? progress = null)
    {
        var list = TransferListParser.Parse(transferListPath);
        var needsSource = list.Commands.Any(command => command.Kind is TransferListCommandKind.Move or TransferListCommandKind.Stash or TransferListCommandKind.Diff or TransferListCommandKind.Imgdiff);
        var needsPatch = list.Commands.Any(command => command.Kind is TransferListCommandKind.Diff or TransferListCommandKind.Imgdiff);
        if (needsSource && string.IsNullOrWhiteSpace(sourceImagePath)) throw new ArgumentException("An incremental OTA requires a source image.", nameof(sourceImagePath));
        if (needsPatch && string.IsNullOrWhiteSpace(patchDataPath)) throw new ArgumentException("An incremental diff requires .patch.dat.", nameof(patchDataPath));
        using var newData = OpenData(newDataPath, brotliCompressed, checked(list.NewDataBlocks * BlockSize));
        var patchLimit = list.Commands.Aggregate(0L, (limit, command) => Math.Max(limit, checked(command.PatchOffset + command.PatchLength)));
        using var patch = needsPatch ? OpenData(patchDataPath!, false, patchLimit) : null;
        var destination = Path.GetFullPath(outputImagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var source = needsSource ? File.OpenRead(sourceImagePath!) : null)
                    if (source is not null) BinaryDeltaPatch.CopyExactly(source, output, source.Length);
                output.SetLength(Math.Max(output.Length, checked(list.TotalBlocks * BlockSize)));
                Execute(list, newData, patch, output, progress);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static void Execute(TransferList list, Stream newData, Stream? patch, FileStream output, IProgress<double>? progress)
    {
        var stashes = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        long stashBlocks = 0;
        var index = 0;
        try
        {
            foreach (var command in list.Commands)
            {
                switch (command.Kind)
                {
                    case TransferListCommandKind.New:
                        using (var target = WriteRanges(output, command.Ranges))
                        { BinaryDeltaPatch.CopyExactly(newData, target, target.Length); target.Complete(); }
                        break;
                    case TransferListCommandKind.Zero:
                    case TransferListCommandKind.Erase:
                        using (var target = WriteRanges(output, command.Ranges))
                        {
                            var zeros = new byte[64 * 1024];
                            while (target.Position < target.Length) target.Write(zeros, 0, (int)Math.Min(zeros.Length, target.Length - target.Position));
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
    private static Stream OpenData(string path, bool forceBrotli, long maximumLength)
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
            using (var bounded = new CountingWriteStream(temporary, maximumLength, leaveOpen: true)) decoder.CopyTo(bounded);
            temporary.Position = 0;
            return temporary;
        }
        catch { temporary.Dispose(); file.Dispose(); throw; }
    }
}