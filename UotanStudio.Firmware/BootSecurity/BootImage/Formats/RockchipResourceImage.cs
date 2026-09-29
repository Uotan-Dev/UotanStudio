using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.BootSecurity.BootImage.Formats;

/// <summary>
/// Classifies known startup resource images by their leading magic and parses, extracts and builds
/// Rockchip RSCE read-only resource containers.
/// <para>依据开头魔数分类已知启动资源镜像，并解析、提取与构建 Rockchip RSCE 只读资源容器。</para>
/// </summary>
public static class RockchipResourceImage
{
    private const string LogoMagic = "LOGO!!!!";
    private const string SplashMagic = "SPLASH!!";
    private const string RockchipResourceMagic = "RSCE";
    private const int RockchipHeaderSize = 512;
    private const int RockchipEntrySize = 512;
    private const int RockchipBlockSize = 512;
    private const uint MaximumRockchipEntries = 65_536;

    /// <summary>Known read-only boot resource signatures.
    /// <para>已知的只读启动资源魔数。</para></summary>
    public enum BootResourceType
    {
        /// <summary>No supported resource signature was found. <para>未发现支持的资源魔数。</para></summary>
        Unknown,

        /// <summary>LOGO!!!! resource image. <para>LOGO!!!! 资源镜像。</para></summary>
        Logo,

        /// <summary>SPLASH!! resource image. <para>SPLASH!! 资源镜像。</para></summary>
        Splash,

        /// <summary>Rockchip RSCE resource container. <para>Rockchip RSCE 资源容器。</para></summary>
        RockchipResource
    }

    /// <summary>Reports a known startup resource signature without parsing or extracting its payload.
    /// <para>报告已知启动资源魔数，但不解析或提取其负载。</para></summary>
    /// <param name="data">Candidate bytes. <para>待检测的候选字节。</para></param>
    /// <returns>The matched resource kind, or <see cref="BootResourceType.Unknown"/>.
    /// <para>匹配到的资源类型；否则返回 <see cref="BootResourceType.Unknown"/>。</para></returns>
    public static BootResourceType DetectResource(byte[]? data)
    {
        if (data is null)
        {
            return BootResourceType.Unknown;
        }

        if (data.Length >= 8 && Encoding.ASCII.GetString(data, 0, 8) == LogoMagic)
        {
            return BootResourceType.Logo;
        }

        if (data.Length >= 8 && Encoding.ASCII.GetString(data, 0, 8) == SplashMagic)
        {
            return BootResourceType.Splash;
        }

        return data.Length >= 4 && Encoding.ASCII.GetString(data, 0, 4) == RockchipResourceMagic
            ? BootResourceType.RockchipResource
            : BootResourceType.Unknown;
    }

    /// <summary>Describes one bounded file entry in an RSCE resource container.
    /// <para>描述 RSCE 资源容器中的一个有界文件条目。</para></summary>
    public sealed class RockchipResourceEntry
    {
        /// <summary>Entry name decoded from the fixed 256-byte name field.
        /// <para>从固定 256 字节名称字段解码出的条目名称。</para></summary>
        public required string Name { get; init; }

        /// <summary>Absolute byte offset of the entry data.
        /// <para>条目数据的绝对字节偏移。</para></summary>
        public required long Offset { get; init; }

        /// <summary>Declared entry data length in bytes.
        /// <para>条目声明的数据长度（字节）。</para></summary>
        public required long Size { get; init; }
    }

    /// <summary>Read-only RSCE header and directory metadata.
    /// <para>只读 RSCE 头部与目录元数据。</para></summary>
    public sealed class RockchipResourceMetadata
    {
        /// <summary>RSCE format version from the header.
        /// <para>头部中的 RSCE 格式版本。</para></summary>
        public required ushort Version { get; init; }

        /// <summary>RSCE file-table version from the header.
        /// <para>头部中的 RSCE 文件表版本。</para></summary>
        public required ushort FileTableVersion { get; init; }

        /// <summary>Declared number of directory entries.
        /// <para>声明的目录条目数量。</para></summary>
        public required uint FileCount { get; init; }

        /// <summary>Validated directory entries.
        /// <para>经过边界校验的目录条目。</para></summary>
        public required IReadOnlyList<RockchipResourceEntry> Entries { get; init; }

        /// <summary>Capability marker reporting that no inspected reference confirms checksum semantics for
        /// any RSCE field; it is a constant and not a result of validating the input.
        /// <para>能力标记：任何已查阅的参考资料均未确认 RSCE 任何字段的校验和语义；该值是常量，并非校验输入的结果。</para></summary>
        /// <value>
        /// The fixed text <c>NoChecksumConfirmed</c>.
        /// <para>固定文本 <c>NoChecksumConfirmed</c>。</para>
        /// </value>
        public string IntegrityStatus => "NoChecksumConfirmed";
    }

    /// <summary>
    /// Parses an RSCE header and fixed-size directory without decoding or writing resource payloads.
    /// <para>解析 RSCE 头部和定长目录，但不解码或写出资源负载。</para>
    /// </summary>
    /// <param name="data">Complete candidate RSCE bytes. <para>完整的 RSCE 候选字节。</para></param>
    /// <returns>Validated RSCE directory metadata. <para>经过校验的 RSCE 目录元数据。</para></returns>
    /// <exception cref="InvalidDataException">The header, directory, entry name, or entry range is malformed.
    /// <para>头部、目录、条目名称或条目范围结构非法。</para></exception>
    public static RockchipResourceMetadata ParseRockchipResource(ReadOnlySpan<byte> data)
    {
        if (data.Length < RockchipHeaderSize || !data[..4].SequenceEqual("RSCE"u8))
        {
            throw new InvalidDataException("RSCE header or magic is invalid.");
        }

        var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(data[12..16]);
        if (fileCount > MaximumRockchipEntries)
        {
            throw new InvalidDataException("RSCE file count exceeds the supported limit.");
        }

        var directoryEnd = checked((long)RockchipHeaderSize + checked((long)fileCount * RockchipEntrySize));
        if (directoryEnd > data.Length)
        {
            throw new InvalidDataException("RSCE directory is truncated.");
        }

        var entries = new List<RockchipResourceEntry>(checked((int)fileCount));
        for (var index = 0; index < fileCount; index++)
        {
            var entryOffset = checked(RockchipHeaderSize + checked((int)index * RockchipEntrySize));
            var entry = data.Slice(entryOffset, RockchipEntrySize);
            if (!entry[..4].SequenceEqual("ENTR"u8))
            {
                throw new InvalidDataException($"RSCE directory entry {index} has an invalid magic.");
            }

            var nameBytes = entry.Slice(4, 256);
            var nul = nameBytes.IndexOf((byte)0);
            var nameLength = nul < 0 ? nameBytes.Length : nul;
            if (nameLength == 0)
            {
                throw new InvalidDataException($"RSCE directory entry {index} has an empty name.");
            }

            var name = Encoding.UTF8.GetString(nameBytes[..nameLength]);
            if (name is "." or ".." || name.Contains('/') || name.Contains('\\'))
            {
                throw new InvalidDataException($"RSCE directory entry {index} has an unsafe name.");
            }

            var blockOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[260..264]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[264..268]);
            var payloadOffset = checked((long)blockOffset * RockchipBlockSize);
            if (payloadOffset < directoryEnd || payloadOffset > data.Length ||
                size > data.Length - payloadOffset)
            {
                throw new InvalidDataException($"RSCE directory entry {index} exceeds the container.");
            }

            entries.Add(new RockchipResourceEntry { Name = name, Offset = payloadOffset, Size = size });
        }

        return new RockchipResourceMetadata
        {
            Version = BinaryPrimitives.ReadUInt16LittleEndian(data[4..6]),
            FileTableVersion = BinaryPrimitives.ReadUInt16LittleEndian(data[6..8]),
            FileCount = fileCount,
            Entries = entries
        };
    }

    /// <summary>
    /// Reads RSCE metadata from a file without materializing the payload.
    /// <para>从文件读取 RSCE 元数据，但不将资源负载整体载入内存。</para>
    /// </summary>
    /// <param name="sourcePath">RSCE container path. <para>RSCE 容器路径。</para></param>
    /// <returns>Validated header and directory metadata. <para>经过验证的头部和目录元数据。</para></returns>
    public static RockchipResourceMetadata ReadRockchipResourceMetadata(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: RockchipBlockSize, FileOptions.SequentialScan);
        return ReadRockchipResourceMetadata(input);
    }

    /// <summary>
    /// Extracts RSCE entries with bounded streaming and atomic per-file publication.
    /// <para>以有界流式方式提取 RSCE 条目，并对每个文件进行原子发布。</para>
    /// </summary>
    /// <param name="sourcePath">RSCE container path. <para>RSCE 容器路径。</para></param>
    /// <param name="outputDirectory">Destination directory. <para>输出目录。</para></param>
    /// <param name="selectedEntries">Optional case-insensitive entry filter; null extracts all entries.
    /// <para>可选的不区分大小写条目过滤器；为 null 时提取全部条目。</para></param>
    /// <param name="overwrite">Whether existing destination files may be replaced.
    /// <para>是否允许替换已有目标文件。</para></param>
    /// <param name="cancellationToken">Cancellation token checked during directory reads and payload copies.
    /// <para>在读取目录和复制负载期间检查的取消令牌。</para></param>
    /// <returns>The number of committed files. <para>已提交文件数。</para></returns>
    public static int ExtractRockchipResource(
        string sourcePath,
        string outputDirectory,
        IReadOnlySet<string>? selectedEntries = null,
        bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: RockchipBlockSize, FileOptions.SequentialScan);
        var metadata = ReadRockchipResourceMetadata(input);
        var count = 0;
        foreach (var entry in metadata.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selectedEntries is not null && !selectedEntries.Contains(entry.Name))
                continue;

            var outputPath = FileExtraction.ResolvePath(outputDirectory, entry.Name);
            input.Position = entry.Offset;
            FileExtraction.WriteAtomic(outputPath, output =>
            {
                StreamCopy.CopyExactly(input, output, entry.Size, cancellationToken,
                    bufferSize: 128 * 1024);
            }, overwrite, cancellationToken);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Builds a canonical unsigned RSCE container from a flat directory.
    /// The output is canonical and unsigned; it claims no checksum and no byte fidelity with any
    /// original OEM package.
    /// <para>从扁平目录构建标准无签名 RSCE 容器。输出是标准且无签名的；不声明任何校验和，也不声明与任何 OEM 原包的字节保真。</para>
    /// </summary>
    /// <param name="inputDirectory">Directory containing resource files.
    /// <para>包含资源文件的目录。</para></param>
    /// <param name="outputPath">Destination RSCE path, published atomically.
    /// <para>以原子方式发布的目标 RSCE 路径。</para></param>
    /// <param name="cancellationToken">Cancellation token checked during planning and copying.
    /// <para>在规划和复制期间检查的取消令牌。</para></param>
    /// <returns>The number of entries written. <para>写入的条目数。</para></returns>
    public static int BuildRockchipResource(
        string inputDirectory,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(inputDirectory))
            throw new DirectoryNotFoundException(inputDirectory);

        var destination = Path.GetFullPath(outputPath);
        var files = Directory.EnumerateFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(Path.GetFileName(path), RepackMetadata.FileName, StringComparison.Ordinal))
            .Where(path => !string.Equals(Path.GetFullPath(path), destination,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .Select(path => new RockchipSourceFile(path, Path.GetFileName(path)))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        if (files.Length > MaximumRockchipEntries)
            throw new InvalidDataException("RSCE file count exceeds the supported limit.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payloadOffsets = new long[files.Length];
        var payloadOffset = checked((long)(files.Length + 1) * RockchipBlockSize);
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nameBytes = Encoding.UTF8.GetBytes(files[index].Name);
            if (nameBytes.Length == 0 || nameBytes.Length > 256 ||
                nameBytes.Any(static value => value == 0 || value < 0x20) ||
                files[index].Name is "." or ".." ||
                files[index].Name.Contains('/') || files[index].Name.Contains('\\') ||
                !names.Add(files[index].Name))
                throw new InvalidDataException($"RSCE entry name is not representable: '{files[index].Name}'.");

            var length = new FileInfo(files[index].Path).Length;
            if (length > uint.MaxValue)
                throw new InvalidDataException($"RSCE entry is larger than 4 GiB: {files[index].Name}.");
            if (payloadOffset / RockchipBlockSize > uint.MaxValue)
                throw new InvalidDataException("RSCE payload offset exceeds the 32-bit block limit.");
            payloadOffsets[index] = payloadOffset;
            payloadOffset = checked(payloadOffset + AlignToBlock(length));
        }

        FileExtraction.WriteAtomic(outputPath, output =>
        {
            var header = new byte[RockchipHeaderSize];
            "RSCE"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 0);
            header[8] = 1;
            header[9] = 1;
            header[10] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), checked((uint)files.Length));
            output.Write(header);

            for (var index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = new byte[RockchipEntrySize];
                "ENTR"u8.CopyTo(entry);
                Encoding.UTF8.GetBytes(files[index].Name).CopyTo(entry.AsSpan(4));
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(260),
                    checked((uint)(payloadOffsets[index] / RockchipBlockSize)));
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(264),
                    checked((uint)new FileInfo(files[index].Path).Length));
                output.Write(entry);
            }

            var buffer = new byte[128 * 1024];
            for (var index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Position = payloadOffsets[index];
                using var input = new FileStream(files[index].Path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, buffer.Length, options: FileOptions.SequentialScan);
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                }
            }
        }, cancellationToken: cancellationToken);

        return files.Length;
    }

    private static long AlignToBlock(long length)
        => checked((length + RockchipBlockSize - 1) / RockchipBlockSize * RockchipBlockSize);

    private static RockchipResourceMetadata ReadRockchipResourceMetadata(Stream input)
    {
        if (input.Length < RockchipHeaderSize)
            throw new InvalidDataException("RSCE header is truncated.");

        var header = new byte[RockchipHeaderSize];
        input.Position = 0;
        input.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual("RSCE"u8))
            throw new InvalidDataException("RSCE header or magic is invalid.");
        var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        if (fileCount > MaximumRockchipEntries)
            throw new InvalidDataException("RSCE file count exceeds the supported limit.");

        var directoryEnd = checked((long)RockchipHeaderSize +
            checked((long)fileCount * RockchipEntrySize));
        if (directoryEnd > input.Length)
            throw new InvalidDataException("RSCE directory is truncated.");

        var entries = new List<RockchipResourceEntry>(checked((int)fileCount));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryBuffer = new byte[RockchipEntrySize];
        for (var index = 0; index < fileCount; index++)
        {
            input.ReadExactly(entryBuffer);
            var entry = ParseRockchipResourceEntry(entryBuffer, checked((uint)index), directoryEnd, input.Length);
            if (!names.Add(entry.Name))
                throw new InvalidDataException($"RSCE directory contains duplicate entry '{entry.Name}'.");
            entries.Add(entry);
        }

        return new RockchipResourceMetadata
        {
            Version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2)),
            FileTableVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2)),
            FileCount = fileCount,
            Entries = entries
        };
    }

    private static RockchipResourceEntry ParseRockchipResourceEntry(
        ReadOnlySpan<byte> entry, uint index, long directoryEnd, long containerLength)
    {
        if (entry.Length < RockchipEntrySize || !entry[..4].SequenceEqual("ENTR"u8))
            throw new InvalidDataException($"RSCE directory entry {index} has an invalid magic.");

        var nameBytes = entry.Slice(4, 256);
        var nul = nameBytes.IndexOf((byte)0);
        var nameLength = nul < 0 ? nameBytes.Length : nul;
        if (nameLength == 0)
            throw new InvalidDataException($"RSCE directory entry {index} has an empty name.");

        var name = Encoding.UTF8.GetString(nameBytes[..nameLength]);
        if (name is "." or ".." || name.Contains('/') || name.Contains('\\') ||
            name.Any(char.IsControl))
            throw new InvalidDataException($"RSCE directory entry {index} has an unsafe name.");

        var blockOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[260..264]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[264..268]);
        var payloadOffset = checked((long)blockOffset * RockchipBlockSize);
        if (payloadOffset < directoryEnd || payloadOffset > containerLength ||
            size > containerLength - payloadOffset)
            throw new InvalidDataException($"RSCE directory entry {index} exceeds the container.");

        return new RockchipResourceEntry { Name = name, Offset = payloadOffset, Size = size };
    }

    private readonly record struct RockchipSourceFile(string Path, string Name);
}