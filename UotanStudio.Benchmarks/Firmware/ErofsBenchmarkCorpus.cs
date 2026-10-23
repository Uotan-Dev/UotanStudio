using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace UotanStudio.Benchmarks.Firmware;

/// <summary>
/// Materializes the deterministic mixed corpus shared by the EROFS build and extraction benchmarks: log-like
/// text, JSON-like structured records, incompressible random bytes, one large mixed file, a symlink and a
/// hardlink. Content is generated from fixed constants only, so every host produces the same bytes.
/// <para>为 EROFS 构建与解包基准生成共享的确定性混合语料：类日志文本、类 JSON 结构化记录、不可压缩随机字节、
/// 一个大型混合文件、一个符号链接与一个硬链接。内容只由固定常量生成，因此任何主机都会得到相同字节。</para>
/// </summary>
internal static class ErofsBenchmarkCorpus
{
    /// <summary>Number of 1 MiB text files. <para>1 MiB 文本文件的数量。</para></summary>
    internal const int TextFileCount = 12;

    /// <summary>Number of 1 MiB structured files. <para>1 MiB 结构化文件的数量。</para></summary>
    internal const int StructuredFileCount = 8;

    /// <summary>Number of 1 MiB random files. <para>1 MiB 随机文件的数量。</para></summary>
    internal const int RandomFileCount = 12;

    /// <summary>Size of the single large mixed file. <para>单个大型混合文件的尺寸。</para></summary>
    internal const int LargeFileBytes = 12 << 20;

    /// <summary>Payload bytes every corpus file spans before links are added. <para>添加链接前所有语料文件的负载字节数。</para></summary>
    internal const int PayloadBytes = (TextFileCount << 20) + (StructuredFileCount << 20)
        + (RandomFileCount << 20) + LargeFileBytes;

    /// <summary>Block granularity used by the generators. <para>生成器使用的块粒度。</para></summary>
    internal const int BlockBytes = 1 << 16;

    /// <summary>Fixed inode timestamp written to every corpus entry. <para>写入每个语料条目的固定 inode 时间戳。</para></summary>
    internal const long PinnedEpochSeconds = 1_700_000_000;

    /// <summary>
    /// Writes the corpus tree under <paramref name="root"/> and returns its shape and link availability.
    /// <para>在 <paramref name="root"/> 下写出语料树，并返回其规模与链接可用性。</para>
    /// </summary>
    /// <param name="root">Destination directory; created when missing. <para>目标目录；缺失时创建。</para></param>
    /// <returns>Payload bytes, regular file count and whether the symlink/hardlink could be created.
    /// <para>负载字节数、常规文件数量，以及符号链接/硬链接是否创建成功。</para></returns>
    internal static Corpus Create(string root)
    {
        Directory.CreateDirectory(root);
        var text = Path.Combine(root, "text");
        var structured = Path.Combine(root, "structured");
        var random = Path.Combine(root, "random");
        var large = Path.Combine(root, "large");
        Directory.CreateDirectory(text);
        Directory.CreateDirectory(structured);
        Directory.CreateDirectory(random);
        Directory.CreateDirectory(large);

        for (var index = 0; index < TextFileCount; index++)
        {
            WriteTextLikeFile(Path.Combine(text, $"text-{index:00}.log"), 1 << 20, index, structuredContent: false);
        }

        for (var index = 0; index < StructuredFileCount; index++)
        {
            WriteTextLikeFile(Path.Combine(structured, $"records-{index:00}.jsonl"), 1 << 20, index,
                structuredContent: true);
        }

        for (var index = 0; index < RandomFileCount; index++)
        {
            WriteRandomFile(Path.Combine(random, $"random-{index:00}.bin"), 1 << 20, (uint)(0x9E37_79B9 * (index + 1)));
        }

        WriteLargeMixedFile(Path.Combine(large, "mixed-12m.bin"), LargeFileBytes, fileIndex: 12);

        var hardLink = TryCreateHardLink(Path.Combine(root, "text-hardlink.log"),
            Path.Combine(text, "text-001.log"));
        var symlink = TryCreateSymbolicLink(Path.Combine(root, "readme-link.log"), "text/text-000.log");
        PinTimes(root);
        return new Corpus(PayloadBytes, TextFileCount + StructuredFileCount + RandomFileCount + 1, symlink, hardLink);
    }

    private static void WriteTextLikeFile(string path, int length, int fileIndex, bool structuredContent)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BlockBytes,
            FileOptions.SequentialScan);
        var buffer = new byte[BlockBytes];
        var remaining = length;
        var blockIndex = 0L;
        while (remaining > 0)
        {
            FillTextBlock(buffer, fileIndex, blockIndex, structuredContent);
            var count = Math.Min(buffer.Length, remaining);
            stream.Write(buffer, 0, count);
            remaining -= count;
            blockIndex++;
        }
    }

    private static void WriteRandomFile(string path, int length, uint seed)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BlockBytes,
            FileOptions.SequentialScan);
        var buffer = new byte[BlockBytes];
        var state = seed | 1u;
        var remaining = length;
        while (remaining > 0)
        {
            FillRandomBlock(buffer, ref state);
            var count = Math.Min(buffer.Length, remaining);
            stream.Write(buffer, 0, count);
            remaining -= count;
        }
    }

    private static void WriteLargeMixedFile(string path, int length, int fileIndex)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BlockBytes,
            FileOptions.SequentialScan);
        var buffer = new byte[BlockBytes];
        var state = 0xC0FF_EE01u;
        var remaining = length;
        var blockIndex = 0L;
        while (remaining > 0)
        {
            if ((blockIndex & 1) == 0)
            {
                FillRandomBlock(buffer, ref state);
            }
            else
            {
                FillTextBlock(buffer, fileIndex, blockIndex, structuredContent: false);
            }

            var count = Math.Min(buffer.Length, remaining);
            stream.Write(buffer, 0, count);
            remaining -= count;
            blockIndex++;
        }
    }

    private static void FillTextBlock(Span<byte> buffer, int fileIndex, long blockIndex, bool structuredContent)
    {
        var offset = 0;
        var line = 0;
        while (true)
        {
            var text = structuredContent
                ? FormattableString.Invariant(
                    $"{{\"seq\":{blockIndex * 512 + line},\"kind\":\"package\",\"path\":\"/vendor/etc/module-{fileIndex:00}/asset-{line % 64:00}.dat\",\"size\":{(line * 977 + fileIndex * 131) % 1_048_576},\"crc\":\"{((uint)(blockIndex * 31 + line) * 2_654_435_761u).ToString("X8", CultureInfo.InvariantCulture)}\",\"tags\":[\"vendor\",\"system\"]}}\n")
                : FormattableString.Invariant(
                    $"2026-10-23T{(fileIndex + line) % 24:00}:{line % 60:00}:{(line * 7 + fileIndex) % 60:00}.{line % 1000:000} INFO uotan.erofs[{fileIndex % 8}] request={blockIndex * 512 + line} path=/system/priv-app/Module{fileIndex:00}/asset-{line % 64:00}.dat status={(line % 17 == 0 ? 404 : 200)} bytes={(line * 977 + fileIndex * 131) % 1_048_576} latency={(line * 37) % 250}ms\n");
            if (offset + text.Length > buffer.Length)
            {
                break;
            }

            offset += Encoding.UTF8.GetBytes(text, buffer[offset..]);
            line++;
        }

        if (offset < buffer.Length)
        {
            buffer[offset..].Fill((byte)'\n');
        }
    }

    private static void FillRandomBlock(Span<byte> buffer, ref uint state)
    {
        var index = 0;
        for (; index + 4 <= buffer.Length; index += 4)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            buffer[index] = (byte)state;
            buffer[index + 1] = (byte)(state >> 8);
            buffer[index + 2] = (byte)(state >> 16);
            buffer[index + 3] = (byte)(state >> 24);
        }

        for (; index < buffer.Length; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            buffer[index] = (byte)state;
        }
    }

    private static void PinTimes(string root)
    {
        var pinned = DateTimeOffset.FromUnixTimeSeconds(PinnedEpochSeconds).UtcDateTime;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, pinned);
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(static path => path.Length))
        {
            Directory.SetLastWriteTimeUtc(directory, pinned);
        }

        Directory.SetLastWriteTimeUtc(root, pinned);
    }

    private static bool TryCreateSymbolicLink(string linkPath, string target)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.WriteLine($"[erofs-bench] symlink unavailable on this host: {ex.Message}");
            return false;
        }
    }

    private static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return CreateHardLinkWindows(linkPath, existingPath, IntPtr.Zero);
            }

            return LinkUnix(existingPath, linkPath) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException)
        {
            Console.WriteLine($"[erofs-bench] hardlink unavailable on this host: {ex.Message}");
            return false;
        }
    }

    /// <summary>Shape of the generated corpus. <para>已生成语料的规模。</para></summary>
    /// <param name="PayloadBytes">Summed payload bytes of the regular files. <para>常规文件的负载字节总数。</para></param>
    /// <param name="FileCount">Regular file count, excluding the two links. <para>常规文件数量，不含两个链接。</para></param>
    /// <param name="SymlinkCreated">Whether the host allowed the symlink. <para>宿主是否允许创建符号链接。</para></param>
    /// <param name="HardLinkCreated">Whether the host allowed the hardlink. <para>宿主是否允许创建硬链接。</para></param>
    internal readonly record struct Corpus(int PayloadBytes, int FileCount, bool SymlinkCreated, bool HardLinkCreated);

    [DllImport("Kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkWindows(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkUnix(string existingPath, string newPath);
}