using System.Buffers;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Models;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Packing;

/// <summary>
/// Writes NB0 firmware containers: a little-endian uint32 entry count, a table of 64-byte entry
/// headers, then the data section with each entry padded to a 4-byte boundary. The packer assigns
/// each entry's <c>DataOffset</c> in the header table, mutating the supplied entry headers.
/// <para>写入 NB0 固件容器：小端 uint32 条目数、64 字节条目头表，随后是数据区，每个条目补齐到 4 字节边界。
/// 打包器会在头部表中分配各条目的 <c>DataOffset</c>，并修改传入的条目头。</para>
/// </summary>
public sealed class Nb0Packer
{
    private const int DefaultBufferSize = 1024 * 1024;

    /// <summary>
    /// Packs the specified entry list into an NB0 firmware file.
    /// <para>将指定条目列表打包为 NB0 固件文件。</para>
    /// </summary>
    /// <param name="outputFilePath">
    /// Destination path; an existing file is replaced (<c>FileMode.Create</c>).
    /// <para>目标路径；已存在的文件会被替换（<c>FileMode.Create</c>）。</para>
    /// </param>
    /// <param name="entries">
    /// Entries to write; each carries either in-memory data or a source file path.
    /// <para>要写入的条目；每个条目携带内存数据或源文件路径。</para>
    /// </param>
    /// <param name="progress">
    /// Receives a report after each entry is written; <see langword="null"/> disables reporting.
    /// <para>每写入一个条目后收到报告；为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="outputFilePath"/> is <see langword="null"/> or empty.
    /// <para><paramref name="outputFilePath"/> 为 <see langword="null"/> 或空。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entries"/> is <see langword="null"/>. <para><paramref name="entries"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// An entry's data length or source file length does not match its declared header size.
    /// <para>条目的数据长度或源文件长度与声明的头部大小不一致。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// A file-backed source ended before its declared size was read.
    /// <para>基于文件的源在达到声明大小前提前结束。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The output file or a source file cannot be opened or written.
    /// <para>无法打开或写入输出文件/源文件。</para>
    /// </exception>
    public void Pack(string outputFilePath, IEnumerable<Nb0Entry> entries, IProgress<Nb0PackProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputFilePath);
        ArgumentNullException.ThrowIfNull(entries);

        using var stream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize);
        PackToStream(stream, entries, progress);
    }

    /// <summary>
    /// Asynchronously packs the specified entry list into an NB0 firmware file.
    /// <para>异步将指定条目列表打包为 NB0 固件文件。</para>
    /// </summary>
    /// <param name="outputFilePath">
    /// Destination path; an existing file is replaced (<c>FileMode.Create</c>).
    /// <para>目标路径；已存在的文件会被替换（<c>FileMode.Create</c>）。</para>
    /// </param>
    /// <param name="entries">
    /// Entries to write; each carries either in-memory data or a source file path.
    /// <para>要写入的条目；每个条目携带内存数据或源文件路径。</para>
    /// </param>
    /// <param name="progress">
    /// Receives a report after each entry is written; <see langword="null"/> disables reporting.
    /// <para>每写入一个条目后收到报告；为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before each entry and between copy blocks; a partial output file is left in place.
    /// <para>在每个条目前及复制块之间检查；取消后可能残留不完整的输出文件。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="outputFilePath"/> is <see langword="null"/> or empty.
    /// <para><paramref name="outputFilePath"/> 为 <see langword="null"/> 或空。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entries"/> is <see langword="null"/>. <para><paramref name="entries"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// An entry's data length or source file length does not match its declared header size.
    /// <para>条目的数据长度或源文件长度与声明的头部大小不一致。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The output file or a source file cannot be opened or written.
    /// <para>无法打开或写入输出文件/源文件。</para>
    /// </exception>
    /// <returns>A task that completes once the container has been written.
    /// <para>在容器写入完成后结束的任务。</para></returns>
    public async Task PackAsync(string outputFilePath, IEnumerable<Nb0Entry> entries, IProgress<Nb0PackProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputFilePath);
        ArgumentNullException.ThrowIfNull(entries);

        using var stream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize, FileOptions.Asynchronous);
        await PackToStreamAsync(stream, entries, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Packs the specified entry list into NB0 format and writes it to the specified stream.
    /// <para>将指定条目列表打包为 NB0 格式并写入指定的流。</para>
    /// </summary>
    /// <param name="stream">
    /// Writable destination stream; it stays open after the call.
    /// <para>可写的目标流；调用结束后保持打开。</para>
    /// </param>
    /// <param name="entries">
    /// Entries to write; each carries either in-memory data or a source file path.
    /// <para>要写入的条目；每个条目携带内存数据或源文件路径。</para>
    /// </param>
    /// <param name="progress">
    /// Receives a report after each entry is written; <see langword="null"/> disables reporting.
    /// <para>每写入一个条目后收到报告；为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> or <paramref name="entries"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 或 <paramref name="entries"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="stream"/> does not support writing. <para><paramref name="stream"/> 不支持写入。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// An entry's data length or source file length does not match its declared header size.
    /// <para>条目的数据长度或源文件长度与声明的头部大小不一致。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// A source file cannot be opened or the stream write fails.
    /// <para>无法打开源文件或流写入失败。</para>
    /// </exception>
    public void PackToStream(Stream stream, IEnumerable<Nb0Entry> entries, IProgress<Nb0PackProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);

        WriteNb0Core(stream, [.. entries], progress, CancellationToken.None);
    }

    /// <summary>
    /// Asynchronously packs the specified entry list into NB0 format and writes it to the specified stream.
    /// <para>异步将指定条目列表打包为 NB0 格式并写入指定的流。</para>
    /// </summary>
    /// <param name="stream">
    /// Writable destination stream; it stays open after the call.
    /// <para>可写的目标流；调用结束后保持打开。</para>
    /// </param>
    /// <param name="entries">
    /// Entries to write; each carries either in-memory data or a source file path.
    /// <para>要写入的条目；每个条目携带内存数据或源文件路径。</para>
    /// </param>
    /// <param name="progress">
    /// Receives a report after each entry is written; <see langword="null"/> disables reporting.
    /// <para>每写入一个条目后收到报告；为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before each entry and between copy blocks; the writing core runs on the thread pool.
    /// <para>在每个条目前及复制块之间检查；写入核心在线程池上运行。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> or <paramref name="entries"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 或 <paramref name="entries"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="stream"/> does not support writing. <para><paramref name="stream"/> 不支持写入。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// An entry's data length or source file length does not match its declared header size.
    /// <para>条目的数据长度或源文件长度与声明的头部大小不一致。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发。</para>
    /// </exception>
    /// <returns>A task that completes once the container has been written to the stream.
    /// <para>在容器写入流完成后结束的任务。</para></returns>
    public async Task PackToStreamAsync(Stream stream, IEnumerable<Nb0Entry> entries, IProgress<Nb0PackProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);

        var entryList = entries.ToList();
        await Task.Run(() => WriteNb0Core(stream, entryList, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Packs all files in the specified directory into an NB0 firmware file (entry names use relative paths).
    /// Files are enumerated recursively in ordinal name order; repack metadata files are skipped and entry
    /// names use forward slashes.
    /// <para>将指定目录中的所有文件打包为 NB0 固件文件（条目名使用相对路径）。文件按名称序号顺序递归枚举；
    /// 跳过重打包元数据文件，条目名使用正斜杠。</para>
    /// </summary>
    /// <param name="sourceDirectory">
    /// Directory whose files become the container entries. <para>其文件将成为容器条目的目录。</para>
    /// </param>
    /// <param name="outputFilePath">
    /// Destination path; an existing file is replaced (<c>FileMode.Create</c>).
    /// <para>目标路径；已存在的文件会被替换（<c>FileMode.Create</c>）。</para>
    /// </param>
    /// <param name="progress">
    /// Receives a report after each entry is written; <see langword="null"/> disables reporting.
    /// <para>每写入一个条目后收到报告；为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourceDirectory"/> or <paramref name="outputFilePath"/> is <see langword="null"/> or empty.
    /// <para><paramref name="sourceDirectory"/> 或 <paramref name="outputFilePath"/> 为 <see langword="null"/> 或空。</para>
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">
    /// <paramref name="sourceDirectory"/> does not exist. <para><paramref name="sourceDirectory"/> 不存在。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A source file length does not match the size recorded in its entry header.
    /// <para>源文件长度与条目头中记录的大小不一致。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The output file or a source file cannot be opened or written.
    /// <para>无法打开或写入输出文件/源文件。</para>
    /// </exception>
    public static void PackFromDirectory(string sourceDirectory, string outputFilePath, IProgress<Nb0PackProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceDirectory);
        ArgumentException.ThrowIfNullOrEmpty(outputFilePath);
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");
        }

        var entries = new List<Nb0Entry>();
        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.IsMetadataFile(filePath)) continue;
            var relative = Path.GetRelativePath(sourceDirectory, filePath).Replace('\\', '/');
            entries.Add(new Nb0Entry(relative, filePath));
        }

        var packer = new Nb0Packer();
        packer.Pack(outputFilePath, entries, progress);
    }

    /// <summary>
    /// Creates a new packer builder.
    /// <para>创建一个新的打包器构建器。</para>
    /// </summary>
    /// <returns>An empty <see cref="Nb0PackerBuilder"/>.
    /// <para>一个空的 <see cref="Nb0PackerBuilder"/>。</para></returns>
    public static Nb0PackerBuilder CreateBuilder() => new();

    private static void WriteNb0Core(Stream stream, List<Nb0Entry> entryList, IProgress<Nb0PackProgress>? progress, CancellationToken cancellationToken)
    {
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write((uint)entryList.Count);

        long dataOffset = 0;
        foreach (var entry in entryList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry.Header.DataOffset = dataOffset;
            dataOffset = checked(dataOffset + entry.Header.FileSize + (4 - entry.Header.FileSize % 4) % 4);
        }

        foreach (var entry in entryList)
        {
            entry.Header.Write(writer);
        }

        var total = entryList.Count;
        var completed = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        try
        {
            foreach (var entry in entryList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Data is not null)
                {
                    if (entry.Data.LongLength != entry.Header.FileSize)
                    {
                        throw new InvalidDataException(
                            $"Entry '{entry.Header.Name}' data size ({entry.Data.LongLength}) does not match the declared header size ({entry.Header.FileSize}).");
                    }
                    writer.Write(entry.Data);

                    var dataPad = (4 - (entry.Data.Length % 4)) % 4;
                    for (var i = 0; i < dataPad; i++)
                    {
                        writer.Write((byte)0);
                    }
                }
                else if (entry.FilePath is not null)
                {


                    using var source = new FileStream(entry.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultBufferSize, FileOptions.SequentialScan);
                    if (source.Length != entry.Header.FileSize)
                    {
                        throw new InvalidDataException(
                            $"File '{entry.FilePath}' size ({source.Length}) does not match the declared entry size ({entry.Header.FileSize}).");
                    }

                    var remaining = entry.Header.FileSize;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var toRead = (int)Math.Min(buffer.Length, remaining);
                        var read = source.Read(buffer, 0, toRead);
                        if (read == 0)
                        {
                            throw new EndOfStreamException($"File '{entry.FilePath}' ended unexpectedly.");
                        }

                        writer.Write(buffer, 0, read);
                        remaining -= read;
                    }


                    var pad = (4 - (entry.Header.FileSize % 4)) % 4;
                    for (var i = 0; i < pad; i++)
                    {
                        writer.Write((byte)0);
                    }
                }
                else
                {
                    writer.Write(Array.Empty<byte>());
                }

                completed++;
                progress?.Report(new Nb0PackProgress { TotalEntries = total, CompletedEntries = completed, CurrentEntryName = entry.Header.Name });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// Fluent collector that accumulates in-memory and file-backed entries, then hands them to
/// <see cref="Nb0Packer"/> in one pass. Entries are written in the order they were added.
/// <para>流式收集器，累积内存条目与文件条目，然后一次性交给 <see cref="Nb0Packer"/>。条目按添加顺序写入。</para>
/// </summary>
/// <example>
/// <code>
/// Nb0Packer.CreateBuilder()
///     .AddDirectory(@"C:\staging", prefix: "images")
///     .AddEntry("version.txt", Encoding.ASCII.GetBytes("1.0"))
///     .Build(@"C:\out\package.nb0");
/// </code>
/// </example>
public sealed class Nb0PackerBuilder
{
    private readonly List<Nb0Entry> _entries = [];

    /// <summary>
    /// Adds an in-memory data entry.
    /// <para>添加一个内存数据条目。</para>
    /// </summary>
    /// <param name="name">
    /// Entry name stored in the header; ASCII-encoded and truncated to 47 characters when written.
    /// <para>存入头部的条目名；写入时按 ASCII 编码并截断到 47 个字符。</para>
    /// </param>
    /// <param name="data">
    /// Entry payload; the header size is taken from its length. <para>条目数据；头部大小取自其长度。</para>
    /// </param>
    /// <returns>This builder. <para>当前构建器。</para>
    /// </returns>
    public Nb0PackerBuilder AddEntry(string name, byte[] data)
    {
        _entries.Add(new Nb0Entry(name, data));
        return this;
    }

    /// <summary>
    /// Adds a disk file entry.
    /// <para>添加一个磁盘文件条目。</para>
    /// </summary>
    /// <param name="filePath">
    /// Source file; its current length is recorded as the entry size and the content is read at build time.
    /// <para>源文件；其当前长度记录为条目大小，内容在构建时读取。</para>
    /// </param>
    /// <param name="entryName">
    /// Name stored in the header; <see langword="null"/> uses the file's display name.
    /// <para>存入头部的名称；为 <see langword="null"/> 时使用文件显示名。</para>
    /// </param>
    /// <returns>This builder. <para>当前构建器。</para>
    /// </returns>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="filePath"/> does not exist. <para><paramref name="filePath"/> 不存在。</para>
    /// </exception>
    public Nb0PackerBuilder AddFile(string filePath, string? entryName = null)
    {
        var name = entryName ?? Path.GetFileName(filePath);
        _entries.Add(new Nb0Entry(name, filePath));
        return this;
    }

    /// <summary>
    /// Recursively adds all files in a directory.
    /// <para>递归添加目录中的所有文件。</para>
    /// </summary>
    /// <param name="directoryPath">
    /// Source directory; files are enumerated in ordinal name order and repack metadata files are skipped.
    /// <para>源目录；文件按名称序号顺序枚举，跳过重打包元数据文件。</para>
    /// </param>
    /// <param name="prefix">
    /// Optional leading path segment prepended with a forward slash to each relative entry name;
    /// <see langword="null"/> or empty adds no prefix.
    /// <para>可选的前导路径段，以正斜杠拼在每个相对条目名前；为 <see langword="null"/> 或空时不加前缀。</para>
    /// </param>
    /// <returns>This builder. <para>当前构建器。</para>
    /// </returns>
    /// <exception cref="DirectoryNotFoundException">
    /// <paramref name="directoryPath"/> does not exist. <para><paramref name="directoryPath"/> 不存在。</para>
    /// </exception>
    public Nb0PackerBuilder AddDirectory(string directoryPath, string? prefix = null)
    {
        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.IsMetadataFile(filePath)) continue;
            var relative = Path.GetRelativePath(directoryPath, filePath).Replace('\\', '/');
            var name = string.IsNullOrEmpty(prefix) ? relative : $"{prefix}/{relative}";
            _entries.Add(new Nb0Entry(name, filePath));
        }
        return this;
    }

    /// <summary>
    /// Packs the built entries to the output file.
    /// <para>将构建的条目打包到输出文件。</para>
    /// </summary>
    /// <param name="outputFilePath">
    /// Destination path; an existing file is replaced (<c>FileMode.Create</c>).
    /// <para>目标路径；已存在的文件会被替换（<c>FileMode.Create</c>）。</para>
    /// </param>
    /// <param name="progress">
    /// Receives a report after each entry is written; <see langword="null"/> disables reporting.
    /// <para>每写入一个条目后收到报告；为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="outputFilePath"/> is <see langword="null"/> or empty.
    /// <para><paramref name="outputFilePath"/> 为 <see langword="null"/> 或空。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A file-backed entry changed size between collection and packing.
    /// <para>基于文件的条目在收集与打包之间发生了大小变化。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The output file or a source file cannot be opened or written.
    /// <para>无法打开或写入输出文件/源文件。</para>
    /// </exception>
    public void Build(string outputFilePath, IProgress<Nb0PackProgress>? progress = null)
    {
        var packer = new Nb0Packer();
        packer.Pack(outputFilePath, _entries, progress);
    }
}