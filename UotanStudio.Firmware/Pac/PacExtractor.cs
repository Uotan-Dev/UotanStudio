namespace UotanStudio.Firmware.Pac;

/// <summary>
/// <para>SPRD PAC 提取模式。</para>
/// Extraction modes for SPRD PAC files.
/// </summary>
public enum ExtractionMode
{
    /// <summary>仅列出条目信息。</summary>
    List = 0,

    /// <summary>提取文件。</summary>
    Extract = 1,

    /// <summary>校验 CRC。</summary>
    Check = 2
}

/// <summary>
/// <para>提供 SPRD PAC 固件包的文件列表与提取功能。</para>
/// Provides file listing and extraction functionality for SPRD PAC firmware packages.
/// </summary>
public static class PacExtractor
{
    private const int ChunkSize = 0x1000;
    private const uint ExpectedMagic = 0xFFFAFFFA;

    /// <summary>
    /// <para>列出 PAC 文件中的所有条目信息。</para>
    /// Lists all entry information within a PAC file.
    /// </summary>
    /// <returns>条目列表(名称、大小、偏移、地址等)。</returns>
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
    /// <para>将 PAC 文件中的所有条目提取到输出目录。</para>
    /// Extracts all entries from a PAC file to the output directory.
    /// </summary>
    public static int Extract(string imagePath, string outDir, Func<string, bool>? nameFilter = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        ArgumentException.ThrowIfNullOrEmpty(outDir);

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
            if ((ulong)file.PacOffset > (ulong)fs.Length ||
                (ulong)file.Size > (ulong)fs.Length - file.PacOffset)
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
    /// <para>校验 PAC 文件的头部 CRC 与数据区 CRC。</para>
    /// Verifies the head CRC and data CRC of a PAC file.
    /// </summary>
    /// <returns>(头部 CRC 是否匹配, 数据区 CRC 是否匹配)。</returns>
    public static (bool HeadOk, bool DataOk) Check(string imagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);

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
        if (totalSize < SprdHead.Size)
        {
            throw new InvalidDataException("unexpected pac size");
        }

        fs.Seek(SprdHead.Size, SeekOrigin.Begin);
        ushort calculatedDataCrc = 0;
        var buffer = new byte[ChunkSize];
        long totalRead = 0;
        while (totalRead < totalSize)
        {
            var toRead = (int)Math.Min(ChunkSize, totalSize - totalRead);
            var read = fs.Read(buffer, 0, toRead);
            if (read == 0)
            {
                break;
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