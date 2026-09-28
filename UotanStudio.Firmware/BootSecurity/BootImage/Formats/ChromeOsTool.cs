using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.BootSecurity.BootImage.Formats;

/// <summary>
/// Recognises ChromeOS kernel images by their leading ASCII magic.
/// <para>依据开头的 ASCII 魔数识别 ChromeOS 内核镜像。</para>
/// </summary>
internal static class ChromeOsTool
{
    private const string ChromeOsMagic = "CHROMEOS";
    private const int KeyblockHeaderSize = 112;
    private const int SignatureDescriptorSize = 24;
    private const int PackedKeyHeaderSize = 32;

    /// <summary>Legacy compatibility probe size retained for existing callers; it is not a format boundary.
    /// <para>为现有调用方保留的兼容探测长度；它不是格式边界。</para></summary>
    public const int SignatureBlockSize = 0x3000;

    /// <summary>Legacy compatibility probe size retained for existing callers; it is not a vblock boundary.
    /// <para>为现有调用方保留的兼容探测长度；它不是 vblock 边界。</para></summary>
    public const int VblockSize = 0x2000;
    private const string LogoMagic = "LOGO!!!!";
    private const string SplashMagic = "SPLASH!!";
    private const string RockchipResourceMagic = "RSCE";
    private const int RockchipHeaderSize = 512;
    private const int RockchipEntrySize = 512;
    private const int RockchipBlockSize = 512;
    private const uint MaximumRockchipEntries = 65_536;

    /// <summary>Known read-only boot resource signatures.
    /// <para>已知的只读启动资源魔数。</para></summary>
    internal enum BootResourceType
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

    /// <summary>
    /// Parses a complete vboot2 keyblock and validates every declared range before publishing metadata.
    /// <para>解析完整的 vboot2 keyblock，并在发布元数据前校验所有声明范围。</para>
    /// </summary>
    /// <param name="data">Bytes beginning at the keyblock magic and containing the declared keyblock.
    /// <para>从 keyblock 魔数开始、并包含声明的完整 keyblock 的字节。</para></param>
    /// <returns>Read-only keyblock metadata; key bytes and signatures remain opaque.
    /// <para>只读 keyblock 元数据；密钥字节和签名保持不透明。</para></returns>
    /// <exception cref="InvalidDataException">The header, declared size, or referenced range is invalid.
    /// <para>头部、声明长度或引用范围非法。</para></exception>
    public static ChromeOsKeyblockMetadata ParseKeyblock(ReadOnlySpan<byte> data)
    {
        if (data.Length < KeyblockHeaderSize || !data[..8].SequenceEqual("CHROMEOS"u8))
        {
            throw new InvalidDataException("ChromeOS keyblock header or magic is invalid.");
        }

        var headerVersionMajor = ReadUInt32(data, 8);
        if (headerVersionMajor != 2)
        {
            throw new InvalidDataException($"Unsupported ChromeOS keyblock version {headerVersionMajor}.");
        }

        var keyblockSize = ReadUInt32(data, 16);
        if (keyblockSize < KeyblockHeaderSize || keyblockSize > data.Length)
        {
            throw new InvalidDataException("ChromeOS keyblock size is outside the supplied data.");
        }

        var keyblockSignature = ReadSignature(data, 24);
        var keyblockHash = ReadSignature(data, 48);
        var dataKey = ReadPackedKey(data, 80);
        EnsureRange(keyblockSignature.Offset, keyblockSignature.Size, keyblockSize - 24, "keyblock signature");
        EnsureRange(keyblockHash.Offset, keyblockHash.Size, keyblockSize - 48, "keyblock hash");
        EnsureRange(dataKey.Offset, dataKey.Size, keyblockSize - 80, "data key");
        EnsureCoveredLength(keyblockSignature.DataSize, keyblockSize - 24, "keyblock signature data");
        EnsureCoveredLength(keyblockHash.DataSize, keyblockSize - 48, "keyblock hash data");

        return new ChromeOsKeyblockMetadata
        {
            HeaderVersionMajor = headerVersionMajor,
            HeaderVersionMinor = ReadUInt32(data, 12),
            KeyblockSize = keyblockSize,
            KeyblockSignature = keyblockSignature,
            KeyblockHash = keyblockHash,
            Flags = ReadUInt32(data, 72),
            DataKey = dataKey
        };
    }

    /// <summary>
    /// Parses a complete vboot2 kernel vblock consisting of a keyblock followed by a kernel preamble.
    /// <para>解析由 keyblock 和内核 preamble 组成的完整 vboot2 内核 vblock。</para>
    /// </summary>
    /// <param name="data">Bytes beginning at the keyblock magic and containing the declared preamble.
    /// <para>从 keyblock 魔数开始、并包含声明的完整 preamble 的字节。</para></param>
    /// <returns>Read-only vblock metadata without signature verification or kernel extraction.
    /// <para>不执行签名验证或内核提取的只读 vblock 元数据。</para></returns>
    /// <exception cref="InvalidDataException">The keyblock, preamble version, or preamble range is invalid.
    /// <para>keyblock、preamble 版本或 preamble 范围非法。</para></exception>
    public static ChromeOsVblockMetadata ParseVblock(ReadOnlySpan<byte> data)
    {
        var keyblock = ParseKeyblock(data);
        var preambleOffset = keyblock.KeyblockSize;
        if (data.Length - preambleOffset < 96)
        {
            throw new InvalidDataException("ChromeOS kernel preamble header is truncated.");
        }

        var preamble = data[(int)preambleOffset..];
        var preambleSize = ReadUInt32(preamble, 0);
        var preambleMajor = ReadUInt32(preamble, 32);
        var preambleMinor = ReadUInt32(preamble, 36);
        var requiredPreambleSize = GetPreambleHeaderSize(preambleMajor, preambleMinor);
        if (preambleSize < requiredPreambleSize || preambleSize > preamble.Length)
        {
            throw new InvalidDataException("ChromeOS kernel preamble size is outside the supplied data.");
        }

        var preambleSignature = ReadSignature(preamble, 8);
        var bodySignature = ReadSignature(preamble, 72);
        EnsureRange(preambleSignature.Offset, preambleSignature.Size, preambleSize - 8, "preamble signature");
        EnsureRange(bodySignature.Offset, bodySignature.Size, preambleSize - 72, "body signature");
        EnsureCoveredLength(preambleSignature.DataSize, preambleSize - 8, "preamble signature data");

        return new ChromeOsVblockMetadata
        {
            Keyblock = keyblock,
            PreambleOffset = preambleOffset,
            PreambleSize = preambleSize,
            PreambleVersionMajor = preambleMajor,
            PreambleVersionMinor = preambleMinor,
            KernelVersion = ReadUInt32(preamble, 40),
            BodyLoadAddress = ReadUInt64(preamble, 48),
            BootloaderAddress = ReadUInt64(preamble, 56),
            BootloaderSize = ReadUInt32(preamble, 64),
            PreambleSignature = preambleSignature,
            BodySignature = bodySignature,
            VmlinuzHeaderAddress = preambleMinor >= 1 ? ReadUInt64(preamble, 96) : 0,
            VmlinuzHeaderSize = preambleMinor >= 1 ? ReadUInt32(preamble, 104) : 0,
            Flags = preambleMinor >= 2 ? ReadUInt32(preamble, 112) : 0
        };
    }

    private static ChromeOsSignatureMetadata ReadSignature(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || data.Length - offset < SignatureDescriptorSize)
        {
            throw new InvalidDataException("ChromeOS signature descriptor is truncated.");
        }

        return new ChromeOsSignatureMetadata
        {
            Offset = ReadUInt32(data, offset),
            Size = ReadUInt32(data, offset + 8),
            DataSize = ReadUInt32(data, offset + 16)
        };
    }

    private static ChromeOsPackedKeyMetadata ReadPackedKey(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || data.Length - offset < PackedKeyHeaderSize)
        {
            throw new InvalidDataException("ChromeOS packed-key descriptor is truncated.");
        }

        return new ChromeOsPackedKeyMetadata
        {
            Offset = ReadUInt32(data, offset),
            Size = ReadUInt32(data, offset + 8),
            Algorithm = ReadUInt32(data, offset + 16),
            Version = ReadUInt32(data, offset + 24)
        };
    }

    private static uint GetPreambleHeaderSize(uint major, uint minor)
    {
        if (major != 2 || minor > 2)
        {
            throw new InvalidDataException($"Unsupported ChromeOS kernel preamble version {major}.{minor}.");
        }

        return minor switch
        {
            0 => 96,
            1 => 112,
            _ => 116
        };
    }

    private static void EnsureRange(uint offset, uint size, uint containerSize, string label)
    {
        if (offset > containerSize || size > containerSize - offset)
        {
            throw new InvalidDataException($"ChromeOS {label} range exceeds its container.");
        }
    }

    private static void EnsureCoveredLength(uint size, uint containerSize, string label)
    {
        if (size > containerSize)
        {
            throw new InvalidDataException($"ChromeOS {label} exceeds its container.");
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || data.Length - offset < sizeof(uint))
        {
            throw new InvalidDataException("ChromeOS structure field is truncated.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || data.Length - offset < sizeof(ulong))
        {
            throw new InvalidDataException("ChromeOS structure field is truncated.");
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
    }

    /// <summary>
    /// The kind of ChromeOS image reported by detection.
    /// <para>检测得出的 ChromeOS 镜像种类。</para>
    /// </summary>
    internal enum ChromeOsImageType
    {
        /// <summary>
        /// No ChromeOS magic was matched. <para>未匹配到 ChromeOS 魔数。</para>
        /// </summary>
        Unknown,

        /// <summary>A validated standalone legacy vboot keyblock.
        /// <para>经过校验的独立 legacy vboot keyblock。</para></summary>
        Keyblock,

        /// <summary>A validated legacy vboot kernel image with a keyblock, preamble and body.
        /// <para>包含 keyblock、preamble 和 body 且经过校验的 legacy vboot 内核镜像。</para></summary>
        Kernel,

        /// <summary>Reserved compatibility value; no fixed-size signature block is inferred.
        /// <para>保留的兼容枚举值；不会推断固定大小的签名块。</para></summary>
        SignatureBlock,

        /// <summary>A validated legacy vboot keyblock followed by a kernel preamble.
        /// <para>keyblock 后紧接内核 preamble 且经过校验的 legacy vboot vblock。</para></summary>
        Vblock,

        /// <summary>
        /// A ChromeOS image that is not a kernel image; <see cref="Detect"/> never assigns it.
        /// <para>非内核镜像的 ChromeOS 镜像；<see cref="Detect"/> 不会赋值此项。</para>
        /// </summary>
        Other
    }

    /// <summary>
    /// The verdict of a ChromeOS detection together with a human-readable label.
    /// <para>ChromeOS 检测结论及其可读标签。</para>
    /// </summary>
    public class ChromeOsDetectionResult
    {
        /// <summary>
        /// Set when the buffer is at least 64 bytes and opens with the <c>CHROMEOS</c> magic.
        /// <para>缓冲区不少于 64 字节且以 <c>CHROMEOS</c> 魔数开头时为真。</para>
        /// </summary>
        /// <value>
        /// The outcome of the magic test for the inspected buffer.
        /// <para>被检查缓冲区的魔数测试结果。</para>
        /// </value>
        public bool IsChromeOs { get; set; }

        /// <summary>
        /// A validated <see cref="ChromeOsImageType.Keyblock"/>, <see cref="ChromeOsImageType.Vblock"/>
        /// or <see cref="ChromeOsImageType.Kernel"/>; malformed candidates are unknown.
        /// <para>经过校验的 <see cref="ChromeOsImageType.Keyblock"/>、<see cref="ChromeOsImageType.Vblock"/>
        /// 或 <see cref="ChromeOsImageType.Kernel"/>；结构非法的候选输入为 unknown。</para>
        /// </summary>
        /// <value>
        /// The classification produced after bounded keyblock and, when present, preamble checks.
        /// <para>在有界校验 keyblock 以及存在时的 preamble 后得出的分类。</para>
        /// </value>
        public ChromeOsImageType ImageType { get; set; }

        /// <summary>Fixed boundary size recognized by the read-only classifier, when known.
        /// <para>只读分类器确认的固定边界大小，未知时为零。</para></summary>
        public int StructureSize { get; set; }

        /// <summary>
        /// Static label for the verdict: "ChromeOS Kernel Image" or "Not a ChromeOS image".
        /// <para>结论的固定标签：“ChromeOS Kernel Image” 或 “Not a ChromeOS image”。</para>
        /// </summary>
        /// <value>
        /// Text intended for display as is; it is never localized and never <see langword="null"/>.
        /// <para>按原样展示的文本；不做本地化处理，也永不为 <see langword="null"/>。</para>
        /// </value>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Failure detail for a candidate whose magic is present but whose bounded structure is invalid.
        /// <para>候选输入存在魔数但有界结构非法时的失败详情。</para>
        /// </summary>
        /// <value>
        /// A human-readable reason, or <see langword="null"/> when nothing went wrong.
        /// <para>人类可读的原因；未发生错误时为 <see langword="null"/>。</para>
        /// </value>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Checks whether the data is a ChromeOS image.
    /// <para>检测数据是否为 ChromeOS 镜像。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate bytes containing a complete keyblock, vblock or kernel image.
    /// <para>包含完整 keyblock、vblock 或内核镜像的候选字节。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="data"/> contains a bounded, structurally valid
    /// legacy vboot keyblock or a following kernel preamble.
    /// <para>当 <paramref name="data"/> 包含有界且结构有效的 legacy vboot keyblock 或后续内核
    /// preamble 时返回 <see langword="true"/>。</para>
    /// </returns>
    public static bool IsChromeOs(byte[] data) => data is not null && Detect(data).IsChromeOs;

    /// <summary>
    /// Detects a ChromeOS image.
    /// <para>检测 ChromeOS 镜像。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate bytes handed to <see cref="IsChromeOs"/>. <para>交给 <see cref="IsChromeOs"/> 的待检测字节。</para>
    /// </param>
    /// <returns>
    /// A populated result that is never <see langword="null"/>. A result is successful only after
    /// all declared keyblock and preamble ranges pass validation.
    /// <para>始终返回非 <see langword="null"/>。只有所有声明的 keyblock 和 preamble 范围通过校验时，
    /// 结果才表示成功。</para>
    /// </returns>
    public static ChromeOsDetectionResult Detect(byte[] data)
    {
        if (data is null)
        {
            return new ChromeOsDetectionResult
            {
                ImageType = ChromeOsImageType.Unknown,
                Description = "Not a ChromeOS image",
                ErrorMessage = "No data was supplied."
            };
        }

        if (!HasMagic(data, 0, ChromeOsMagic))
        {
            return new ChromeOsDetectionResult
            {
                ImageType = ChromeOsImageType.Unknown,
                Description = "Not a ChromeOS image"
            };
        }

        try
        {
            var keyblock = ParseKeyblock(data);
            if (data.LongLength == keyblock.KeyblockSize)
            {
                return CreateResult(
                    ChromeOsImageType.Keyblock,
                    checked((int)keyblock.KeyblockSize),
                    "ChromeOS keyblock structure recognized; signature verification is not implemented.");
            }

            var vblock = ParseVblock(data);
            var vblockEnd = checked((ulong)vblock.PreambleOffset + vblock.PreambleSize);
            var bodyEnd = checked(vblockEnd + vblock.BodySignature.DataSize);
            var hasBody = (ulong)data.LongLength >= bodyEnd;
            return CreateResult(
                hasBody ? ChromeOsImageType.Kernel : ChromeOsImageType.Vblock,
                checked((int)vblockEnd),
                hasBody
                    ? "ChromeOS keyblock, kernel preamble and bounded body structure recognized; signature verification is not implemented."
                    : "ChromeOS keyblock and kernel preamble structure recognized; kernel body is not present in the supplied data.");
        }
        catch (InvalidDataException ex)
        {
            return new ChromeOsDetectionResult
            {
                ImageType = ChromeOsImageType.Unknown,
                Description = "Not a structurally recognized ChromeOS image",
                ErrorMessage = ex.Message
            };
        }
    }

    private static ChromeOsDetectionResult CreateResult(
        ChromeOsImageType imageType, int structureSize, string description)
        => new()
        {
            IsChromeOs = true,
            ImageType = imageType,
            StructureSize = structureSize,
            Description = description
        };

    private static bool HasMagic(byte[] data, int offset, string magic)
        => offset >= 0 && offset <= data.Length - magic.Length &&
            Encoding.ASCII.GetString(data, offset, magic.Length) == magic;

    /// <summary>
    /// Extracts the kernel data. This entry point performs no extraction and rejects every call.
    /// <para>提取内核数据。此入口不执行任何提取，并拒绝所有调用。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate image buffer; it is not read before the call is rejected.
    /// <para>待处理的镜像缓冲区；在调用被拒绝前不会被读取。</para>
    /// </param>
    /// <returns>
    /// Nothing; the call always fails, so the declared kernel payload is never produced.
    /// <para>不返回任何内容；调用总是失败，因此不会产生所声明的内核数据。</para>
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// Always thrown. <para>始终抛出。</para>
    /// </exception>
    public static byte[]? ExtractKernel(byte[] data)
    {
        throw new NotSupportedException(
            "ChromeOS kernel extraction is not inlined in this simplified port.");
    }

    /// <summary>
    /// Verifies the signature. This entry point performs no verification and rejects every call.
    /// <para>验证签名。此入口不执行任何验证，并拒绝所有调用。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate image buffer; it is not read before the call is rejected.
    /// <para>待处理的镜像缓冲区；在调用被拒绝前不会被读取。</para>
    /// </param>
    /// <returns>
    /// Nothing; the call always fails, so no verdict is produced.
    /// <para>不返回任何内容；调用总是失败，因此不会给出结论。</para>
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// Always thrown. <para>始终抛出。</para>
    /// </exception>
    public static bool VerifySignature(byte[] data)
    {
        throw new NotSupportedException(
            "ChromeOS signature verification is not inlined in this simplified port.");
    }

    /// <summary>Describes one vboot2 signature descriptor.
    /// <para>描述一个 vboot2 签名描述符。</para></summary>
    public sealed class ChromeOsSignatureMetadata
    {
        /// <summary>Signature data offset relative to this descriptor.
        /// <para>相对于此描述符的签名数据偏移。</para></summary>
        public required uint Offset { get; init; }

        /// <summary>Signature data length in bytes.
        /// <para>签名数据长度（字节）。</para></summary>
        public required uint Size { get; init; }

        /// <summary>Length of the data covered by the signature descriptor.
        /// <para>签名描述符覆盖的数据长度。</para></summary>
        public required uint DataSize { get; init; }
    }

    /// <summary>Describes a vboot2 packed public-key header without decoding key bytes.
    /// <para>描述 vboot2 压缩公钥头，但不解码密钥字节。</para></summary>
    public sealed class ChromeOsPackedKeyMetadata
    {
        /// <summary>Key data offset relative to this packed-key descriptor.
        /// <para>相对于此压缩密钥描述符的密钥数据偏移。</para></summary>
        public required uint Offset { get; init; }

        /// <summary>Key data length in bytes.
        /// <para>密钥数据长度（字节）。</para></summary>
        public required uint Size { get; init; }

        /// <summary>vboot signature algorithm identifier.
        /// <para>vboot 签名算法标识。</para></summary>
        public required uint Algorithm { get; init; }

        /// <summary>Rollback key version.
        /// <para>回滚保护使用的密钥版本。</para></summary>
        public required uint Version { get; init; }
    }

    /// <summary>Validated read-only metadata for a vboot2 keyblock.
    /// <para>经过校验的 vboot2 keyblock 只读元数据。</para></summary>
    public sealed class ChromeOsKeyblockMetadata
    {
        /// <summary>Keyblock header major version.
        /// <para>keyblock 头部主版本。</para></summary>
        public required uint HeaderVersionMajor { get; init; }

        /// <summary>Keyblock header minor version.
        /// <para>keyblock 头部次版本。</para></summary>
        public required uint HeaderVersionMinor { get; init; }

        /// <summary>Total keyblock length including padding.
        /// <para>包含填充在内的 keyblock 总长度。</para></summary>
        public required uint KeyblockSize { get; init; }

        /// <summary>Signature descriptor for the keyblock, without cryptographic verification.
        /// <para>keyblock 签名描述符，但不执行密码学验证。</para></summary>
        public required ChromeOsSignatureMetadata KeyblockSignature { get; init; }

        /// <summary>Hash descriptor for the keyblock, without digest calculation.
        /// <para>keyblock 哈希描述符，但不计算摘要。</para></summary>
        public required ChromeOsSignatureMetadata KeyblockHash { get; init; }

        /// <summary>Keyblock flags.
        /// <para>keyblock 标志。</para></summary>
        public required uint Flags { get; init; }

        /// <summary>Packed key header used to verify the following data.
        /// <para>用于验证后续数据的压缩密钥头。</para></summary>
        public required ChromeOsPackedKeyMetadata DataKey { get; init; }
    }

    /// <summary>Validated read-only metadata for a vboot2 kernel vblock.
    /// <para>经过校验的 vboot2 内核 vblock 只读元数据。</para></summary>
    public sealed class ChromeOsVblockMetadata
    {
        /// <summary>Validated keyblock metadata at offset zero.
        /// <para>位于零偏移处的 keyblock 元数据。</para></summary>
        public required ChromeOsKeyblockMetadata Keyblock { get; init; }

        /// <summary>Kernel preamble offset, equal to <see cref="ChromeOsKeyblockMetadata.KeyblockSize"/>.
        /// <para>内核 preamble 偏移，等于 <see cref="ChromeOsKeyblockMetadata.KeyblockSize"/>。</para></summary>
        public required uint PreambleOffset { get; init; }

        /// <summary>Total kernel preamble length including padding.
        /// <para>包含填充在内的内核 preamble 总长度。</para></summary>
        public required uint PreambleSize { get; init; }

        /// <summary>Kernel preamble header major version.
        /// <para>内核 preamble 头部主版本。</para></summary>
        public required uint PreambleVersionMajor { get; init; }

        /// <summary>Kernel preamble header minor version.
        /// <para>内核 preamble 头部次版本。</para></summary>
        public required uint PreambleVersionMinor { get; init; }

        /// <summary>Rollback kernel version.
        /// <para>回滚保护使用的内核版本。</para></summary>
        public required uint KernelVersion { get; init; }

        /// <summary>Kernel body load address.
        /// <para>内核 body 加载地址。</para></summary>
        public required ulong BodyLoadAddress { get; init; }

        /// <summary>Bootloader address recorded in the preamble.
        /// <para>preamble 中记录的 bootloader 地址。</para></summary>
        public required ulong BootloaderAddress { get; init; }

        /// <summary>Bootloader length in bytes.
        /// <para>bootloader 长度（字节）。</para></summary>
        public required uint BootloaderSize { get; init; }

        /// <summary>Signature descriptor for the preamble.
        /// <para>preamble 签名描述符。</para></summary>
        public required ChromeOsSignatureMetadata PreambleSignature { get; init; }

        /// <summary>Signature descriptor for the kernel body.
        /// <para>内核 body 签名描述符。</para></summary>
        public required ChromeOsSignatureMetadata BodySignature { get; init; }

        /// <summary>vmlinuz header address for preamble version 2.1 and later.
        /// <para>preamble 2.1 及更高版本的 vmlinuz 头地址。</para></summary>
        public required ulong VmlinuzHeaderAddress { get; init; }

        /// <summary>vmlinuz header length for preamble version 2.1 and later.
        /// <para>preamble 2.1 及更高版本的 vmlinuz 头长度。</para></summary>
        public required uint VmlinuzHeaderSize { get; init; }

        /// <summary>Kernel image type flags for preamble version 2.2.
        /// <para>preamble 2.2 的内核镜像类型标志。</para></summary>
        public required uint Flags { get; init; }
    }

}