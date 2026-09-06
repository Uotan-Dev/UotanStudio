using System.Text;

namespace UotanStudio.Firmware.UpdateApp;

public sealed class UpdateAppEntry
{
    public string Name { get; set; } = string.Empty;

    public long HeaderOffset { get; set; }

    public uint HeaderSize { get; set; }

    public uint FileSize { get; set; }

    public long DataOffset { get; set; }
}

public static class UpdateAppProcessor
{
    public static readonly byte[] Magic = [0x55, 0xAA, 0x5A, 0xA5];

    public static ReadOnlySpan<byte> MagicBytes => Magic;

    /// <summary>
    /// Enumerates all entries within an update.app file.
    /// <para>枚举 update.app 中的所有条目。</para>
    /// </summary>
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
            var headerSize = br.ReadUInt32();
            fs.Seek(16, SeekOrigin.Current);
            var fileSize = br.ReadUInt32();
            fs.Seek(32, SeekOrigin.Current);

            var nameBytes = br.ReadBytes(16);
            var filename = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
            filename = CleanFileName(filename).ToLower();

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

            fs.Seek(22, SeekOrigin.Current);
            fs.Seek(headerStart + headerSize, SeekOrigin.Begin);

            var entry = new UpdateAppEntry
            {
                Name = filename,
                HeaderOffset = headerStart,
                HeaderSize = headerSize,
                FileSize = fileSize,
                DataOffset = fs.Position
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
    public static int Extract(string source, string outDir, List<string>? fileList = null)
    {
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The update.app file does not exist!", source);
        }

        Directory.CreateDirectory(outDir);
        var extractedFiles = new HashSet<string>();
        var count = 0;

        using var fs = new FileStream(source, FileMode.Open, FileAccess.Read);
        foreach (var entry in GetEntries(source))
        {
            if (fileList is not null && fileList.Count > 0 &&
                !fileList.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var finalName = entry.Name;
            if (extractedFiles.Contains(finalName))
            {
                finalName = $"{finalName}_2";
            }

            var outPath = Path.Combine(outDir, $"{finalName}.img");
            using var outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            fs.Seek(entry.DataOffset, SeekOrigin.Begin);

            long remaining = entry.FileSize;
            var buffer = new byte[10240];
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = fs.Read(buffer, 0, toRead);
                if (read <= 0)
                {
                    break;
                }
                outFs.Write(buffer, 0, read);
                remaining -= read;
            }
            extractedFiles.Add(entry.Name);
            count++;
        }

        return count;
    }

    private static string CleanFileName(string name) => new([.. name.Where(c => c is >= ((char)32) and <= ((char)126))]);
}
