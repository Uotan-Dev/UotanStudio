using System.Buffers;
using System.Text;

namespace UotanStudio.Firmware.Nb0;

/// <summary>
/// <para>提供将条目列表打包为 FIH Mobile NB0 固件文件的功能。</para>
/// Provides functionality to pack a list of entries into an FIH Mobile NB0 firmware file.
/// </summary>
public sealed class Nb0Packer
{
    private const int DefaultBufferSize = 1024 * 1024;

    /// <summary>
    /// <para>将指定条目列表打包为 NB0 固件文件。</para>
    /// Packs the specified entry list into an NB0 firmware file.
    /// </summary>
    public void Pack(string outputFilePath, IEnumerable<Nb0Entry> entries, IProgress<Nb0PackProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputFilePath);
        ArgumentNullException.ThrowIfNull(entries);

        using var stream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize);
        PackToStream(stream, entries, progress);
    }

    /// <summary>
    /// <para>异步将指定条目列表打包为 NB0 固件文件。</para>
    /// Asynchronously packs the specified entry list into an NB0 firmware file.
    /// </summary>
    public async Task PackAsync(string outputFilePath, IEnumerable<Nb0Entry> entries, IProgress<Nb0PackProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputFilePath);
        ArgumentNullException.ThrowIfNull(entries);

        using var stream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize, FileOptions.Asynchronous);
        await PackToStreamAsync(stream, entries, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>将指定条目列表打包为 NB0 格式并写入指定的流。</para>
    /// Packs the specified entry list into NB0 format and writes it to the specified stream.
    /// </summary>
    public void PackToStream(Stream stream, IEnumerable<Nb0Entry> entries, IProgress<Nb0PackProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);

        WriteNb0Core(stream, [.. entries], progress, CancellationToken.None);
    }

    /// <summary>
    /// <para>异步将指定条目列表打包为 NB0 格式并写入指定的流。</para>
    /// Asynchronously packs the specified entry list into NB0 format and writes it to the specified stream.
    /// </summary>
    public async Task PackToStreamAsync(Stream stream, IEnumerable<Nb0Entry> entries, IProgress<Nb0PackProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);

        var entryList = entries.ToList();
        await Task.Run(() => WriteNb0Core(stream, entryList, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>将指定目录中的所有文件打包为 NB0 固件文件(条目名使用相对路径)。</para>
    /// Packs all files in the specified directory into an NB0 firmware file (entry names use relative paths).
    /// </summary>
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
            var relative = Path.GetRelativePath(sourceDirectory, filePath).Replace('\\', '/');
            entries.Add(new Nb0Entry(relative, filePath));
        }

        var packer = new Nb0Packer();
        packer.Pack(outputFilePath, entries, progress);
    }

    /// <summary>
    /// <para>创建一个新的打包器构建器。</para>
    /// Creates a new packer builder.
    /// </summary>
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
            dataOffset = checked(dataOffset + entry.Header.FileSize);
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
                    // NB0 entries are 4-byte aligned.
                    var dataPad = (4 - (entry.Data.Length % 4)) % 4;
                    for (var i = 0; i < dataPad; i++)
                    {
                        writer.Write((byte)0);
                    }
                }
                else if (entry.FilePath is not null)
                {
                    // 从源文件分块流式复制，不再整文件读入内存。
                    // Stream the source file in chunks instead of loading it into memory.
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

                    // NB0 entries are 4-byte aligned.
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
/// <para>提供构建 NB0 打包条目的流式构建器。</para>
/// Provides a fluent builder for constructing NB0 pack entries.
/// </summary>
public sealed class Nb0PackerBuilder
{
    private readonly List<Nb0Entry> _entries = [];

    /// <summary>
    /// <para>添加一个内存数据条目。</para>
    /// Adds an in-memory data entry.
    /// </summary>
    public Nb0PackerBuilder AddEntry(string name, byte[] data)
    {
        _entries.Add(new Nb0Entry(name, data));
        return this;
    }

    /// <summary>
    /// <para>添加一个磁盘文件条目。</para>
    /// Adds a disk file entry.
    /// </summary>
    public Nb0PackerBuilder AddFile(string filePath, string? entryName = null)
    {
        var name = entryName ?? Path.GetFileName(filePath);
        _entries.Add(new Nb0Entry(name, filePath));
        return this;
    }

    /// <summary>
    /// <para>递归添加目录中的所有文件。</para>
    /// Recursively adds all files in a directory.
    /// </summary>
    public Nb0PackerBuilder AddDirectory(string directoryPath, string? prefix = null)
    {
        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(directoryPath, filePath).Replace('\\', '/');
            var name = string.IsNullOrEmpty(prefix) ? relative : $"{prefix}/{relative}";
            _entries.Add(new Nb0Entry(name, filePath));
        }
        return this;
    }

    /// <summary>
    /// <para>将构建的条目打包到输出文件。</para>
    /// Packs the built entries to the output file.
    /// </summary>
    public void Build(string outputFilePath, IProgress<Nb0PackProgress>? progress = null)
    {
        var packer = new Nb0Packer();
        packer.Pack(outputFilePath, _entries, progress);
    }
}