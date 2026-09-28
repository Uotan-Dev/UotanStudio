using UotanStudio.Firmware.VendorFirmware.Pac;
namespace UotanStudio.Firmware.VendorFirmware.Pac;

/// <summary>
/// Operation to run against a PAC image: enumerate entries, extract them, or verify CRC.
/// <para>针对 PAC 镜像执行的操作：枚举条目、提取条目或校验 CRC。</para>
/// </summary>
public enum ExtractionMode
{
    /// <summary>List the entries without writing any file. <para>仅列出条目，不写出任何文件。</para></summary>
    List = 0,

    /// <summary>Extract the selected entries to an output directory. <para>将所选条目提取到输出目录。</para></summary>
    Extract = 1,

    /// <summary>Verify the header and data CRC of the image. <para>校验镜像的头部与数据区 CRC。</para></summary>
    Check = 2
}

/// <summary>
/// Reads SPRD/Unisoc PAC firmware images: lists entries, extracts them to disk, and verifies header and data CRC.
/// <para>读取 SPRD/展锐 PAC 固件镜像：列出条目、将其提取到磁盘，并校验头部与数据区 CRC。</para>
/// </summary>
/// <remarks>
/// PAC files, their directory table and CRC fields are treated as untrusted input; every offset, length and count is bounded before use and extraction writes are staged through a temporary file and atomically moved.
/// <para>PAC 文件及其目录表与 CRC 字段均被视为不可信输入；每个偏移、长度和计数在使用前都会做上限校验，提取写出会经由临时文件暂存后原子移动。</para>
/// </remarks>
internal static class PacExtractor
{
    private const int ChunkSize = 0x1000;
    private const uint ExpectedMagic = 0xFFFAFFFA;

    /// <summary>
    /// Lists all entry information within a PAC file.
    /// <para>列出 PAC 文件中的所有条目信息。</para>
    /// </summary>
    /// <param name="imagePath">
    /// Path to the PAC image; must be a non-empty, existing file readable from the start.
    /// <para>PAC 镜像路径；必须是非空、存在且可从开头读取的文件。</para>
    /// </param>
    /// <returns>One tuple per declared file entry giving its name, size, offset in the image, type flag, address count and address array, in table order; entries are returned even when their data range is invalid.
    /// <para>每个声明的文件条目对应一个元组，按表顺序给出名称、大小、在镜像中的偏移、类型标志、地址数量与地址数组；即使数据范围无效条目仍会被返回。</para></returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="imagePath"/> is <see langword="null"/> or empty.
    /// <para><paramref name="imagePath"/> 为 <see langword="null"/> 或空。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The header magic, directory offset or file count fails validation, or a directory entry has an unexpected struct size.
    /// <para>头部魔数、目录偏移或文件数量未通过校验，或目录条目结构大小不符合预期。</para>
    /// </exception>
    public static List<(string Name, uint Size, uint Offset, uint Type, uint AddrNum, uint[] Addr)> List(string imagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);

        using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(fs);

        var head = SprdHead.Read(reader);
        ValidateHeader(head);

        var result = new List<(string, uint, uint, uint, uint, uint[])>();
        for (var i = 0; i < head.FileCount; i++)
        {
            fs.Seek(SprdHead.Size + (i * SprdFile.StructFixedSize), SeekOrigin.Begin);
            var file = SprdFile.Read(reader);
            if (file.StructSize != SprdFile.StructFixedSize)
            {
                throw new InvalidDataException("unexpected struct size");
            }
            result.Add((file.GetName(), file.Size, file.PacOffset, file.Type, file.AddrNum, file.Addr));
        }
        return result;
    }

    /// <summary>
    /// Extracts all entries from a PAC file to the output directory.
    /// <para>将 PAC 文件中的所有条目提取到输出目录。</para>
    /// </summary>
    /// <param name="imagePath">
    /// Path to the PAC image to read.
    /// <para>要读取的 PAC 镜像路径。</para>
    /// </param>
    /// <param name="outDir">
    /// Destination directory; created when absent. Each entry is written beneath it and never outside it.
    /// <para>目标目录；不存在时创建。每个条目都写入其下，绝不写到其外。</para>
    /// </param>
    /// <param name="nameFilter">
    /// Predicate deciding which entry names to extract, or <see langword="null"/> to extract every entry with a non-empty name, offset and size.
    /// <para>决定提取哪些条目名称的谓词；为 <see langword="null"/> 时提取所有名称、偏移与大小均非空的条目。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token checked before the run and between copy chunks; cancelling it aborts extraction.
    /// <para>在开始前及各复制块之间检查的令牌；取消它会中止提取。</para>
    /// </param>
    /// <returns>The number of entries written to disk. Entries with a zero name, offset or size are skipped and not counted.
    /// <para>实际写入磁盘的条目数。名称、偏移或大小为零的条目会被跳过且不计入。</para></returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="imagePath"/> or <paramref name="outDir"/> is <see langword="null"/> or empty.
    /// <para><paramref name="imagePath"/> 或 <paramref name="outDir"/> 为 <see langword="null"/> 或空。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A directory entry has an unexpected struct size, an invalid output name, no output directory, a range outside the image, or a path that escapes <paramref name="outDir"/>.
    /// <para>目录条目的结构大小不符合预期、输出名称无效、缺少输出目录、范围超出镜像，或路径逃逸出 <paramref name="outDir"/>。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// The image ends before an entry's declared size is fully read.
    /// <para>镜像在条目声明大小读取完成之前就已结束。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被发出取消信号。</para>
    /// </exception>
    public static int Extract(string imagePath, string outDir, Func<string, bool>? nameFilter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(outDir);
        var outputRoot = Path.GetFullPath(outDir);
        var rootPrefix = outputRoot.EndsWith(Path.DirectorySeparatorChar)
            ? outputRoot
            : outputRoot + Path.DirectorySeparatorChar;

        using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(fs);

        var head = SprdHead.Read(reader);
        ValidateHeader(head);

        var count = 0;
        for (var i = 0; i < head.FileCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fs.Seek(SprdHead.Size + (i * SprdFile.StructFixedSize), SeekOrigin.Begin);
            var file = SprdFile.Read(reader);

            if (file.StructSize != SprdFile.StructFixedSize)
            {
                throw new InvalidDataException("unexpected struct size");
            }

            if (file.Name[0] == 0 || file.PacOffset == 0 || file.Size == 0)
            {
                continue;
            }

            var fileName = file.GetName();
            if (nameFilter is not null && !nameFilter(fileName))
            {
                continue;
            }

            ValidateOutputName(fileName);
            var relativePath = fileName.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(relativePath, outputRoot);
            if (!fullPath.StartsWith(rootPrefix, PathComparison))
            {
                throw new InvalidDataException($"PAC entry escapes output directory: {fileName}");
            }
            if (file.PacOffset > (ulong)fs.Length ||
                file.Size > (ulong)fs.Length - file.PacOffset)
            {
                throw new InvalidDataException($"PAC entry range is outside the image: {fileName}");
            }

            fs.Seek(file.PacOffset, SeekOrigin.Begin);
            var parentDirectory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidDataException($"PAC entry has no output directory: {fileName}");
            Directory.CreateDirectory(parentDirectory);
            var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var fo = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    long remaining = file.Size;
                    var buffer = new byte[ChunkSize];
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var toRead = (int)Math.Min(ChunkSize, remaining);
                        var read = fs.Read(buffer, 0, toRead);
                        if (read == 0)
                        {
                            throw new EndOfStreamException($"Unexpected EOF extracting PAC entry: {fileName}");
                        }
                        fo.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
                File.Move(tempPath, fullPath, true);
                count++;
            }
            finally
            {
                File.Delete(tempPath);
            }
        }
        return count;
    }

    /// <summary>
    /// Verifies the head CRC and data CRC of a PAC file.
    /// <para>校验 PAC 文件的头部 CRC 与数据区 CRC。</para>
    /// </summary>
    /// <param name="imagePath">
    /// Path to the PAC image to verify.
    /// <para>要校验的 PAC 镜像路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token checked before the run and between data chunks; cancelling it aborts verification.
    /// <para>在开始前及各数据块之间检查的令牌；取消它会中止校验。</para>
    /// </param>
    /// <returns>A tuple reporting whether the header CRC matches the stored value and whether the CRC computed over the declared data region matches the stored value; the data scan stops early at the physical end of the stream.
    /// <para>元组分别报告头部 CRC 是否与存储值相符、按声明数据区计算的 CRC 是否与存储值相符；数据扫描会在流的物理末尾提前停止。</para></returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="imagePath"/> is <see langword="null"/> or empty.
    /// <para><paramref name="imagePath"/> 为 <see langword="null"/> 或空。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The header magic, directory offset or file count fails validation, or the declared PAC size is smaller than the header.
    /// <para>头部魔数、目录偏移或文件数量未通过校验，或声明的 PAC 大小小于头部。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被发出取消信号。</para>
    /// </exception>
    public static (bool HeadOk, bool DataOk) Check(string imagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        cancellationToken.ThrowIfCancellationRequested();

        using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(fs);

        var head = SprdHead.Read(reader);
        ValidateHeader(head);

        var headBytes = head.ToBytes();
        var headForCrc = new byte[headBytes.Length - 4];
        Array.Copy(headBytes, 0, headForCrc, 0, headForCrc.Length);
        var calculatedHeadCrc = Crc16.Calculate(0, headForCrc);
        var headOk = head.HeadCrc == calculatedHeadCrc;

        long totalSize = head.PacSize;
        if (totalSize < SprdHead.Size || totalSize > fs.Length)
        {
            throw new InvalidDataException("PAC declared size is outside the physical file.");
        }

        fs.Seek(SprdHead.Size, SeekOrigin.Begin);
        ushort calculatedDataCrc = 0;
        var buffer = new byte[ChunkSize];
        long totalRead = 0;
        var dataSize = totalSize - SprdHead.Size;
        while (totalRead < dataSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(ChunkSize, dataSize - totalRead);
            var read = fs.Read(buffer, 0, toRead);
            if (read == 0)
            {
                throw new EndOfStreamException("PAC data ended before the declared package size.");
            }
            calculatedDataCrc = Crc16.Calculate(calculatedDataCrc, read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray());
            totalRead += read;
        }

        return (headOk, head.DataCrc == calculatedDataCrc);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void ValidateOutputName(string name)
    {
        var hasPortableRoot = name.Length > 0 && (name[0] is '/' or '\\' ||
            (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':'));
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || hasPortableRoot || name.Contains('\0'))
        {
            throw new InvalidDataException($"Invalid PAC output name: {name}");
        }
    }

    private static void ValidateHeader(SprdHead head)
    {
        if (head.PacMagic != ExpectedMagic)
        {
            throw new InvalidDataException("Bad pac_magic!");
        }
        if (head.DirOffset != SprdHead.Size)
        {
            throw new InvalidDataException("unexpected directory offset");
        }
        if ((head.FileCount >> 10) != 0)
        {
            throw new InvalidDataException("too many files");
        }
    }
}