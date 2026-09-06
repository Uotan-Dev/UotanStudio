namespace UotanStudio.Firmware.Dz;

public sealed class DzChunk
{
    public required DzChunkHeader Header { get; init; }

    public long DataOffset { get; init; }

    public string Name => Header.GetChunkName();
}

public static class DzExtractor
{
    /// <summary>
    /// Parses a DZ file, returning the file header and chunk list.
    /// <para>解析 DZ 文件,返回文件头与块列表。</para>
    /// </summary>
    public static (DzFileHeader Header, List<DzChunk> Chunks) Parse(string dzPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dzPath);

        using var fs = new FileStream(dzPath, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);

        var dzSize = fs.Length;
        var magicBytes = br.ReadBytes(4);
        if (!magicBytes.SequenceEqual(DzConstants.DzFileHeader))
        {
            throw new InvalidDataException("Unsupported DZ file format.");
        }

        fs.Position = 0;
        var headerBuffer = br.ReadBytes(DzFileHeader.Size);
        var header = DzFileHeader.FromBytes(headerBuffer);

        var chunks = new List<DzChunk>();
        fs.Position = 512;
        while (true)
        {
            var currentPos = fs.Position;
            var buf = br.ReadBytes(512);
            if (buf.Length < 512)
            {
                break;
            }

            var chunk = DzChunkHeader.FromBytes(buf);
            if (!chunk.Header.AsSpan().SequenceEqual(DzConstants.DzChunkHeader))
            {
                break;
            }

            chunks.Add(new DzChunk { Header = chunk, DataOffset = fs.Position });

            if (fs.Position + chunk.DataSize >= dzSize)
            {
                break;
            }

            fs.Seek(chunk.DataSize, SeekOrigin.Current);
        }

        return (header, chunks);
    }

    /// <summary>
    /// Extracts all chunks from a DZ file to the output directory.
    /// <para>将 DZ 文件中的所有块提取到输出目录。</para>
    /// </summary>
    public static int Extract(string dzPath, string outDir, Func<string, bool>? nameFilter = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dzPath);
        ArgumentException.ThrowIfNullOrEmpty(outDir);

        Directory.CreateDirectory(outDir);

        using var fs = new FileStream(dzPath, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);

        var dzSize = fs.Length;
        var magicBytes = br.ReadBytes(4);
        if (!magicBytes.SequenceEqual(DzConstants.DzFileHeader))
        {
            throw new InvalidDataException("Unsupported DZ file format.");
        }

        fs.Position = 512;
        var count = 0;
        while (true)
        {
            var buf = br.ReadBytes(512);
            if (buf.Length < 512)
            {
                break;
            }

            var chunk = DzChunkHeader.FromBytes(buf);
            if (!chunk.Header.AsSpan().SequenceEqual(DzConstants.DzChunkHeader))
            {
                break;
            }

            var name = chunk.GetChunkName();
            if (nameFilter is null || nameFilter(name))
            {
                var outPath = Path.Combine(outDir, name);
                fs.Seek(fs.Position, SeekOrigin.Begin);
                using var outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
                long remaining = chunk.DataSize;
                var buffer = new byte[1024 * 1024];
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = fs.Read(buffer, 0, toRead);
                    if (read == 0)
                    {
                        break;
                    }
                    outFs.Write(buffer, 0, read);
                    remaining -= read;
                }
                count++;
            }

            if (fs.Position + chunk.DataSize >= dzSize)
            {
                break;
            }
            fs.Seek(chunk.DataSize, SeekOrigin.Current);
        }

        return count;
    }
}
