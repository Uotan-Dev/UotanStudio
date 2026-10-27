using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using UotanStudio.Compress;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Hash.Adler32;
using UotanStudio.Hash.Crc32Ieee;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.DeviceTree.Dt;

/// <summary>
/// Static entry points for reading, unpacking, packing and repacking DTBO overlay tables with bounded,
/// streamed blob handling.
/// <para>以有界流式处理读取、解包、打包和重打包 DTBO overlay 表的静态入口。</para>
/// </summary>
public static partial class DtboFormat
{
    /// <summary>Names the repack configuration file.<para>重打包配置文件名。</para></summary>
    public const string ManifestFileName = RepackMetadata.FileName;

    private const string OriginalFileName = "original.dtbo.img";
    private const int MaximumEntries = 65536;
    private const int SmallestSizeLevel = 9;

    internal static System.Text.Json.Nodes.JsonObject CreateConfiguration(Stream input)
    {
        var (header, entries) = ReadTable(input);
        var manifest = new DtboManifest { Header = header, ImageSize = input.Length, HasOriginalImage = true };
        for (var i = 0; i < entries.Count; i++)
            manifest.Entries.Add(new() { File = $"overlay_{i:D2}.dtb", Metadata = entries[i] });
        return (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(manifest, DtboJsonContext.Default.DtboManifest)!;
    }

    /// <summary>
    /// Reads and validates the table without loading blobs; offsets are absolute from stream start.
    /// <para>仅读取并校验条目表，不加载数据块；偏移以流起点为基准。</para>
    /// </summary>
    /// <param name="input">Readable, seekable DTBO image stream; it is repositioned and left open.
    /// <para>可读取、可定位的 DTBO 镜像流；会被重新定位并保持打开。</para></param>
    /// <returns>The validated header and its entries, every blob range proven to sit inside total_size.
    /// <para>已校验的表头及其条目；每个数据块范围均被证明位于 total_size 之内。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null. <para><paramref name="input"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException">The stream cannot read or cannot seek. <para>流不可读或不可定位。</para></exception>
    /// <exception cref="InvalidDataException">The header, table geometry or any entry range is malformed, or a
    /// compression code is unknown. <para>表头、表几何或任一条目范围无效，或压缩编码未知。</para></exception>
    public static (DtboHeader Header, List<DtboEntry> Entries) ReadTable(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek)
            throw new ArgumentException("A readable, seekable stream is required.", nameof(input));
        if (input.Length < FdtConstants.DtboHeaderSize)
            throw new InvalidDataException("Truncated DTBO header.");
        input.Position = 0;
        Span<byte> bytes = stackalloc byte[32];
        input.ReadExactly(bytes);
        var header = DtboHeader.ReadFrom(bytes);
        ValidateHeader(header);
        if (header.TotalSize > input.Length || header.HeaderSize > header.TotalSize)
            throw new InvalidDataException("DTBO total size is outside the input.");
        if (header.EntryCount > MaximumEntries)
            throw new InvalidDataException("DTBO entry count exceeds 65536.");
        var tableEnd = checked((long)header.EntriesOffset + (long)header.EntryCount * header.EntrySize);
        if (tableEnd > header.TotalSize)
            throw new InvalidDataException("DTBO entry table is outside total_size.");
        input.Position = header.EntriesOffset;
        var entries = new List<DtboEntry>((int)header.EntryCount);
        for (var i = 0; i < header.EntryCount; i++)
        {
            input.ReadExactly(bytes);
            var entry = DtboEntry.ReadFrom(bytes);
            if (entry.DtOffset < tableEnd || entry.DtOffset > header.TotalSize
                || entry.DtSize > header.TotalSize - entry.DtOffset)
                throw new InvalidDataException($"DTBO entry {i} is outside the blob area.");
            _ = CompressionOf(header.Version, entry);
            entries.Add(entry);
        }
        return (header, entries);
    }

    /// <summary>Extracts overlays, their configuration and an original image for lossless unchanged repacking.
    /// <para>提取 overlay、配置和原始镜像，支持未修改时无损重打包。</para></summary>
    /// <param name="inputPath">DTBO image file to extract.<para>要提取的 DTBO 镜像文件。</para></param>
    /// <param name="outputDirectory">Destination directory; files are staged in a temporary folder and moved only after the whole extraction succeeds.
    /// <para>目标目录；文件先写入临时暂存目录，仅在整体提取成功后移动。</para></param>
    /// <param name="options">Output-size limit per blob, overwrite policy and progress sink; <see langword="null"/> uses defaults.
    /// <para>逐数据块输出上限、覆盖策略与进度回调；为 <see langword="null"/> 时使用默认值。</para></param>
    /// <param name="cancellationToken">Cancels decoding and copying; a canceled run publishes nothing into the destination.
    /// <para>取消解码与复制；被取消的运行不会向目标发布任何文件。</para></param>
    /// <returns>Absolute paths of every written file: each overlay DTB, the original image and the repack manifest.
    /// <para>所有已写文件的绝对路径：各 overlay DTB、原始镜像与重打包清单。</para></returns>
    /// <exception cref="FileNotFoundException">The input image cannot be opened.
    /// <para>无法打开输入镜像。</para></exception>
    /// <exception cref="InvalidDataException">The table is malformed or a blob exceeds the output limit.
    /// <para>表结构无效，或某数据块超出输出上限。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="UnpackOptions.MaximumOutputBytes"/> is negative.
    /// <para><see cref="UnpackOptions.MaximumOutputBytes"/> 为负数。</para></exception>
    /// <exception cref="IOException">Extraction would overwrite the input, an existing output refuses to be replaced, or a reserved metadata name collides.
    /// <para>提取会覆盖输入、已存在的输出拒绝被替换，或保留的 元数据文件名发生冲突。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    /// <remarks>This is an explicit synchronous boundary: the repack metadata layer only offers an asynchronous writer, so this method waits on it at the boundary. Asynchronous callers should use <see cref="UnpackAsync"/> instead.
    /// <para>本方法是显式同步边界：重打包元数据层只提供异步写入器，因此本方法在该边界上等待它。 异步调用方应改用 <see cref="UnpackAsync"/>。</para></remarks>
    public static IReadOnlyList<string> Unpack(string inputPath, string outputDirectory,
        UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new UnpackOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumOutputBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(outputDirectory);
        var (names, staging) = StageBlobs(inputPath, destination, options, cancellationToken);
        try
        {
            RepackMetadata.WriteAsync(new DtFormatHandler(), inputPath, staging, cancellationToken).GetAwaiter().GetResult();
            return PublishStaged(names, staging, destination, options, cancellationToken);
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>Extracts overlays, their configuration and an original image for lossless unchanged repacking.
    /// <para>提取 overlay、配置和原始镜像，支持未修改时无损重打包。</para></summary>
    /// <param name="inputPath">DTBO image file to extract.<para>要提取的 DTBO 镜像文件。</para></param>
    /// <param name="outputDirectory">Destination directory; files are staged in a temporary folder and moved only after the whole extraction succeeds.
    /// <para>目标目录；文件先写入临时暂存目录，仅在整体提取成功后移动。</para></param>
    /// <param name="options">Output-size limit per blob, overwrite policy and progress sink; <see langword="null"/> uses defaults.
    /// <para>逐数据块输出上限、覆盖策略与进度回调；为 <see langword="null"/> 时使用默认值。</para></param>
    /// <param name="cancellationToken">Cancels decoding, copying and metadata publication; a canceled run publishes nothing into the destination.
    /// <para>取消解码、复制与元数据发布；被取消的运行不会向目标发布任何文件。</para></param>
    /// <returns>A task whose result holds absolute paths of every written file: each overlay DTB, the original image and the repack manifest.
    /// <para>任务结果为所有已写文件的绝对路径：各 overlay DTB、原始镜像与重打包清单。</para></returns>
    /// <exception cref="FileNotFoundException">The input image cannot be opened.
    /// <para>无法打开输入镜像。</para></exception>
    /// <exception cref="InvalidDataException">The table is malformed or a blob exceeds the output limit.
    /// <para>表结构无效，或某数据块超出输出上限。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="UnpackOptions.MaximumOutputBytes"/> is negative.
    /// <para><see cref="UnpackOptions.MaximumOutputBytes"/> 为负数。</para></exception>
    /// <exception cref="IOException">Extraction would overwrite the input, an existing output refuses to be replaced, or a reserved metadata name collides.
    /// <para>提取会覆盖输入、已存在的输出拒绝被替换，或保留的 元数据文件名发生冲突。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    public static Task<IReadOnlyList<string>> UnpackAsync(string inputPath, string outputDirectory,
        UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new UnpackOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumOutputBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(outputDirectory);
        var (names, staging) = StageBlobs(inputPath, destination, options, cancellationToken);
        return UnpackCoreAsync(inputPath, destination, names, staging, options, cancellationToken);
    }

    /// <summary>Metadata publication and staged publication for <see cref="UnpackAsync"/>. <para><see cref="UnpackAsync"/> 的元数据发布与分阶段发布流程。</para></summary>
    private static async Task<IReadOnlyList<string>> UnpackCoreAsync(string inputPath, string destination,
        string[] names, string staging, UnpackOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await RepackMetadata.WriteAsync(new DtFormatHandler(), inputPath, staging, cancellationToken).ConfigureAwait(false);
            return PublishStaged(names, staging, destination, options, cancellationToken);
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// Validates the extraction targets and streams every overlay blob plus the original image into the staging directory.
    /// <para>校验提取目标，并把每个 overlay 数据块与原始镜像流式写入暂存目录。</para>
    /// </summary>
    private static (string[] Names, string Staging) StageBlobs(string inputPath, string destination,
        UnpackOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        using var input = File.OpenRead(inputPath);
        var (header, entries) = ReadTable(input);
        var manifest = new DtboManifest { Header = header, ImageSize = input.Length, HasOriginalImage = true };
        for (var i = 0; i < entries.Count; i++)
            manifest.Entries.Add(new() { File = $"overlay_{i:D2}.dtb", Metadata = entries[i] });

        var names = manifest.Entries.Select(e => e.File).Append(OriginalFileName).Append(ManifestFileName).ToArray();
        foreach (var name in names)
        {
            var path = LocalFile(destination, name);
            if (SamePath(path, inputPath))
                throw new IOException("Extraction would overwrite its input image.");
            if (Directory.Exists(path) || (!options.Overwrite && File.Exists(path)))
                throw new IOException($"Output already exists: {path}");
            if (name == ManifestFileName && File.Exists(path) && !RepackMetadata.IsMetadataFile(path))
                throw new IOException("The output contains a file reserved for repack metadata.");
        }

        var staging = Path.Combine(destination, ".dtbo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                using var output = File.Create(Path.Combine(staging, manifest.Entries[i].File));
                CopyBlob(input, header.Version, entries[i], output, options.MaximumOutputBytes, cancellationToken);
                options.Progress?.Report(90.0 * (i + 1) / entries.Count, manifest.Entries[i].File);
            }
            using (var original = File.Create(Path.Combine(staging, OriginalFileName)))
            {
                input.Position = 0;
                StreamCopy.CopyExactly(input, original, input.Length, cancellationToken);
            }
        }
        catch
        {
            Directory.Delete(staging, recursive: true);
            throw;
        }
        return (names, staging);
    }

    /// <summary>Moves staged files to their destination names and reports completion. <para>把分阶段文件移动到目标名称并上报完成。</para></summary>
    private static IReadOnlyList<string> PublishStaged(string[] names, string staging, string destination,
        UnpackOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var name in names)
            File.Move(Path.Combine(staging, name), LocalFile(destination, name), options.Overwrite);
        options.Progress?.Report(100, "DTBO overlays and manifest extracted.");
        return [.. names.Select(name => Path.Combine(destination, name))];
    }

    /// <summary>Rebuilds a manifest directory, preserving metadata and compression; changed authenticated tails are rejected.
    /// <para>从配置目录重建并保留元数据和压缩；修改后拒绝复用认证尾部。</para></summary>
    /// <param name="inputDirectory">Directory produced by an earlier <see cref="Unpack"/> call, including the repack manifest.
    /// <para>由更早一次 <see cref="Unpack"/> 调用生成的目录，须包含重打包清单。</para></param>
    /// <param name="outputPath">Destination image, written to a temporary file and published atomically.
    /// <para>目标镜像，先写临时文件再原子发布。</para></param>
    /// <param name="cancellationToken">Cancels before or during the rebuild; the existing destination keeps its content.
    /// <para>在重建开始前或进行中取消；已有目标保持原内容。</para></param>
    /// <exception cref="InvalidDataException">Metadata or manifest is missing or invalid, an overlay fails DTB bounds validation, the header layout change is unsupported, a modified image carries nonzero trailing authentication data, or the rebuild exceeds the recorded image size.
    /// <para>元数据或清单缺失/无效、某个 overlay 未通过 DTB 边界校验、不支持的表头布局变更、被修改的镜像带有 非零尾部认证数据，或重建结果超出记录的镜像大小。</para></exception>
    /// <exception cref="IOException"><paramref name="outputPath"/> would replace one of the manifest inputs.
    /// <para><paramref name="outputPath"/> 将替换清单的某个输入。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    public static void PackDirectory(string inputDirectory, string outputPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetFullPath(inputDirectory);
        var metadata = RepackMetadata.Read(directory);
        if (metadata?.FormatName != new DtFormatHandler().FormatName
            || metadata.Configuration["entries"] is not System.Text.Json.Nodes.JsonArray)
            throw new InvalidDataException("Missing DTBO repack metadata.");
        var manifest = metadata.Configuration.Deserialize(DtboJsonContext.Default.DtboManifest)
            ?? throw new InvalidDataException("Missing DTBO manifest.");
        if (manifest.FormatVersion != 1 || manifest.Header is null || manifest.Entries is null
            || manifest.Entries.Count > MaximumEntries || manifest.ImageSize < 0)
            throw new InvalidDataException("Invalid or unsupported DTBO manifest.");
        ValidateHeader(manifest.Header);
        var inputs = manifest.Entries.Select(entry => entry is not null && entry.Metadata is not null
            ? LocalFile(directory, entry.File) : throw new InvalidDataException("Invalid DTBO manifest entry.")).ToArray();
        if (inputs.Any(path => SamePath(path, outputPath))
            || SamePath(outputPath, LocalFile(directory, ManifestFileName))
            || SamePath(outputPath, LocalFile(directory, OriginalFileName)))
            throw new IOException("DTBO output must not replace a manifest input.");

        using var original = manifest.HasOriginalImage ? File.OpenRead(LocalFile(directory, OriginalFileName)) : null;
        DtboHeader? originalHeader = null;
        List<DtboEntry>? originalEntries = null;
        if (original is not null)
            (originalHeader, originalEntries) = ReadTable(original);
        else if (manifest.Header.HeaderSize != 32 || manifest.Header.EntriesOffset != 32)
            throw new InvalidDataException("Extended DTBO headers require the original image.");

        AtomicWrite(outputPath, output =>
        {
            var scratchPath = output.Name + ".blobs";
            try
            {
                using var scratch = new FileStream(scratchPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                var unchanged = original is not null && original.Length == manifest.ImageSize
                    && originalHeader!.ToBytes().AsSpan().SequenceEqual(manifest.Header.ToBytes())
                    && originalEntries!.Count == manifest.Entries.Count;
                var sizes = new List<uint>(inputs.Length);
                for (var i = 0; i < inputs.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = manifest.Entries[i].Metadata;
                    var compression = CompressionOf(manifest.Header.Version, entry);
                    using var source = File.OpenRead(inputs[i]);
                    ValidateDtbFile(source);
                    var start = scratch.Position;
                    byte[] hash;
                    using (var encoder = CreateEncoder(scratch, compression))
                        hash = CopyAndHash(source, encoder ?? scratch, source.Length, cancellationToken, source.Length);
                    sizes.Add(checked((uint)(scratch.Position - start)));
                    if (unchanged)
                    {
                        unchanged = originalEntries![i].ToBytes().AsSpan().SequenceEqual(entry.ToBytes());
                        if (unchanged)
                        {
                            var originalHash = CopyBlob(original!, originalHeader!.Version, originalEntries[i],
                                Stream.Null, uint.MaxValue, cancellationToken);
                            unchanged = hash.AsSpan().SequenceEqual(originalHash);
                        }
                    }
                }
                if (unchanged)
                {
                    original!.Position = 0;
                    StreamCopy.CopyExactly(original, output, original.Length, cancellationToken);
                    return;
                }

                if (original is not null)
                {
                    if (manifest.Header.HeaderSize != originalHeader!.HeaderSize
                        || manifest.Header.EntriesOffset != originalHeader.EntriesOffset)
                        throw new InvalidDataException("Changing extended DTBO header layout is unsupported.");
                    EnsureZeroTail(original, originalHeader.TotalSize, cancellationToken);
                    original.Position = 0;
                    StreamCopy.CopyExactly(original, output, originalHeader.EntriesOffset, cancellationToken);
                }

                var header = DtboHeader.ReadFrom(manifest.Header.ToBytes());
                header.EntryCount = checked((uint)inputs.Length);
                var offset = checked((long)header.EntriesOffset + (long)header.EntryCount * 32);
                header.TotalSize = checked((uint)(offset + scratch.Length));
                var imageSize = manifest.ImageSize == 0 ? header.TotalSize : manifest.ImageSize;
                if (imageSize < header.TotalSize)
                    throw new InvalidDataException("Rebuilt DTBO exceeds imageSize; increase the unsigned image capacity explicitly.");
                output.SetLength(imageSize);
                output.Position = 0;
                output.Write(header.ToBytes());
                output.Position = header.EntriesOffset;
                for (var i = 0; i < inputs.Length; i++)
                {
                    var entry = DtboEntry.ReadFrom(manifest.Entries[i].Metadata.ToBytes());
                    entry.DtSize = sizes[i];
                    entry.DtOffset = checked((uint)offset);
                    output.Write(entry.ToBytes());
                    offset = checked(offset + sizes[i]);
                }
                scratch.Position = 0;
                StreamCopy.CopyExactly(scratch, output, scratch.Length, cancellationToken);
            }
            finally
            {
                File.Delete(scratchPath);
            }
        }, cancellationToken);
    }

    /// <summary>Validates all overlays and atomically copies an unchanged image, including its padding and authentication data.
    /// <para>校验全部 overlay 后原子重打包未修改镜像，保留填充及认证数据。</para></summary>
    /// <param name="inputPath">Existing DTBO image to validate and copy byte for byte.
    /// <para>要逐字节校验并复制的现有 DTBO 镜像。</para></param>
    /// <param name="outputPath">Destination file, published from a temporary file after validation succeeds.
    /// <para>目标文件，在校验成功后由临时文件发布。</para></param>
    /// <param name="cancellationToken">Cancels validation or copying; the temporary output is discarded and the existing destination is untouched.
    /// <para>取消校验或复制；临时输出被丢弃且既有目标不受影响。</para></param>
    /// <exception cref="FileNotFoundException">The input image cannot be opened.
    /// <para>无法打开输入镜像。</para></exception>
    /// <exception cref="InvalidDataException">The table is malformed or a decoded blob exceeds the 256 MiB default budget.
    /// <para>表结构无效，或某解码数据块超出 256 MiB 默认预算。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    public static void Repack(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        AtomicWrite(outputPath, output =>
        {
            using var input = File.OpenRead(inputPath);
            var (header, entries) = ReadTable(input);
            foreach (var entry in entries)
                CopyBlob(input, header.Version, entry, Stream.Null, DefaultMaximumDecompressedBytes, cancellationToken);
            input.Position = 0;
            StreamCopy.CopyExactly(input, output, input.Length, cancellationToken);
        }, cancellationToken);
    }

    private static void ValidateHeader(DtboHeader header)
    {
        if (!header.IsValidMagic || header.Version > 1 || header.HeaderSize < 32
            || header.EntrySize != 32 || header.EntriesOffset < header.HeaderSize)
            throw new InvalidDataException("Invalid or unsupported DTBO header.");
    }

    private static uint CompressionOf(uint version, DtboEntry entry)
    {
        var compression = version == 0 ? CompressionNone : entry.Custom0 & CompressionFlagMask;
        if (compression > CompressionGzip)
            throw new InvalidDataException($"Unknown DTBO compression flags: 0x{compression:X}.");
        return compression;
    }

    /// <summary>
    /// Creates the incremental encoder for a DTBO compression flag, appending its member at the current position of
    /// <paramref name="output"/> and leaving that stream open.
    /// <para>为 DTBO 压缩标志创建增量编码器，成员从 <paramref name="output"/> 的当前位置开始追加，且不关闭该流。</para>
    /// </summary>
    /// <param name="output">Blob destination positioned at the start of the member.
    /// <para>定位在成员起点的数据块目标流。</para></param>
    /// <param name="compression">Flag decoded by <see cref="CompressionOf"/>; <see cref="CompressionNone"/> yields
    /// <see langword="null"/> so the caller writes the plaintext itself.
    /// <para>由 <see cref="CompressionOf"/> 解析出的压缩标志；<see cref="CompressionNone"/> 返回
    /// <see langword="null"/>，由调用方自行写明文。</para></param>
    /// <returns>The encoder that must be disposed before the blob length is recorded, or <see langword="null"/> for an
    /// uncompressed blob. <para>必须在记录数据块长度之前释放的编码器；未压缩的数据块返回 <see langword="null"/>。</para></returns>
    /// <remarks>
    /// The gzip branch goes through <see cref="CompressionService.CreateCompressionStream"/> with
    /// <see cref="CompressionOptions.Level"/> set to <see cref="SmallestSizeLevel"/>, because the codec mapper maps
    /// every level of nine and above onto <see cref="CompressionLevel.SmallestSize"/>; a mapper change outside that
    /// band would rewrite every packed byte. The zlib branch stays on the built-in <see cref="ZLibStream"/> because the
    /// library's zlib codec is a different deflate implementation whose level enum has no smallest-size member.
    /// <para>gzip 分支经由 <see cref="CompressionService.CreateCompressionStream"/> 创建，并把
    /// <see cref="CompressionOptions.Level"/> 设为 <see cref="SmallestSizeLevel"/>：映射器把九级及以上统一转换为
    /// <see cref="CompressionLevel.SmallestSize"/>，映射器一旦跳出该档位，所有打包字节都会随之改变。zlib 分支仍使用
    /// 内置 <see cref="ZLibStream"/>，因为库中的 zlib 编解码器是另一套 deflate 实现，其等级枚举没有最小体积成员。</para>
    /// </remarks>
    private static Stream? CreateEncoder(Stream output, uint compression) => compression switch
    {
        CompressionZlib => new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true),
        CompressionGzip => CompressionService.CreateCompressionStream(output, CompressionFormat.Gzip,
            new CompressionOptions { Level = SmallestSizeLevel }),
        _ => null
    };

    /// <remarks>
    /// A zlib blob is authenticated by folding every decoded block into a running Adler-32 value with
    /// <see cref="Adler32.Compute(ReadOnlySpan{byte}, uint)"/>, which the hash library seeds with the previous packed
    /// value so the streamed result equals the one-shot checksum over the concatenated bytes; the running value starts
    /// at <c>1</c>. The gzip branch accumulates with <see cref="Crc32IeeeAccumulator"/>, the hash library's standard
    /// CRC-32 form.
    /// <para>zlib 数据块的认证方式是把每个解码块用 <see cref="Adler32.Compute(ReadOnlySpan{byte}, uint)"/> 并入累进的
    /// Adler-32 值，哈希库以上一次的打包值作为种子，因此流式结果与对拼接字节一次性计算的结果一致；累进值从 <c>1</c>
    /// 开始。gzip 分支改用 <see cref="Crc32IeeeAccumulator"/> 累加，即哈希库提供的标准 CRC-32 形式。</para>
    /// </remarks>
    private static byte[] CopyBlob(Stream input, uint version, DtboEntry entry, Stream output,
        long limit, CancellationToken cancellationToken)
    {
        using var range = new BoundedReadStream(input, entry.DtOffset, entry.DtSize, leaveOpen: true);
        var compression = CompressionOf(version, entry);
        using Stream? decoder = compression switch
        {
            CompressionZlib => new ZLibStream(range, CompressionMode.Decompress, leaveOpen: true),
            CompressionGzip => CompressionService.CreateDecompressionStream(range, CompressionFormat.Gzip),
            _ => null
        };
        var crc = compression == CompressionGzip ? new Crc32IeeeAccumulator() : null;
        var adler = 1u;
        long length = 0;
        var hash = CopyAndHash(decoder ?? range, output, limit, cancellationToken,
            compression == CompressionNone ? entry.DtSize : null, (buffer, count) =>
            {
                crc?.Append(buffer.AsSpan(0, count));
                if (compression == CompressionZlib)
                {
                    adler = Adler32.Compute(buffer.AsSpan(0, count), adler);
                }
                length += count;
            });
        if (compression != CompressionNone)
        {
            var trailerSize = compression == CompressionGzip ? 8 : 4;
            if (entry.DtSize < (compression == CompressionGzip ? 18 : 6))
                throw new InvalidDataException("Truncated DTBO compression wrapper.");
            Span<byte> trailer = stackalloc byte[8];
            input.Position = (long)entry.DtOffset + entry.DtSize - trailerSize;
            input.ReadExactly(trailer[..trailerSize]);
            if (compression == CompressionGzip
                ? BinaryPrimitives.ReadUInt32LittleEndian(trailer) != crc!.GetCurrentHash()
                    || BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) != unchecked((uint)length)
                : BinaryPrimitives.ReadUInt32BigEndian(trailer) != adler)
                throw new InvalidDataException("DTBO compression trailer checksum or size mismatch.");
        }
        return hash;
    }

    private static byte[] CopyAndHash(Stream input, Stream output, long limit, CancellationToken cancellationToken,
        long? exactLength = null, Action<byte[], int>? observe = null)
    {
        using var hash = Sha256.CreateAccumulator();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer);
            if (read == 0) break;
            if (read > limit - total)
                throw new InvalidDataException("DTBO overlay exceeds the output limit.");
            total += read;
            output.Write(buffer, 0, read);
            hash.Append(buffer.AsSpan(0, read));
            observe?.Invoke(buffer, read);
        }
        if (exactLength is not null && total != exactLength)
            throw new InvalidDataException("Truncated DTBO overlay.");
        return hash.GetHashAndReset();
    }

    private static void EnsureZeroTail(Stream input, long offset, CancellationToken cancellationToken)
    {
        input.Position = offset;
        var buffer = new byte[81920];
        while (input.Position < input.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, input.Length - input.Position);
            input.ReadExactly(buffer.AsSpan(0, count));
            if (buffer.AsSpan(0, count).ContainsAnyExcept((byte)0))
                throw new InvalidDataException("Modified DTBO has AVB or other nonzero trailing data. Remove authentication using an AVB-aware workflow before unpacking and editing.");
        }
    }

    private static void ValidateDtbFile(Stream input)
    {
        Span<byte> bytes = stackalloc byte[40];
        input.ReadExactly(bytes);
        var header = FdtHeader.ReadFrom(bytes);
        if (!header.IsValidMagic || header.TotalSize < 40 || header.TotalSize > input.Length
            || input.Length > uint.MaxValue || header.OffsetDtStruct > header.TotalSize
            || header.SizeDtStruct > header.TotalSize - header.OffsetDtStruct
            || header.OffsetDtStrings > header.TotalSize || header.SizeDtStrings > header.TotalSize - header.OffsetDtStrings
            || header.OffsetMemRsvmap > header.TotalSize || 16 > header.TotalSize - header.OffsetMemRsvmap)
            throw new InvalidDataException("Invalid DTB header or component bounds in manifest input.");
        input.Position = 0;
    }

    internal static void AtomicWrite(string outputPath, Action<FileStream> write, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = TempFileHelper.CreateAdjacentPath(path);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                write(output);
            cancellationToken.ThrowIfCancellationRequested();
            AtomicPublish.Publish(temporary, path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string LocalFile(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ') || name.IndexOfAny(['/', '\\', ':']) >= 0
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.IsPathRooted(name))
            throw new InvalidDataException("DTBO manifest paths must be local filenames.");
        var path = Path.Combine(directory, name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("DTBO manifest files must not be symbolic links.");
        return path;
    }

    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}