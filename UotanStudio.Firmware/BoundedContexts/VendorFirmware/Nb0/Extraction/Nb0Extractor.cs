using System.Buffers;
using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Models;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Parsing;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Extraction;

/// <summary>
/// Controls which NB0 entries are extracted and whether an existing destination file is replaced.
/// <para>控制提取哪些 NB0 条目，以及是否替换已存在的目标文件。</para>
/// </summary>
public sealed class ExtractionOptions
{
    /// <summary>Whether extraction replaces a destination file that already exists.
    /// <para>提取时是否替换已存在的目标文件。</para></summary>
    /// <value><see langword="true"/> to overwrite, <see langword="false"/> to skip the entry; the default is
    /// <see langword="true"/>, while a <see langword="null"/> options instance behaves like <see langword="false"/>.
    /// <para>为 <see langword="true"/> 时覆盖，为 <see langword="false"/> 时跳过该条目；默认值为 <see langword="true"/>，
    /// 而传入 <see langword="null"/> 选项实例的效果等同于 <see langword="false"/>。</para></value>
    public bool Overwrite { get; set; } = true;

    /// <summary>Predicate over the unsanitized archive entry name deciding which entries reach the disk.
    /// <para>作用于未净化归档条目名的谓词，决定哪些条目会写入磁盘。</para></summary>
    /// <value><see langword="true"/> to extract the entry. <see langword="null"/> extracts every entry; names rejected by
    /// the filter are still counted as completed progress.
    /// <para>返回 <see langword="true"/> 时提取该条目。<see langword="null"/> 表示提取全部条目；被过滤掉的条目名仍会计入已完成进度。</para></value>
    public Func<string, bool>? FileFilter { get; set; }
}

/// <summary>
/// Outcome of an NB0 extraction run: how many files landed on disk plus the failures and parser notes collected along the way.
/// <para>NB0 提取流程的结果：写入磁盘的文件数量，以及过程中收集的失败项与解析器提示。</para>
/// </summary>
public sealed class ExtractionResult
{
    /// <summary>Number of entries whose bytes were written to the destination directory.
    /// <para>字节已写入目标目录的条目数量。</para></summary>
    /// <value>One per entry that passed filtering, checksum verification and the final move; filtered-out and failed entries are not counted.
    /// <para>每个通过筛选、校验和验证并最终完成移动的条目计一次；被过滤掉或失败的条目不计入。</para></value>
    public int ExtractedFileCount { get; set; }

    /// <summary>Per-entry failure messages of the form <c>Failed to extract '&lt;name&gt;': &lt;reason&gt;</c>.
    /// <para>逐条目的失败消息，格式为 <c>Failed to extract '&lt;name&gt;': &lt;reason&gt;</c>。</para></summary>
    /// <value>A mutable list that is empty when every handled entry succeeded; a non-empty list marks the run as failed.
    /// <para>可变列表，所有已处理条目成功时为空；列表非空即表示本次提取失败。</para></value>
    public List<string> Errors { get; set; } = [];

    /// <summary>Parser warnings carried over from the NB0 metadata, for example structurally odd records.
    /// <para>从 NB0 元数据继承的解析器警告，例如结构异常的记录。</para></summary>
    /// <value>A mutable list, empty when the parser reported nothing; warnings never affect <see cref="Success"/>.
    /// <para>可变列表，解析器未报告内容时为空；警告不会影响 <see cref="Success"/>。</para></value>
    public List<string> Warnings { get; set; } = [];

    /// <summary>Whether the run finished without any per-entry failure.
    /// <para>本次提取是否未出现任何逐条目失败。</para></summary>
    /// <value><see langword="true"/> when <see cref="Errors"/> is empty; warnings are ignored by this check.
    /// <para><see cref="Errors"/> 为空时为 <see langword="true"/>；此判断忽略警告。</para></value>
    public bool Success => Errors.Count == 0;

    /// <summary>
    /// Returns a string representation of this value.
    /// <para>返回此值的字符串表示。</para>
    /// </summary>
    public override string ToString() => $"Extracted {ExtractedFileCount} files, {Errors.Count} errors, {Warnings.Count} warnings";
}

/// <summary>
/// Extracts NB0 firmware entries from a file or a seekable stream, validating each entry range against the input length
/// and comparing the incrementally computed MD5 with the archive's MD5 records.
/// <para>从文件或可寻址流中提取 NB0 固件条目，按输入长度校验每个条目的范围，并把增量计算的 MD5 与归档内的 MD5 记录比对。</para>
/// </summary>
/// <remarks>
/// Entries are read by absolute position, so the input stream must support seeking; its position is moved during
/// extraction and the stream is never closed here. Each extracted file is written to a temporary sibling path, verified,
/// and only then moved onto the destination.
/// <para>条目按绝对位置读取，因此输入流必须支持寻址；提取过程会移动流位置，但绝不在此关闭流。每个解出的文件先写入同目录的临时路径，
/// 校验通过后才移动到目标位置。</para>
/// </remarks>
/// <param name="bufferSize">
/// Size in bytes of the pooled read chunk and of the <see cref="FileStream"/> buffer used by the file-based overloads;
/// larger values reduce the number of read calls at the cost of per-entry buffering. Defaults to 1 MiB.
/// <para>池化读取块以及文件重载所用 <see cref="FileStream"/> 缓冲区的字节大小；取值越大读取次数越少，但单条目缓冲开销越大。默认为 1 MiB。</para>
/// </param>
public sealed class Nb0Extractor(int bufferSize = Nb0Extractor.DefaultBufferSize)
{
    private const int DefaultBufferSize = 1024 * 1024;

    private readonly int _bufferSize = bufferSize;

    /// <summary>
    /// Extracts all entries from an NB0 firmware file to the output directory.
    /// <para>从 NB0 固件文件中提取所有条目到输出目录。</para>
    /// </summary>
    /// <param name="nb0FilePath">
    /// Path of the NB0 file; it is opened read-only, shared for reading and closed before the call returns.
    /// <para>NB0 文件路径；以只读、允许共享读的方式打开，并在调用返回前关闭。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Destination directory, created when absent. Entry names are sanitized by replacing invalid file-name characters with
    /// <c>_</c>, and every entry lands directly in this directory.
    /// <para>目标目录，不存在时创建。条目名中非法文件名字符会被替换为 <c>_</c>，所有条目都直接写入该目录。</para>
    /// </param>
    /// <param name="options">
    /// Filter and overwrite policy; <see langword="null"/> extracts every entry and leaves existing files untouched.
    /// <para>筛选与覆盖策略；传 <see langword="null"/> 时提取全部条目且不改动已存在的文件。</para>
    /// </param>
    /// <param name="progress">
    /// Sink notified once per handled entry with the total, completed count and current name; <see langword="null"/> reports
    /// nothing.
    /// <para>每个被处理的条目通知一次，携带总数、已完成数与当前名称；传 <see langword="null"/> 表示不上报。</para>
    /// </param>
    /// <returns>
    /// The run outcome; a failing entry is recorded in <see cref="ExtractionResult.Errors"/> and the remaining entries are
    /// still attempted.
    /// <para>本次运行的结果；失败的条目会记入 <see cref="ExtractionResult.Errors"/>，其余条目仍会继续处理。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="nb0FilePath"/> or <paramref name="outputDirectory"/> is <see langword="null"/> or empty.
    /// <para><paramref name="nb0FilePath"/> 或 <paramref name="outputDirectory"/> 为 <see langword="null"/> 或空字符串。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="nb0FilePath"/> does not exist. <para><paramref name="nb0FilePath"/> 不存在。</para>
    /// </exception>
    /// <exception cref="Nb0CorruptedException">
    /// The container header or entry table is malformed; this aborts the whole run instead of being recorded as an entry error.
    /// <para>容器头部或条目表结构损坏；这会中止整个流程，而不会作为条目错误记录。</para>
    /// </exception>
    /// <example>
    /// <code>
    /// var result = new Nb0Extractor().Extract(@"C:\fw\image.nb0", @"C:\out", new ExtractionOptions { Overwrite = false });
    /// if (!result.Success)
    /// {
    ///     foreach (var error in result.Errors)
    ///     {
    ///         Console.Error.WriteLine(error);
    ///     }
    /// }
    /// </code>
    /// </example>
    public ExtractionResult Extract(string nb0FilePath, string outputDirectory, ExtractionOptions? options = null, IProgress<Nb0ExtractionProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(nb0FilePath);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        using var stream = new FileStream(nb0FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, _bufferSize, FileOptions.SequentialScan);
        return ExtractFromStream(stream, outputDirectory, options, progress);
    }

    /// <summary>
    /// Asynchronously extracts all entries from an NB0 firmware file to the output directory.
    /// <para>异步从 NB0 固件文件中提取所有条目到输出目录。</para>
    /// </summary>
    /// <param name="nb0FilePath">
    /// Path of the NB0 file; it is opened asynchronously, read-only and shared for reading, and closed before the task completes.
    /// <para>NB0 文件路径；以异步、只读、允许共享读的方式打开，并在任务完成前关闭。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Destination directory, created when absent; entry names are sanitized the same way as in the synchronous overload.
    /// <para>目标目录，不存在时创建；条目名的净化方式与同步重载相同。</para>
    /// </param>
    /// <param name="options">
    /// Filter and overwrite policy; <see langword="null"/> extracts every entry and leaves existing files untouched.
    /// <para>筛选与覆盖策略；传 <see langword="null"/> 时提取全部条目且不改动已存在的文件。</para>
    /// </param>
    /// <param name="progress">
    /// Sink notified once per handled entry; <see langword="null"/> reports nothing.
    /// <para>每个被处理的条目通知一次；传 <see langword="null"/> 表示不上报。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels header parsing, entry reads and the destination move. A cancelled run throws instead of returning a result;
    /// the destination of the entry in flight is never created and its temporary file is deleted.
    /// <para>取消头部解析、条目读取与目标文件移动。被取消时抛出异常而不返回结果；正在处理条目的目标文件不会被创建，其临时文件会被删除。</para>
    /// </param>
    /// <returns>
    /// A task carrying the run outcome; per-entry failures are collected while the remaining entries continue.
    /// <para>携带运行结果的任务；逐条目失败会被收集，其余条目继续处理。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="nb0FilePath"/> or <paramref name="outputDirectory"/> is <see langword="null"/> or empty.
    /// <para><paramref name="nb0FilePath"/> 或 <paramref name="outputDirectory"/> 为 <see langword="null"/> 或空字符串。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="nb0FilePath"/> does not exist. <para><paramref name="nb0FilePath"/> 不存在。</para>
    /// </exception>
    /// <exception cref="Nb0CorruptedException">
    /// The container header or entry table is malformed. <para>容器头部或条目表结构损坏。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signalled; cancellation is rethrown and never recorded as an entry error.
    /// <para><paramref name="cancellationToken"/> 被取消；取消异常会重新抛出，绝不作为条目错误记录。</para>
    /// </exception>
    public async Task<ExtractionResult> ExtractAsync(string nb0FilePath, string outputDirectory, ExtractionOptions? options = null, IProgress<Nb0ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nb0FilePath);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        using var stream = new FileStream(nb0FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, _bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ExtractFromStreamAsync(stream, outputDirectory, options, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts all entries from a stream containing NB0 firmware data to the output directory.
    /// <para>从包含 NB0 固件数据的流中提取所有条目到输出目录。</para>
    /// </summary>
    /// <param name="stream">
    /// NB0 data positioned at the container start; it must be readable and seekable, is repositioned per entry, and stays
    /// open for the caller when the call returns.
    /// <para>定位在容器起始处的 NB0 数据；必须可读且可寻址，每个条目都会重新定位，调用返回后仍由调用方持有。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Destination directory, created when absent; sanitized entry names are written directly inside it.
    /// <para>目标目录，不存在时创建；净化后的条目名直接写入该目录。</para>
    /// </param>
    /// <param name="options">
    /// Filter and overwrite policy; <see langword="null"/> extracts every entry but skips names whose destination file
    /// already exists.
    /// <para>筛选与覆盖策略；传 <see langword="null"/> 时提取全部条目，但目标文件已存在的条目会被跳过。</para>
    /// </param>
    /// <param name="progress">
    /// Sink notified once per handled entry, including filtered-out ones; <see langword="null"/> reports nothing.
    /// <para>每个被处理的条目（含被过滤掉的）通知一次；传 <see langword="null"/> 表示不上报。</para>
    /// </param>
    /// <returns>
    /// The run outcome carrying parser warnings and one <see cref="ExtractionResult.Errors"/> line per failed entry,
    /// including MD5 mismatches; a failed entry does not stop the run and its temporary file is deleted.
    /// <para>运行结果，携带解析警告以及每个失败条目一条 <see cref="ExtractionResult.Errors"/> 记录（含 MD5 不一致）；
    /// 条目失败不会中止流程，其临时文件会被删除。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> is <see langword="null"/>. <para><paramref name="stream"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="outputDirectory"/> is <see langword="null"/> or empty.
    /// <para><paramref name="outputDirectory"/> 为 <see langword="null"/> 或空字符串。</para>
    /// </exception>
    /// <exception cref="Nb0CorruptedException">
    /// The header, entry table or an entry range is malformed; parsing failures escape rather than becoming entry errors.
    /// <para>头部、条目表或某个条目范围损坏；解析失败会直接抛出，不会转为条目错误。</para>
    /// </exception>
    public ExtractionResult ExtractFromStream(Stream stream, string outputDirectory, ExtractionOptions? options = null, IProgress<Nb0ExtractionProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        var result = new ExtractionResult();
        var metadata = Nb0Parser.ParseFromStream(stream);

        result.Warnings.AddRange(metadata.Warnings);
        Directory.CreateDirectory(outputDirectory);

        var total = metadata.Entries.Count;
        var completed = 0;

        for (var i = 0; i < metadata.Entries.Count; i++)
        {
            var entry = metadata.Entries[i];
            if (options?.FileFilter is not null && !options.FileFilter(entry.Name))
            {
                completed++;
                continue;
            }

            try
            {
                var safeName = SanitizeEntryName(entry.Name);
                var outPath = Path.Combine(outputDirectory, safeName);
                if (File.Exists(outPath) && options?.Overwrite != true)
                {
                    completed++;
                    continue;
                }

                var tempPath = CreateTempPath(outPath);
                try
                {
                    var md5 = ExtractEntryToFile(stream, entry, tempPath);
                    VerifyComputedMd5(entry, i, md5, metadata);
                    File.Move(tempPath, outPath, overwrite: true);
                    result.ExtractedFileCount++;
                }
                finally
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to extract '{entry.Name}': {ex.Message}");
            }

            completed++;
            progress?.Report(new Nb0ExtractionProgress { TotalEntries = total, CompletedEntries = completed, CurrentEntryName = entry.Name });
        }

        return result;
    }

    /// <summary>
    /// Asynchronously extracts all entries from a stream containing NB0 firmware data to the output directory.
    /// <para>异步从包含 NB0 固件数据的流中提取所有条目到输出目录。</para>
    /// </summary>
    /// <param name="stream">
    /// NB0 data positioned at the container start; it must be readable and seekable, and stays open for the caller.
    /// <para>定位在容器起始处的 NB0 数据；必须可读且可寻址，调用结束后仍归调用方所有。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Destination directory, created when absent. <para>目标目录，不存在时创建。</para>
    /// </param>
    /// <param name="options">
    /// Filter and overwrite policy; <see langword="null"/> extracts every entry but keeps existing destination files.
    /// <para>筛选与覆盖策略；传 <see langword="null"/> 时提取全部条目，但保留已存在的目标文件。</para>
    /// </param>
    /// <param name="progress">
    /// Sink notified once per handled entry; <see langword="null"/> reports nothing.
    /// <para>每个被处理的条目通知一次；传 <see langword="null"/> 表示不上报。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before each entry and observed by every read, write and flush; once signalled it aborts the run and is
    /// rethrown instead of being recorded as an entry error.
    /// <para>每个条目处理前检查，且每次读、写、刷盘都会观察该令牌；一旦取消即中止流程并重新抛出，不会作为条目错误记录。</para>
    /// </param>
    /// <returns>
    /// A task carrying the run outcome with parser warnings and one error line per failed entry.
    /// <para>携带运行结果的任务，含解析警告与每个失败条目一条错误记录。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> is <see langword="null"/>. <para><paramref name="stream"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="outputDirectory"/> is <see langword="null"/> or empty.
    /// <para><paramref name="outputDirectory"/> 为 <see langword="null"/> 或空字符串。</para>
    /// </exception>
    /// <exception cref="Nb0CorruptedException">
    /// The header, entry table or an entry range is malformed. <para>头部、条目表或某个条目范围损坏。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signalled before or during an entry.
    /// <para>在某个条目开始之前或处理之中 <paramref name="cancellationToken"/> 已被取消。</para>
    /// </exception>
    public async Task<ExtractionResult> ExtractFromStreamAsync(Stream stream, string outputDirectory, ExtractionOptions? options = null, IProgress<Nb0ExtractionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        var result = new ExtractionResult();
        var metadata = await Nb0Parser.ParseFromStreamAsync(stream, cancellationToken).ConfigureAwait(false);

        result.Warnings.AddRange(metadata.Warnings);
        Directory.CreateDirectory(outputDirectory);

        var total = metadata.Entries.Count;
        var completed = 0;

        for (var i = 0; i < metadata.Entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = metadata.Entries[i];
            if (options?.FileFilter is not null && !options.FileFilter(entry.Name))
            {
                completed++;
                continue;
            }

            try
            {
                var safeName = SanitizeEntryName(entry.Name);
                var outPath = Path.Combine(outputDirectory, safeName);
                if (File.Exists(outPath) && options?.Overwrite != true)
                {
                    completed++;
                    continue;
                }

                var tempPath = CreateTempPath(outPath);
                try
                {
                    var md5 = await ExtractEntryToFileAsync(stream, entry, tempPath, cancellationToken).ConfigureAwait(false);
                    VerifyComputedMd5(entry, i, md5, metadata);
                    File.Move(tempPath, outPath, overwrite: true);
                    result.ExtractedFileCount++;
                }
                finally
                {
                    File.Delete(tempPath);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to extract '{entry.Name}': {ex.Message}");
            }

            completed++;
            progress?.Report(new Nb0ExtractionProgress { TotalEntries = total, CompletedEntries = completed, CurrentEntryName = entry.Name });
        }

        return result;
    }

    /// <summary>
    /// Streams a single entry to the target file in chunks, incrementally computing its MD5.
    /// <para>将单个条目以分块流式方式复制到目标文件，同时增量计算 MD5 摘要。</para>
    /// </summary>
    /// <param name="stream">
    /// NB0 data source; it must be readable and seekable because the entry start is set as an absolute position, and its
    /// position is left at the end of the entry data.
    /// <para>NB0 数据源；必须可读且可寻址，因为条目起始位置是按绝对位置设置的，返回后流位置停在条目数据末尾。</para>
    /// </param>
    /// <param name="entry">
    /// Entry describing the name, size and offset of the data to copy; the range is validated before any byte is written.
    /// <para>描述待复制数据的名称、大小与偏移的条目；写入任何字节之前先校验该范围。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination file. Parent directories are created, a symbolic link or reparse point anywhere on the path is rejected,
    /// and the content lands through a temporary file that is moved into place, so the destination is never left partial.
    /// <para>目标文件。会创建父目录，路径上任何位置的符号链接或重解析点都会被拒绝，内容先写入临时文件再移动到位，因此目标文件不会出现半成品。</para>
    /// </param>
    /// <returns>
    /// The raw 16-byte MD5 digest of the extracted bytes; the value is computed while copying and is not compared against
    /// the archive records by this method.
    /// <para>提取字节的原始 16 字节 MD5 摘要；该值在复制过程中计算，本方法不会将其与归档记录比对。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="outputPath"/> is <see langword="null"/> or empty. <para><paramref name="outputPath"/> 为 <see langword="null"/> 或空字符串。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> or <paramref name="entry"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 或 <paramref name="entry"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="entry"/> carries a negative offset or size. <para><paramref name="entry"/> 的偏移或大小为负数。</para>
    /// </exception>
    /// <exception cref="Nb0CorruptedException">
    /// The entry range overflows <see cref="long"/> or extends past the end of a seekable stream.
    /// <para>条目范围使 <see cref="long"/> 溢出，或在可寻址流上越过末尾。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// <paramref name="outputPath"/> resolves through a symbolic link or reparse point.
    /// <para><paramref name="outputPath"/> 的路径经过符号链接或重解析点。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// The stream ends before <paramref name="entry"/> declares, leaving the entry partially read.
    /// <para>流提前结束，条目只读到一部分。</para>
    /// </exception>
    public byte[] ExtractEntryToFile(Stream stream, Nb0FileEntry entry, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        byte[]? hash = null;
        FileExtraction.WriteAtomic(outputPath, output => hash = ExtractEntryCore(stream, entry, output));
        return hash!;
    }

    private byte[] ExtractEntryCore(Stream stream, Nb0FileEntry entry, Stream output)
    {
        var start = ValidateEntryRange(stream, entry);

        stream.Position = start;
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        try
        {
            var remaining = entry.Size;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = stream.Read(buffer, 0, toRead);
                if (read == 0)
                {
                    throw new EndOfStreamException($"Entry '{entry.Name}' ended unexpectedly after {entry.Size - remaining} of {entry.Size} bytes.");
                }

                output.Write(buffer, 0, read);
                md5.AppendData(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        output.Flush();
        return md5.GetHashAndReset();
    }

    /// <summary>
    /// Asynchronously streams a single entry to the target file in chunks while incrementally computing its MD5.
    /// <para>异步将单个条目以分块流式方式复制到目标文件，同时增量计算 MD5 摘要。</para>
    /// </summary>
    /// <param name="stream">
    /// NB0 data source; it must support both seeking and async reads, and its position is left at the end of the entry data.
    /// <para>NB0 数据源；必须支持寻址与异步读取，返回后流位置停在条目数据末尾。</para>
    /// </param>
    /// <param name="entry">
    /// Entry describing the range to copy; offset, size and stream bounds are validated first.
    /// <para>描述待复制范围的条目；先校验偏移、大小与流边界。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination file, committed by moving a temporary sibling into place; cancellation leaves no file at this path.
    /// <para>目标文件，通过把同目录临时文件移动到位提交；被取消时该路径不会留下文件。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before the write starts and observed by every read, write and flush.
    /// <para>写入开始前检查，且每次读、写、刷盘都会观察该令牌。</para>
    /// </param>
    /// <returns>
    /// A task whose result is the raw 16-byte MD5 digest of the extracted bytes, computed without consulting the archive
    /// records.
    /// <para>返回的任务结果为提取字节的原始 16 字节 MD5 摘要，计算过程不参考归档记录。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="outputPath"/> is <see langword="null"/> or empty.
    /// <para><paramref name="outputPath"/> 为 <see langword="null"/> 或空字符串。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> or <paramref name="entry"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 或 <paramref name="entry"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="entry"/> carries a negative offset or size. <para><paramref name="entry"/> 的偏移或大小为负数。</para>
    /// </exception>
    /// <exception cref="Nb0CorruptedException">
    /// The entry range overflows <see cref="long"/> or extends past the end of a seekable stream.
    /// <para>条目范围使 <see cref="long"/> 溢出，或在可寻址流上越过末尾。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// <paramref name="outputPath"/> resolves through a symbolic link or reparse point.
    /// <para><paramref name="outputPath"/> 的路径经过符号链接或重解析点。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// The stream ends before the declared entry size is reached. <para>流在达到声明的条目大小之前结束。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signalled. <para><paramref name="cancellationToken"/> 已被取消。</para>
    /// </exception>
    public async Task<byte[]> ExtractEntryToFileAsync(Stream stream, Nb0FileEntry entry, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        byte[]? hash = null;
        await FileExtraction.WriteAtomicAsync(outputPath, async output =>
            hash = await ExtractEntryCoreAsync(stream, entry, output, cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return hash!;
    }

    private async Task<byte[]> ExtractEntryCoreAsync(Stream stream, Nb0FileEntry entry, Stream output,
        CancellationToken cancellationToken)
    {
        var start = ValidateEntryRange(stream, entry);

        stream.Position = start;
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        try
        {
            var remaining = entry.Size;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException($"Entry '{entry.Name}' ended unexpectedly after {entry.Size - remaining} of {entry.Size} bytes.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                md5.AppendData(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return md5.GetHashAndReset();
    }

    /// <summary>
    /// Extracts a single entry into a byte buffer.
    /// <para>将单个条目提取到字节缓冲区。</para>
    /// </summary>
    /// <param name="stream">
    /// NB0 data source; it must be readable and seekable, and its position is left at the end of the entry data.
    /// <para>NB0 数据源；必须可读且可寻址，返回后流位置停在条目数据末尾。</para>
    /// </param>
    /// <param name="entry">
    /// Entry whose declared size determines how many bytes are allocated and read; no chunking or hashing is applied.
    /// <para>以其声明大小决定分配并读取多少字节的条目；不进行分块，也不计算摘要。</para>
    /// </param>
    /// <returns>
    /// A new array holding exactly <paramref name="entry"/> bytes, sized from the archive metadata and therefore fully
    /// resident in memory.
    /// <para>新分配的数组，恰好包含 <paramref name="entry"/> 声明的字节数；大小取自归档元数据，整体驻留内存。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> or <paramref name="entry"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 或 <paramref name="entry"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="entry"/> carries a negative offset or size. <para><paramref name="entry"/> 的偏移或大小为负数。</para>
    /// </exception>
    /// <exception cref="Nb0CorruptedException">
    /// The entry range overflows <see cref="long"/> or extends past the end of a seekable stream.
    /// <para>条目范围使 <see cref="long"/> 溢出，或在可寻址流上越过末尾。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// The stream ends before the declared entry size is reached. <para>流在达到声明的条目大小之前结束。</para>
    /// </exception>
    public byte[] ExtractEntryToBuffer(Stream stream, Nb0FileEntry entry)
    {
        var start = ValidateEntryRange(stream, entry);

        stream.Position = start;
        var data = new byte[entry.Size];
        stream.ReadExactly(data);
        return data;
    }

    /// <summary>
    /// Asynchronously extracts a single entry into a byte buffer.
    /// <para>异步将单个条目提取到字节缓冲区。</para>
    /// </summary>
    /// <param name="stream">
    /// NB0 data source; it must support seeking and async reads, and its position is left at the end of the entry data.
    /// <para>NB0 数据源；必须支持寻址与异步读取，返回后流位置停在条目数据末尾。</para>
    /// </param>
    /// <param name="entry">
    /// Entry whose declared size determines the allocation; the whole buffer is read before the task completes.
    /// <para>其声明大小决定分配量的条目；任务完成前会读满整个缓冲区。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Observed by the underlying read; a whole entry stays in memory either way, so cancellation cannot bound the
    /// allocation, only the reading.
    /// <para>由底层读取观察；整个条目始终驻留内存，因此取消只能中止读取，无法限制分配量。</para>
    /// </param>
    /// <returns>
    /// A task whose result is a new array holding exactly <paramref name="entry"/> bytes.
    /// <para>返回的任务结果为新分配的数组，恰好包含 <paramref name="entry"/> 声明的字节数。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> or <paramref name="entry"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 或 <paramref name="entry"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="entry"/> carries a negative offset or size. <para><paramref name="entry"/> 的偏移或大小为负数。</para>
    /// </exception>
    /// <exception cref="Nb0CorruptedException">
    /// The entry range overflows <see cref="long"/> or extends past the end of a seekable stream.
    /// <para>条目范围使 <see cref="long"/> 溢出，或在可寻址流上越过末尾。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// The stream ends before the declared entry size is reached. <para>流在达到声明的条目大小之前结束。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signalled. <para><paramref name="cancellationToken"/> 已被取消。</para>
    /// </exception>
    public async Task<byte[]> ExtractEntryToBufferAsync(Stream stream, Nb0FileEntry entry, CancellationToken cancellationToken = default)
    {
        var start = ValidateEntryRange(stream, entry);

        stream.Position = start;
        var data = new byte[entry.Size];
        await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        return data;
    }

    private static string SanitizeEntryName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    /// <summary>
    /// Validates the entry range: offset &lt;= length and size &lt;= length - offset, rejecting ranges that exceed the stream length.
    /// <para>校验条目范围：offset &lt;= 长度 且 size &lt;= 长度 - offset，超出流长度时拒绝。</para>
    /// </summary>
    private static long ValidateEntryRange(Stream stream, Nb0FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.FileDataOffset < 0 || entry.Offset < 0 || entry.Size < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), $"Entry '{entry.Name}' has a negative offset or size.");
        }

        long start;
        try { start = checked(entry.FileDataOffset + entry.Offset); }
        catch (OverflowException ex)
        {
            throw new Nb0CorruptedException($"Entry '{entry.Name}' start offset overflows Int64.", ex);
        }
        if (stream.CanSeek && (start > stream.Length || entry.Size > stream.Length - start))
        {
            throw new Nb0CorruptedException(
                $"Entry '{entry.Name}' range (offset 0x{start:X}, size {entry.Size}) exceeds the stream length {stream.Length}.");
        }
        return start;
    }

    /// <summary>
    /// Compares the incrementally computed MD5 against the archive's MD5 records and adds a warning on mismatch.
    /// <para>将提取时增量计算的 MD5 与归档内 MD5 记录比对，不一致时添加警告。</para>
    /// </summary>
    private static string CreateTempPath(string outputPath) =>
        Path.Combine(Path.GetDirectoryName(outputPath)!, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");

    private static void VerifyComputedMd5(Nb0FileEntry entry, int entryIndex, byte[] computedMd5, Nb0Metadata metadata)
    {
        if (metadata.Md5Records.Count == 0)
        {
            return;
        }

        var record = Nb0Md5Helper.FindMd5Record(entry, entryIndex, metadata);
        if (record is not null && !Nb0Md5Helper.Md5Equals(computedMd5, record.Md5Checksum))
        {
            throw new InvalidDataException($"MD5 mismatch for entry '{entry.Name}'.");
        }
    }
}