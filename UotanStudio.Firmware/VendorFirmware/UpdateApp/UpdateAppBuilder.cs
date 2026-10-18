using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Hash.Crc16CcittReversed;

namespace UotanStudio.Firmware.VendorFirmware.UpdateApp;

/// <summary>
/// Options for building a canonical unsigned Huawei-style <c>update.app</c> container.
/// <para>构建标准无签名华为风格 <c>update.app</c> 容器的选项。</para>
/// </summary>
public sealed class UpdateAppPackOptions
{
    /// <summary>Block size used for the per-block CRC16 table. <para>逐块 CRC16 表使用的分块大小。</para></summary>
    public ushort BlockSize { get; set; } = 4096;
}

/// <summary>
/// Builds the canonical unsigned entry form documented by mature Huawei update.app readers.
/// <para>构建已有成熟华为 update.app 读取器明确支持的标准无签名条目形式。</para>
/// </summary>
public static class UpdateAppBuilder
{
    private const int CanonicalHeaderSize = 98;
    private const int HeaderChecksumOffset = 92;
    private const int BlockSizeOffset = 94;
    private const int FileTypeOffset = 60;
    private const int FileTypeSize = 16;
    private const int CopyBufferSize = 128 * 1024;
    private const int MaximumEntries = 1024;
    private const int MaximumChecksumEntries = 1_048_576;

    /// <summary>Builds a flat-directory, canonical unsigned container and publishes it atomically.
    /// <para>从扁平目录构建标准无签名容器，并以原子方式发布输出。</para></summary>
    /// <param name="inputDirectory">Directory containing partition files, normally with an <c>.img</c> suffix.
    /// <para>包含分区文件的目录，通常使用 <c>.img</c> 后缀。</para></param>
    /// <param name="outputPath">Destination container path.<para>目标容器路径。</para></param>
    /// <param name="options">Writer options; <see langword="null"/> uses defaults.
    /// <para>写入选项；为 <see langword="null"/> 时使用默认值。</para></param>
    /// <param name="cancellationToken">Cancellation token checked during both passes.
    /// <para>在两次文件扫描和写入期间检查的取消令牌。</para></param>
    /// <returns>The number of entries written.<para>写入的条目数。</para></returns>
    /// <exception cref="DirectoryNotFoundException"><paramref name="inputDirectory"/> does not exist.
    /// <para><paramref name="inputDirectory"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The configured block size is zero, an entry name is not printable ASCII of at most the 16 bytes the file_type field stores, two entries share a name, an entry exceeds 4 GiB, or a checksum table would exceed its stored limit.
    /// <para>配置的分块大小为零、条目名不是 file_type 字段可存放的最多 16 字节可打印 ASCII、两个条目重名、 单个条目超过 4 GiB，或校验和表超出其存储上限。</para></exception>
    public static int Build(
        string inputDirectory,
        string outputPath,
        UpdateAppPackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(inputDirectory))
            throw new DirectoryNotFoundException(inputDirectory);

        options ??= new UpdateAppPackOptions();
        if (options.BlockSize == 0)
            throw new InvalidDataException("UPDATE.APP block size must be non-zero.");

        var destination = Path.GetFullPath(outputPath);
        var files = Directory.EnumerateFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(Path.GetFileName(path), RepackMetadata.FileName, StringComparison.Ordinal))
            .Where(path => !string.Equals(Path.GetFullPath(path), destination,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .Select(path => new SourceFile(path, GetEntryName(path)))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        if (files.Length > MaximumEntries)
            throw new InvalidDataException($"UPDATE.APP contains more than {MaximumEntries} entries.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new EntryPlan[files.Length];
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(files[index].Name))
                throw new InvalidDataException($"UPDATE.APP contains duplicate entry name '{files[index].Name}'.");

            var info = new FileInfo(files[index].Path);
            if (info.Length > uint.MaxValue)
                throw new InvalidDataException($"UPDATE.APP entry is larger than 4 GiB: {files[index].Name}.");
            var blockCount = info.Length == 0
                ? 0
                : checked((int)((info.Length + options.BlockSize - 1) / options.BlockSize));
            if (blockCount > MaximumChecksumEntries)
                throw new InvalidDataException($"UPDATE.APP checksum table is too large: {files[index].Name}.");

            var checksums = new ushort[blockCount];
            using var input = new FileStream(files[index].Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                CopyBufferSize, FileOptions.SequentialScan);
            var buffer = new byte[Math.Min(options.BlockSize, CopyBufferSize)];
            for (var block = 0; block < blockCount; block++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = ReadBlock(input, buffer, (long)block * options.BlockSize,
                    Math.Min(buffer.Length, checked((int)Math.Min(options.BlockSize, info.Length - (long)block * options.BlockSize))));
                checksums[block] = Crc16CcittReversed.Compute(buffer.AsSpan(0, count));
            }

            entries[index] = new EntryPlan(files[index].Path, files[index].Name,
                checked((uint)info.Length), checksums);
        }

        FileExtraction.WriteAtomic(outputPath, output =>
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteEntry(output, entry, options.BlockSize, cancellationToken);
            }
        }, cancellationToken: cancellationToken);
        return entries.Length;
    }

    private static void WriteEntry(Stream output, EntryPlan entry, ushort blockSize,
        CancellationToken cancellationToken)
    {
        var header = new byte[checked(CanonicalHeaderSize + entry.Checksums.Length * sizeof(ushort))];
        UpdateAppProcessor.Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), checked((uint)header.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), entry.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(BlockSizeOffset), blockSize);
        Encoding.ASCII.GetBytes(entry.Name).CopyTo(header, FileTypeOffset);
        for (var index = 0; index < entry.Checksums.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(
                header.AsSpan(CanonicalHeaderSize + index * sizeof(ushort)), entry.Checksums[index]);

        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(HeaderChecksumOffset), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(HeaderChecksumOffset),
            Crc16CcittReversed.Compute(header));
        output.Write(header);

        using var input = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, FileOptions.SequentialScan);
        var buffer = new byte[CopyBufferSize];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
        }

        var padding = (int)((4 - (output.Position & 3)) & 3);
        if (padding != 0)
            output.Write(new byte[padding]);
    }

    private static int ReadBlock(FileStream input, byte[] buffer, long offset, int count)
    {
        input.Position = offset;
        var total = 0;
        while (total < count)
        {
            var read = input.Read(buffer, total, count - total);
            if (read == 0)
                throw new EndOfStreamException("UPDATE.APP entry ended unexpectedly.");
            total += read;
        }
        return total;
    }

    private static string GetEntryName(string path)
    {
        var fileName = Path.GetFileName(path);
        var name = fileName.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\'))
        {
            throw new InvalidDataException(
                $"UPDATE.APP entry name is not representable: '{fileName}' gives no bare file-type name for the {FileTypeSize}-byte file_type field.");
        }

        foreach (var character in name)
        {
            if (character is < '\x20' or > '\x7E')
            {
                throw new InvalidDataException(
                    $"UPDATE.APP entry name is not representable: U+{(uint)character:X4} is outside the printable ASCII range stored by the {FileTypeSize}-byte file_type field: '{name}'.");
            }
        }

        var bytes = Encoding.ASCII.GetByteCount(name);
        if (bytes > FileTypeSize)
        {
            throw new InvalidDataException(
                $"UPDATE.APP entry name is not representable: {bytes} ASCII bytes exceed the {FileTypeSize}-byte file_type field: '{name}'.");
        }

        return name;
    }

    private readonly record struct SourceFile(string Path, string Name);
    private readonly record struct EntryPlan(string Path, string Name, uint Length, ushort[] Checksums);
}