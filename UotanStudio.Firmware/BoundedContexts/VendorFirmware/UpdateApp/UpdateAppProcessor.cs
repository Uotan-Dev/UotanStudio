using System.Text;
using System.Buffers.Binary;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.UpdateApp;

/// <summary>
/// One payload entry located inside an <c>update.app</c> container, described by offsets into the source file.
/// <para>位于 <c>update.app</c> 容器内的单个载荷条目，以在源文件中的偏移量描述。</para>
/// </summary>
public sealed class UpdateAppEntry
{
    /// <summary>Filesystem-safe lower-case name derived from the header, without the <c>.img</c> suffix used on output. <para>由头部字段推导出的文件系统安全小写名称，不含输出时追加的 <c>.img</c> 后缀。</para></summary>
    /// <value>The entry name; scanning skips a signature whose cleaned name comes out empty. <para>条目名称；扫描时会跳过清理后名称为空的签名。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute offset of the 4-byte entry magic inside the source file. <para>条目 4 字节魔数在源文件中的绝对偏移。</para></summary>
    /// <value>A non-negative offset into the container. <para>容器内的非负偏移。</para></value>
    public long HeaderOffset { get; set; }

    /// <summary>Declared header length; data begins at <c>HeaderOffset + HeaderSize</c>. <para>头部声明长度；数据起始于 <c>HeaderOffset + HeaderSize</c>。</para></summary>
    /// <value>At least <c>76</c>, since shorter declared headers are rejected while scanning. <para>至少为 <c>76</c>，更短的声明头部在扫描时会被拒绝。</para></value>
    public uint HeaderSize { get; set; }

    /// <summary>Declared payload length in bytes. <para>声明的载荷长度（字节）。</para></summary>
    /// <value>The byte count copied from <see cref="DataOffset"/>; <c>0</c> yields an empty output file. <para>从 <see cref="DataOffset"/> 复制的字节数；为 <c>0</c> 时输出空文件。</para></value>
    public uint FileSize { get; set; }

    /// <summary>Absolute offset of the first payload byte, equal to <c>HeaderOffset + HeaderSize</c>. <para>首个载荷字节的绝对偏移，等于 <c>HeaderOffset + HeaderSize</c>。</para></summary>
    /// <value>An offset that, together with <see cref="FileSize"/>, stays inside the container. <para>与 <see cref="FileSize"/> 相加仍位于容器范围内的偏移。</para></value>
    public long DataOffset { get; set; }

    /// <summary>Declared CRC block size, or zero when the entry uses the legacy unverified header form.
    /// <para>声明的 CRC 分块大小；旧式未校验头部时为零。</para></summary>
    /// <value>The positive payload block size used by the entry checksum table, when available.
    /// <para>可用时表示条目校验表使用的正数载荷分块大小。</para></value>
    public ushort BlockSize { get; set; }

    /// <summary>Per-block CRC16 values read from the canonical Huawei entry header.
    /// <para>从标准华为条目头部读取的逐块 CRC16 值。</para></summary>
    /// <value>An empty array means that this entry has no verifiable checksum table.
    /// <para>空数组表示条目没有可验证的校验表。</para></value>
    public ushort[] ChecksumTable { get; set; } = [];

    /// <summary>
    /// Optional caller-supplied SHA-256 hash for integrity verification.
    /// The canonical Huawei entry header has no proven SHA-256 field, so the parser does not populate this value.
    /// <para>从条目头部读取的可选 SHA-256 哈希，用于完整性校验。为 <see langword="null"/> 时跳过校验。</para>
    /// </summary>
    /// <value>
    /// The expected 32-byte digest, or <see langword="null"/> when no digest is available for the entry.
    /// <para>预期的 32 字节摘要；该条目无摘要时为 <see langword="null"/>。</para>
    /// </value>
    public byte[]? Sha256Hash { get; set; }
}

/// <summary>
/// Reader for the Huawei-style <c>update.app</c> container: entry scanning and extraction with atomic output commits.
/// <para>华为风格 <c>update.app</c> 容器的读取器：条目扫描与提取，输出采用原子提交。</para>
/// </summary>
public static class UpdateAppProcessor
{
    private const int CanonicalHeaderSize = 98;
    private const int HeaderChecksumOffset = 92;
    private const int BlockSizeOffset = 94;
    private const int ChecksumTableLimit = 1_048_576;
    /// <summary>The 4-byte entry signature <c>55 AA 5A A5</c>. <para>4 字节条目签名 <c>55 AA 5A A5</c>。</para></summary>
    public static readonly byte[] Magic = [0x55, 0xAA, 0x5A, 0xA5];

    /// <summary>The entry signature as a read-only span for scan loops that must not copy. <para>以只读跨度暴露的条目签名，供无需复制的扫描循环使用。</para></summary>
    /// <value>A span over the same bytes as <see cref="Magic"/>. <para>与 <see cref="Magic"/> 指向相同字节的跨度。</para></value>
    public static ReadOnlySpan<byte> MagicBytes => Magic;

    /// <summary>
    /// Enumerates all entries within an update.app file.
    /// <para>枚举 update.app 中的所有条目。</para>
    /// </summary>
    /// <param name="source">Path to the container; scanning stops at the last position where a 4-byte signature can start. <para>容器路径；扫描在最后一个可容纳 4 字节签名的位置之后停止。</para></param>
    /// <returns>
    /// Entries in file order, lazily read from a stream the iterator opens and owns. A missing file yields an empty sequence;
    /// a signature whose header or payload exceeds the file bounds aborts enumeration, and 4-byte blocks without the signature are skipped.
    /// <para>按文件顺序惰性返回条目，迭代器自行打开并持有底层流。文件不存在时返回空序列；头部或载荷超出文件边界的签名会中断枚举，不含签名的 4 字节块被跳过。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">An entry header is shorter than 76 bytes, or a header or payload range lies outside the file.
    /// <para>条目头部短于 76 字节，或头部/载荷范围超出文件。</para></exception>
    /// <exception cref="IOException">The container cannot be read. <para>无法读取容器。</para></exception>
    /// <example>
    /// <code>
    /// foreach (var entry in UpdateAppProcessor.GetEntries(@"F:\OTA\update.app"))
    ///     Console.WriteLine(entry.Name + " " + entry.DataOffset + " " + entry.FileSize);
    /// </code>
    /// </example>
    public static IEnumerable<UpdateAppEntry> GetEntries(string source)
    {
        if (!File.Exists(source))
        {
            yield break;
        }

        using var fs = new FileStream(source, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);

        while (fs.Position + 4 <= fs.Length)
        {
            var magicBytes = br.ReadBytes(4);
            if (!magicBytes.SequenceEqual(Magic))
            {
                continue;
            }

            var headerStart = fs.Position - 4;
            FileExtraction.ValidateRange(headerStart, 76, fs.Length, "UPDATE.APP entry header");
            var headerSize = br.ReadUInt32();
            if (headerSize < 76)
                throw new InvalidDataException("UPDATE.APP entry header is too short.");
            FileExtraction.ValidateRange(headerStart, headerSize, fs.Length, "UPDATE.APP entry header");
            if (headerSize > int.MaxValue)
                throw new InvalidDataException("UPDATE.APP entry header is too large.");

            fs.Seek(headerStart, SeekOrigin.Begin);
            var header = br.ReadBytes(checked((int)headerSize));
            if (header.Length != headerSize)
                throw new EndOfStreamException("UPDATE.APP entry header ended unexpectedly.");

            var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
            var nameBytes = header.AsSpan(60, 16).ToArray();
            var filename = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
            filename = CleanFileName(filename).ToLowerInvariant();
            FileExtraction.ValidateRange(headerStart + headerSize, fileSize, fs.Length, "UPDATE.APP entry data");

            var blockSize = (ushort)0;
            ushort[] checksumTable = [];
            if (headerSize >= CanonicalHeaderSize &&
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(BlockSizeOffset)) != 0)
            {
                blockSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(BlockSizeOffset));
                var tableBytes = checked((int)headerSize - CanonicalHeaderSize);
                if ((tableBytes & 1) != 0)
                    throw new InvalidDataException("UPDATE.APP checksum table has an odd byte length.");
                var blockCount = blockSize == 0 || fileSize == 0
                    ? 0
                    : checked((int)(((ulong)fileSize + blockSize - 1) / blockSize));
                if (blockCount > ChecksumTableLimit || tableBytes / 2 != blockCount)
                    throw new InvalidDataException("UPDATE.APP checksum table length does not match the payload.");
                checksumTable = new ushort[blockCount];
                for (var index = 0; index < blockCount; index++)
                    checksumTable[index] = BinaryPrimitives.ReadUInt16LittleEndian(
                        header.AsSpan(CanonicalHeaderSize + index * 2));

                var expectedHeaderCrc = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(HeaderChecksumOffset));
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(HeaderChecksumOffset), 0);
                var actualHeaderCrc = ComputeCrc16(header);
                if (actualHeaderCrc != expectedHeaderCrc)
                    throw new InvalidDataException($"UPDATE.APP header CRC mismatch at 0x{headerStart:X}.");
            }

            if (string.IsNullOrEmpty(filename))
            {
                fs.Seek(headerStart + headerSize + fileSize, SeekOrigin.Begin);
                var rem = fs.Position % 4;
                if (rem != 0)
                {
                    fs.Seek(4 - rem, SeekOrigin.Current);
                }
                continue;
            }

            fs.Seek(headerStart + headerSize, SeekOrigin.Begin);

            var entry = new UpdateAppEntry
            {
                Name = filename,
                HeaderOffset = headerStart,
                HeaderSize = headerSize,
                FileSize = fileSize,
                DataOffset = fs.Position,
                BlockSize = blockSize,
                ChecksumTable = checksumTable
            };

            yield return entry;

            fs.Seek(fileSize, SeekOrigin.Current);

            var remainder = fs.Position % 4;
            if (remainder != 0)
            {
                fs.Seek(4 - remainder, SeekOrigin.Current);
            }
        }
    }

    /// <summary>
    /// Extracts entries from an update.app to the output directory.
    /// <para>将 update.app 中的条目提取到输出目录。</para>
    /// </summary>
    /// <param name="source">The update.app source path. <para>update.app 源路径。</para></param>
    /// <param name="outDir">The output directory. <para>输出目录。</para></param>
    /// <param name="fileList">The optional names to extract. <para>可选的待提取名称列表。</para></param>
    /// <param name="overwrite">Whether to replace existing files. <para>是否替换已有文件。</para></param>
    /// <param name="cancellationToken">The cancellation token. <para>取消令牌。</para></param>
    /// <returns>The committed entry count. <para>已提交的条目数。</para></returns>
    public static int Extract(string source, string outDir, List<string>? fileList = null,
        bool overwrite = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The update.app file does not exist!", source);
        }

        var extractedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        using var fs = new FileStream(source, FileMode.Open, FileAccess.Read);
        foreach (var entry in GetEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fileList is not null && fileList.Count > 0 &&
                !fileList.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var finalName = entry.Name;
            for (var suffix = 2; extractedFiles.Contains(finalName); suffix++)
                finalName = $"{entry.Name}_{suffix}";

            var outPath = FileExtraction.ResolvePath(outDir, $"{finalName}.img");
            fs.Seek(entry.DataOffset, SeekOrigin.Begin);
            FileExtraction.WriteAtomic(outPath, output =>
            {
                using var hashingStream = new HashingWriteStream(output, leaveOpen: true);
                if (entry.ChecksumTable.Length == 0)
                {
                    StreamCopy.CopyExactly(fs, hashingStream, entry.FileSize,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    var buffer = new byte[Math.Min(entry.BlockSize, 1024 * 1024)];
                    for (var block = 0; block < entry.ChecksumTable.Length; block++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var count = (int)Math.Min(buffer.Length,
                            entry.FileSize - (long)block * entry.BlockSize);
                        var remaining = count;
                        var offset = 0;
                        while (remaining > 0)
                        {
                            var read = fs.Read(buffer, offset, remaining);
                            if (read == 0)
                                throw new EndOfStreamException("UPDATE.APP payload ended unexpectedly.");
                            offset += read;
                            remaining -= read;
                        }
                        var actual = ComputeCrc16(buffer.AsSpan(0, count));
                        if (actual != entry.ChecksumTable[block])
                            throw new InvalidDataException($"UPDATE.APP payload CRC mismatch for '{entry.Name}' block {block}.");
                        hashingStream.Write(buffer, 0, count);
                    }
                }

                if (entry.Sha256Hash is not null)
                {
                    var computed = hashingStream.GetHashAndReset();
                    if (!computed.AsSpan().SequenceEqual(entry.Sha256Hash))
                    {
                        throw new InvalidDataException(
                            $"SHA-256 mismatch for entry '{entry.Name}'. Expected {Convert.ToHexString(entry.Sha256Hash)}, got {Convert.ToHexString(computed)}.");
                    }
                }
            }, overwrite);
            extractedFiles.Add(finalName);
            count++;
        }

        return count;
    }

    private static string CleanFileName(string name) => new([.. name.Where(c => c is >= ((char)32) and <= ((char)126))]);

    private static ushort ComputeCrc16(ReadOnlySpan<byte> bytes)
    {
        var crc = (ushort)0xFFFF;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1);
        }
        return (ushort)(crc ^ 0xFFFF);
    }
}
