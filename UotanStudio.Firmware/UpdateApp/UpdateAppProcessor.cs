using System.Text;
using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Common.Streams;

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
            FileExtraction.ValidateRange(headerStart, 76, fs.Length, "UPDATE.APP entry header");
            var headerSize = br.ReadUInt32();
            if (headerSize < 76)
                throw new InvalidDataException("UPDATE.APP entry header is too short.");
            FileExtraction.ValidateRange(headerStart, headerSize, fs.Length, "UPDATE.APP entry header");
            fs.Seek(16, SeekOrigin.Current);
            var fileSize = br.ReadUInt32();
            fs.Seek(32, SeekOrigin.Current);

            var nameBytes = br.ReadBytes(16);
            var filename = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
            filename = CleanFileName(filename).ToLowerInvariant();
            FileExtraction.ValidateRange(headerStart + headerSize, fileSize, fs.Length, "UPDATE.APP entry data");

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
            FileExtraction.WriteAtomic(outPath, output => StreamCopy.CopyExactly(fs, output, entry.FileSize,
                cancellationToken: cancellationToken), overwrite);
            extractedFiles.Add(finalName);
            count++;
        }

        return count;
    }

    private static string CleanFileName(string name) => new([.. name.Where(c => c is >= ((char)32) and <= ((char)126))]);
}