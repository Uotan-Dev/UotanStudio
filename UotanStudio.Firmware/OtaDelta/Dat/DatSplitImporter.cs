using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using UotanStudio.Firmware.Compression;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Firmware.OtaDelta.Diff;

namespace UotanStudio.Firmware.OtaDelta.Dat;

/// <summary>
/// Identifies the artifact produced by a numbered DAT or TWRP backup set.
/// <para>标识编号 DAT 或 TWRP 备份卷集合生成的工件类型。</para>
/// </summary>
public enum DatSplitKind
{
    /// <summary>A block-OTA new-data stream. <para>块 OTA 新数据流。</para></summary>
    NewDat,
    /// <summary>A Brotli-compressed block-OTA new-data stream. <para>Brotli 压缩的块 OTA 新数据流。</para></summary>
    NewDatBrotli,
    /// <summary>A block-OTA patch-data stream. <para>块 OTA 补丁数据流。</para></summary>
    PatchDat,
    /// <summary>A Brotli-compressed block-OTA patch-data stream. <para>Brotli 压缩的块 OTA 补丁数据流。</para></summary>
    PatchDatBrotli,
    /// <summary>Independent TWRP TAR volumes. <para>各自完整的 TWRP TAR 卷。</para></summary>
    TwrpTar,
    /// <summary>Numbered TWRP image pieces, stored raw or per-volume gzip/LZ4-legacy/zstd streams. <para>带编号的 TWRP 镜像片段，原始存储或每卷独立的 gzip/LZ4 传统/zstd 流。</para></summary>
    TwrpImage
}

/// <summary>
/// A validated, ordered set of numbered input files.
/// <para>已校验并排好顺序的编号输入文件集合。</para>
/// </summary>
/// <param name="Kind">Output artifact kind. <para>输出工件类型。</para></param>
/// <param name="Parts">Input paths in import order. <para>按导入顺序排列的输入路径。</para></param>
/// <param name="TotalInputBytes">Sum of physical input lengths. <para>输入卷的物理长度总和。</para></param>
/// <param name="CompanionTransferList">Same-stem transfer list for DAT, when present. <para>存在时与 DAT 同词干的传输清单。</para></param>
public sealed record DatSplitSet(
    DatSplitKind Kind,
    IReadOnlyList<string> Parts,
    long TotalInputBytes,
    string? CompanionTransferList);

/// <summary>
/// Groups numbered DAT streams and TWRP backup volumes and imports them with bounded streaming.
/// <para>按编号归组 DAT 流和 TWRP 备份卷，并以有界流导入。</para>
/// </summary>
public static class DatSplitImporter
{
    private const int BufferSize = 64 * 1024;
    private const int MaximumParts = 4096;
    private const long MaximumBytes = 1L << 40;

    /// <summary>
    /// Resolves all sibling volumes sharing the selected file's exact prefix, validates numbering and classifies the format.
    /// <para>解析与所选文件共享准确前缀的全部同级卷，校验编号并判定格式。</para>
    /// </summary>
    /// <param name="selectedPath">Any numbered member of the set. <para>集合中任意带编号的成员。</para></param>
    /// <returns>An ordered set with its DAT transfer list when present. <para>带有可用 DAT 传输清单的有序集合。</para></returns>
    /// <exception cref="InvalidDataException">The name, numbering, size or format is invalid. <para>名称、编号、长度或格式无效。</para></exception>
    public static DatSplitSet Inspect(string selectedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        var selected = Path.GetFullPath(selectedPath);
        var name = Path.GetFileName(selected);
        var isTwrp = TryTwrpName(name, out var prefix, out var numberText);
        if (!isTwrp && !TryDatName(name, out prefix, out numberText))
        {
            throw new InvalidDataException("Expected a numbered .new.dat[.br], .patch.dat or .win backup volume.");
        }

        var directory = Path.GetDirectoryName(selected)!;
        var members = new SortedDictionary<int, string>();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var width = numberText.Length;
        foreach (var candidate in Directory.EnumerateFiles(directory))
        {
            var candidateName = Path.GetFileName(candidate);
            if (!candidateName.StartsWith(prefix, comparison))
            {
                continue;
            }

            var tail = candidateName.AsSpan(prefix.Length);
            if (tail.IsEmpty || !IsAsciiDigits(tail))
            {
                continue;
            }

            if (tail.Length != width || !int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || (isTwrp && (number > 899 || width != 3)))
            {
                throw new InvalidDataException($"Invalid or ambiguous volume number: {candidateName}");
            }

            if (!members.TryAdd(number, candidate))
            {
                throw new InvalidDataException($"Duplicate volume number: {candidateName}");
            }

            if (members.Count > MaximumParts)
            {
                throw new InvalidDataException("Too many split volumes.");
            }
        }

        if (!members.Values.Any(path => string.Equals(path, selected, comparison)))
        {
            throw new FileNotFoundException("Selected split volume is not present.", selected);
        }

        var firstThread = isTwrp ? members.Keys.First() / 100 : 0;
        if (isTwrp && firstThread > 1)
        {
            throw new InvalidDataException("TWRP volume set does not start at thread 0 or 1.");
        }

        var expected = isTwrp ? firstThread * 100 : 0;
        foreach (var number in members.Keys)
        {
            if (isTwrp && number / 100 != expected / 100)
            {
                if (number / 100 != expected / 100 + 1 || expected % 100 == 0)
                {
                    throw new InvalidDataException("Missing TWRP thread or volume.");
                }

                expected = number / 100 * 100;
            }

            if (number != expected++)
            {
                throw new InvalidDataException($"Missing volume {prefix}{expected - 1:D3}.");
            }
        }

        long total = 0;
        foreach (var path in members.Values)
        {
            var length = new FileInfo(path).Length;
            if (length <= 0 || length > MaximumBytes - total)
            {
                throw new InvalidDataException($"Invalid split volume length: {Path.GetFileName(path)}");
            }

            total += length;
        }

        var kind = isTwrp ? ClassifyTwrp(members.Values.First(), prefix)
            : prefix.EndsWith(".new.dat.br.", StringComparison.OrdinalIgnoreCase) ? DatSplitKind.NewDatBrotli
            : prefix.EndsWith(".new.dat.", StringComparison.OrdinalIgnoreCase) ? DatSplitKind.NewDat
            : prefix.EndsWith(".patch.dat.br.", StringComparison.OrdinalIgnoreCase) ? DatSplitKind.PatchDatBrotli
            : DatSplitKind.PatchDat;
        var companion = isTwrp ? null : Path.Combine(directory, prefix[..prefix.IndexOf(kind switch
        {
            DatSplitKind.NewDatBrotli => ".new.dat.br.",
            DatSplitKind.NewDat => ".new.dat.",
            DatSplitKind.PatchDatBrotli => ".patch.dat.br.",
            _ => ".patch.dat."
        }, StringComparison.OrdinalIgnoreCase)] + DatFileSet.TransferListSuffix);
        if (companion is not null && !File.Exists(companion))
        {
            companion = null;
        }
        else if (companion is not null)
        {
            if (new FileInfo(companion).Length > 16L * 1024 * 1024)
            {
                throw new InvalidDataException("DAT transfer list exceeds the import limit.");
            }

            TransferListParser.Parse(companion);
        }

        return new DatSplitSet(kind, [.. members.Values], total, companion);
    }

    /// <summary>
    /// Imports a numbered set to one output artifact, atomically replacing an existing file only after completion.
    /// <para>将编号卷集合导入为一个输出工件，仅在完成后原子替换已有文件。</para>
    /// </summary>
    /// <param name="selectedPath">Any member used to resolve siblings. <para>用于定位同组卷的任一成员。</para></param>
    /// <param name="outputPath">Destination file; must differ from every input. <para>目标文件，不得与任何输入相同。</para></param>
    /// <param name="progress">Optional byte progress for raw/DAT joining. <para>原始镜像和 DAT 拼接时可选的字节进度。</para></param>
    /// <param name="cancellationToken">Cancels streaming before publication. <para>发布前取消流式处理。</para></param>
    /// <returns>The validated set and its dispatch kind. <para>已校验的集合及其分发类型。</para></returns>
    public static Task<DatSplitSet> ImportAsync(
        string selectedPath,
        string outputPath,
        IProgress<(long Processed, long Total)>? progress = null,
        CancellationToken cancellationToken = default)
        => ImportAsync(selectedPath, outputPath, progress, MaximumBytes, cancellationToken);

    /// <summary>
    /// Imports a numbered set with an explicit decoded-size cap for compressed TWRP image volumes.
    /// <para>以显式解码大小上限导入编号集合，用于压缩的 TWRP 镜像卷。</para>
    /// </summary>
    /// <param name="selectedPath">Any member used to resolve siblings. <para>用于定位同组卷的任一成员。</para></param>
    /// <param name="outputPath">Destination file; must differ from every input. <para>目标文件，不得与任何输入相同。</para></param>
    /// <param name="progress">Optional byte progress for raw/DAT joining. <para>原始镜像和 DAT 拼接时可选的字节进度。</para></param>
    /// <param name="maximumDecodedBytes">Cumulative cap over the bytes produced by decoding compressed image volumes; never unbounded. <para>压缩镜像卷解码产出字节的累计上限，绝不无界。</para></param>
    /// <param name="cancellationToken">Cancels streaming before publication. <para>发布前取消流式处理。</para></param>
    /// <returns>The validated set and its dispatch kind. <para>已校验的集合及其分发类型。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumDecodedBytes"/> is below <c>1</c> or exceeds the import ceiling.
    /// <para><paramref name="maximumDecodedBytes"/> 小于 <c>1</c> 或超过导入上限。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A compressed TWRP volume carries a truncated codec header or the decoded stream exceeds the cap.
    /// <para>压缩 TWRP 卷携带被截断的编解码器头部，或解码流超过上限。</para>
    /// </exception>
    internal static async Task<DatSplitSet> ImportAsync(
        string selectedPath,
        string outputPath,
        IProgress<(long Processed, long Total)>? progress,
        long maximumDecodedBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDecodedBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumDecodedBytes, MaximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var set = Inspect(selectedPath);
        if (set.Kind is (DatSplitKind.NewDat or DatSplitKind.NewDatBrotli or DatSplitKind.PatchDat
                or DatSplitKind.PatchDatBrotli) && set.CompanionTransferList is null)
        {
            throw new InvalidDataException("A numbered DAT set requires a same-stem transfer list.");
        }

        var destination = Path.GetFullPath(outputPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (set.Parts.Any(path => string.Equals(path, destination, comparison))
            || (set.CompanionTransferList is { } list && string.Equals(list, destination, comparison)))
        {
            throw new ArgumentException("The output must not overwrite an input or its transfer list.", nameof(outputPath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = TempFileHelper.CreateAdjacentPath(destination);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (set.Kind is DatSplitKind.TwrpTar)
                {
                    await WriteTarAsync(set, output, cancellationToken);
                }
                else
                {
                    await CopyPartsAsync(set, output, progress, maximumDecodedBytes, cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (set.Kind is DatSplitKind.NewDat or DatSplitKind.NewDatBrotli)
            {
                ValidateJoinedNewData(set, temporary, cancellationToken);
            }

            AtomicPublish.Publish(temporary, destination);
            return set;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// Reconstructs a full-OTA partition image from numbered new-data volumes and their same-stem transfer list.
    /// <para>使用带编号的新数据卷及同词干传输清单重建全量 OTA 分区镜像。</para>
    /// </summary>
    /// <param name="selectedPath">Any numbered new-data volume. <para>任一带编号的新数据卷。</para></param>
    /// <param name="outputImagePath">Destination image, replaced only after successful reconstruction. <para>只在成功重建后替换的目标镜像。</para></param>
    /// <param name="progress">Optional reconstruction fraction. <para>可选的重建进度比例。</para></param>
    /// <param name="cancellationToken">Cancels joining and reconstruction before publication. <para>在发布前取消拼接与重建。</para></param>
    /// <returns>The validated source set. <para>已校验的源卷集合。</para></returns>
    public static async Task<DatSplitSet> ReconstructDatImageAsync(
        string selectedPath,
        string outputImagePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputImagePath);
        cancellationToken.ThrowIfCancellationRequested();
        var set = Inspect(selectedPath);
        if (set.Kind is not (DatSplitKind.NewDat or DatSplitKind.NewDatBrotli)
            || set.CompanionTransferList is null)
        {
            throw new InvalidDataException("Full-image reconstruction needs new-data volumes and a same-stem transfer list.");
        }

        var destination = Path.GetFullPath(outputImagePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (set.Parts.Any(path => string.Equals(path, destination, comparison))
            || string.Equals(set.CompanionTransferList, destination, comparison))
        {
            throw new ArgumentException("The output image must not overwrite an input.", nameof(outputImagePath));
        }

        if (new FileInfo(set.CompanionTransferList).Length > 16L * 1024 * 1024)
        {
            throw new InvalidDataException("DAT transfer list exceeds the import limit.");
        }

        var temporaryData = destination + ".data-" + Guid.NewGuid().ToString("N");
        try
        {
            await ImportAsync(selectedPath, temporaryData, cancellationToken: cancellationToken);
            await Task.Run(() => DatReader.Unpack(
                set.CompanionTransferList, temporaryData, null, null, destination,
                set.Kind is DatSplitKind.NewDatBrotli, progress, cancellationToken), cancellationToken);
            return set;
        }
        finally
        {
            File.Delete(temporaryData);
        }
    }

    private static async Task CopyPartsAsync(
        DatSplitSet set,
        FileStream output,
        IProgress<(long Processed, long Total)>? progress,
        long maximumDecodedBytes,
        CancellationToken token)
    {
        var buffer = new byte[BufferSize];
        long total = 0;
        long decodedTotal = 0;
        foreach (var path in set.Parts)
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = input.Length;
            if (length <= 0 || length > MaximumBytes - total)
            {
                throw new InvalidDataException("The volume length changed after inspection.");
            }

            if (set.Kind is DatSplitKind.TwrpImage)
            {
                var codec = DetectCompressedVolume(input);
                if (codec is not null)
                {
                    if (length < MinimumCodecHeaderBytes(codec.Value))
                    {
                        throw new InvalidDataException(
                            $"Compressed TWRP image volume has a truncated codec header: {Path.GetFileName(path)}");
                    }
                    if (decodedTotal >= maximumDecodedBytes)
                    {
                        throw new InvalidDataException("The decoded TWRP image budget is exhausted.");
                    }

                    var before = output.Position;
                    var budget = Math.Min(maximumDecodedBytes - decodedTotal, MaximumBytes);
                    await Task.Run(() => CompressionService.Decompress(input, output, codec.Value, budget, token), token);
                    decodedTotal = checked(decodedTotal + (output.Position - before));
                    total += length;
                    progress?.Report((total, set.TotalInputBytes));
                    continue;
                }
            }

            var remaining = length;
            while (remaining > 0)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                if (count == 0)
                {
                    throw new EndOfStreamException($"Split volume was truncated: {Path.GetFileName(path)}");
                }

                await output.WriteAsync(buffer.AsMemory(0, count), token);
                remaining -= count;
                progress?.Report((total + length - remaining, set.TotalInputBytes));
            }

            if (input.ReadByte() != -1)
            {
                throw new InvalidDataException($"Split volume grew during import: {Path.GetFileName(path)}");
            }

            total += length;
        }

        if (total != set.TotalInputBytes)
        {
            throw new InvalidDataException("The split set changed during import.");
        }
    }

    private static CompressionFormat? DetectCompressedVolume(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];
        var read = 0;
        while (read < 4)
        {
            var count = stream.Read(header[read..]);
            if (count == 0)
            {
                break;
            }
            read += count;
        }
        stream.Position = 0;
        if (read >= 2 && header[0] == 0x1F && header[1] == 0x8B)
        {
            return CompressionFormat.Gzip;
        }
        if (read == 4 && header[0] == 0x02 && header[1] == 0x21 && header[2] == 0x4C && header[3] == 0x18)
        {
            return CompressionFormat.Lz4Legacy;
        }
        if (read == 4 && header[0] == 0x28 && header[1] == 0xB5 && header[2] == 0x2F && header[3] == 0xFD)
        {
            return CompressionFormat.Zstd;
        }
        return null;
    }

    private static int MinimumCodecHeaderBytes(CompressionFormat format) => format switch
    {
        CompressionFormat.Gzip => 10,
        CompressionFormat.Lz4Legacy => 8,
        _ => 5
    };

    private static void ValidateJoinedNewData(DatSplitSet set, string path, CancellationToken token)
    {
        var list = TransferListParser.Parse(set.CompanionTransferList!);
        var expected = checked(list.NewDataBlocks * DatReader.BlockSize);
        using var file = File.OpenRead(path);
        if (set.Kind is DatSplitKind.NewDat)
        {
            if (file.Length != expected)
            {
                throw new InvalidDataException("Joined new.dat length does not match the transfer list.");
            }

            return;
        }

        using var decoder = new StrictBrotliReadStream(file);
        var buffer = new byte[BufferSize];
        long decoded = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = decoder.Read(buffer);
            if (count == 0)
            {
                break;
            }

            if (count > expected - decoded)
            {
                throw new InvalidDataException("Joined Brotli DAT exceeds the transfer-list length.");
            }

            decoded += count;
        }

        if (decoded != expected)
        {
            throw new InvalidDataException("Joined Brotli DAT is shorter than the transfer list.");
        }
    }

    private static async Task WriteTarAsync(DatSplitSet set, FileStream output, CancellationToken token)
    {
        using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        long payloadBytes = 0;
        long inputBytes = 0;
        foreach (var path in set.Parts)
        {
            token.ThrowIfCancellationRequested();
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length <= 0 || file.Length > MaximumBytes)
            {
                throw new InvalidDataException("The TAR volume length changed after inspection.");
            }

            inputBytes = checked(inputBytes + file.Length);
            using var archive = OpenTarVolume(file);
            using var cancellable = new CancellationReadStream(archive, token);
            using var reader = new TarReader(cancellable, leaveOpen: true);
            var count = 0;
            while (reader.GetNextEntry(copyData: false) is { } entry)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Length < 0 || entry.Length > MaximumBytes - payloadBytes)
                {
                    throw new InvalidDataException("TWRP TAR entry exceeds the import limit.");
                }

                payloadBytes += entry.Length;
                writer.WriteEntry(entry);
                if (++count > 1_000_000)
                {
                    throw new InvalidDataException("TWRP TAR contains too many entries.");
                }
            }

            if (count == 0)
            {
                throw new InvalidDataException($"Empty or invalid TWRP TAR volume: {Path.GetFileName(path)}");
            }
        }

        if (inputBytes != set.TotalInputBytes)
        {
            throw new InvalidDataException("The TWRP volume set changed during import.");
        }
    }

    private static Stream OpenTarVolume(FileStream file)
    {
        Span<byte> header = stackalloc byte[2];
        file.ReadExactly(header);
        file.Position = 0;
        return header[0] == 0x1F && header[1] == 0x8B
            ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: true)
            : file;
    }

    private static DatSplitKind ClassifyTwrp(string path, string prefix)
    {
        if (prefix.EndsWith(".emmc.win", StringComparison.OrdinalIgnoreCase))
        {
            using var raw = File.OpenRead(path);
            var codec = DetectCompressedVolume(raw);
            if (codec is not null && raw.Length < MinimumCodecHeaderBytes(codec.Value))
            {
                throw new InvalidDataException("Compressed TWRP image volume has a truncated codec header.");
            }

            return DatSplitKind.TwrpImage;
        }

        using var file = File.OpenRead(path);
        using var archive = OpenTarVolume(file);
        using var reader = new TarReader(archive, leaveOpen: true);
        if (reader.GetNextEntry(copyData: false) is null)
        {
            throw new InvalidDataException("Unsupported or empty TWRP volume; encrypted/raw unknown volumes cannot be merged as TAR.");
        }

        return DatSplitKind.TwrpTar;
    }

    private static bool TryTwrpName(string name, out string prefix, out string numberText)
    {
        prefix = "";
        numberText = "";
        if (name.Length < 10 || !IsAsciiDigits(name.AsSpan(name.Length - 3)))
        {
            return false;
        }

        prefix = name[..^3];
        numberText = name[^3..];
        return prefix.Contains(".win", StringComparison.OrdinalIgnoreCase)
            && prefix.EndsWith(".win", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDatName(string name, out string prefix, out string numberText)
    {
        prefix = "";
        numberText = "";
        var index = name.LastIndexOf('.');
        if (index <= 0 || index == name.Length - 1 || !IsAsciiDigits(name.AsSpan(index + 1)))
        {
            return false;
        }

        prefix = name[..(index + 1)];
        numberText = name[(index + 1)..];
        return prefix.EndsWith(".new.dat.", StringComparison.OrdinalIgnoreCase)
            || prefix.EndsWith(".new.dat.br.", StringComparison.OrdinalIgnoreCase)
            || prefix.EndsWith(".patch.dat.", StringComparison.OrdinalIgnoreCase)
            || prefix.EndsWith(".patch.dat.br.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAsciiDigits(ReadOnlySpan<char> text)
    {
        foreach (var value in text)
        {
            if (value is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

}