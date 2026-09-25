using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Packing;

/// <summary>
/// Options controlling how a directory is repacked into an APK.
/// <para>控制目录重打包为 APK 的选项。</para>
/// </summary>
public sealed class ApkRepackOptions
{
    /// <summary>
    /// Alignment for stored native libraries in bytes (0 disables; 16384 for 16 KiB page devices).
    /// <para>native 库存储对齐字节数（0 为不对齐；16 KiB 页设备用 16384）。</para>
    /// </summary>
    /// <value>For <c>.so</c> entries a value above 0 stores them uncompressed and pads the data start to this boundary with an alignment extra field (header id 0xd935); 1 stores without padding; 0 or below sends <c>.so</c> files through the normal deflate path. Defaults to 4096.
    /// <para>对 <c>.so</c> 条目：大于 0 时不压缩存储，并用对齐扩展字段（头 id 0xd935）把数据起点填充到该边界；1 为存储但不填充；0 或负值使 <c>.so</c> 走常规 deflate 路径。默认 4096。</para></value>
    public int NativeLibraryAlignmentBytes { get; set; } = 4096;

    /// <summary>
    /// Stores resources.arsc uncompressed with 4-byte alignment (required when targetSdkVersion >= 30).
    /// <para>以未压缩 + 4 字节对齐存储 resources.arsc（targetSdkVersion >= 30 时必需）。</para>
    /// </summary>
    /// <value>When <see langword="true"/> the <c>resources.arsc</c> entry (case-insensitive name) is stored with a 4-byte alignment extra field; false deflates it like ordinary entries.
    /// <para>为 <see langword="true"/> 时，<c>resources.arsc</c> 条目（按名称不区分大小写）带 4 字节对齐扩展字段存储；false 时与普通条目一样 deflate。</para></value>
    public bool StoreResourcesArsc { get; set; } = true;

    /// <summary>
    /// Removes v1 signature entries (META-INF MANIFEST.MF/*.SF/*.RSA/*.DSA/*.EC) that are
    /// already present in the input, so re-signing does not fail verification.
    /// <para>移除输入中既有的 v1 签名条目（META-INF MANIFEST.MF/*.SF/*.RSA/*.DSA/*.EC），避免重新签名后校验失败。</para>
    /// </summary>
    /// <value>When <see langword="true"/> entry collection skips files under <c>META-INF/</c> matching the v1 signature name patterns; false carries them over like any other file.
    /// <para>为 <see langword="true"/> 时，收集条目阶段跳过 <c>META-INF/</c> 下命中 v1 签名命名模式的文件；false 时与其它文件一并打包。</para></value>
    public bool StripSignatureEntries { get; set; } = true;

    /// <summary>Deflate level for compressed entries.
    /// <para>压缩条目的 deflate 级别。</para></summary>
    /// <value>Applied to every deflated entry; entries forced to store mode (<c>resources.arsc</c> and aligned <c>.so</c> files) are never compressed. Defaults to <see cref="System.IO.Compression.CompressionLevel.Optimal"/>.
    /// <para>用于所有 deflate 条目；强制存储的条目（<c>resources.arsc</c> 与对齐的 <c>.so</c>）不被压缩。默认 <see cref="System.IO.Compression.CompressionLevel.Optimal"/>。</para></value>
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Optimal;
}

/// <summary>
/// Repacks an unpacked APK directory back into an installable APK: a streaming ZIP writer with
/// entry alignment (zipalign semantics), deterministic entry ordering and atomic output commit.
/// <para>把解包后的 APK 目录重建为可安装 APK：流式 ZIP 写入器，带条目对齐（zipalign 语义）、
/// 确定性条目顺序与输出原子提交。</para>
/// </summary>
public static class ApkRepacker
{
    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint EocdSignature = 0x06054b50;
    private const ushort VersionNeeded = 20;
    private const ushort Utf8Flag = 0x0800;
    private const ushort AlignmentExtraFieldId = 0xd935;

    /// <summary>
    /// Repacks a directory into an APK file. The output is written to a sibling temporary file
    /// and atomically replaces the target on success.
    /// <para>把目录重打包为 APK 文件。输出先写入同级临时文件，成功后原子替换目标。</para>
    /// </summary>
    /// <param name="inputDirectory">Existing directory enumerated recursively; every file becomes an entry whose name is the <c>/</c>-separated relative path, written in ordinal-sorted order. <para>递归枚举的已存在目录；每个文件成为一个条目，条目名为以 <c>/</c> 分隔的相对路径，按序数排序写出。</para></param>
    /// <param name="outputPath">Destination APK path; an existing file is replaced only after the archive is fully written, and the parent directory is created when missing. <para>目标 APK 路径；仅在归档完整写出后才替换既有文件，缺失的父目录会被创建。</para></param>
    /// <param name="options">Repacking options; <see langword="null"/> uses the <see cref="ApkRepackOptions"/> defaults. <para>重打包选项；为 null 时使用 <see cref="ApkRepackOptions"/> 默认值。</para></param>
    /// <param name="cancellationToken">Observed during collection and per-entry writing; failure or cancellation deletes the temporary file and leaves the existing output untouched. <para>在条目收集与逐条写入期间生效；失败或取消时删除临时文件并保持既有输出不动。</para></param>
    /// <exception cref="DirectoryNotFoundException"><paramref name="inputDirectory"/> is blank or does not exist. <para><paramref name="inputDirectory"/> 为空或不存在。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="outputPath"/> is blank. <para><paramref name="outputPath"/> 为空白。</para></exception>
    /// <exception cref="InvalidDataException">The input directory holds no files, more than 65535 entries, or any entry or archive beyond the 32-bit ZIP limits. <para>输入目录没有文件、条目超过 65535 个，或有条目/整包超出 32 位 ZIP 上限。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    /// <exception cref="OverflowException">An entry's offsets or sizes exceed the 32-bit fields written into its headers. <para>条目的偏移或大小超出写入头部所需的 32 位字段。</para></exception>
    /// <exception cref="IOException">The temporary file cannot be written or moved onto the target. <para>临时文件无法写入或无法移动到目标。</para></exception>
    public static void Repack(string inputDirectory, string outputPath, ApkRepackOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputDirectory) || !Directory.Exists(inputDirectory))
        {
            throw new DirectoryNotFoundException($"Input directory not found: {inputDirectory}. 未找到输入目录：{inputDirectory}");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path must be provided. 必须提供输出路径。", nameof(outputPath));
        }

        var resolvedOptions = options ?? new ApkRepackOptions();
        var entries = CollectEntries(inputDirectory, resolvedOptions, cancellationToken);
        if (entries.Count == 0)
        {
            throw new InvalidDataException($"Input directory contains no files: {inputDirectory}. 输入目录中没有文件：{inputDirectory}");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = outputPath + ".tmp";
        try
        {
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
            {
                WriteArchive(output, inputDirectory, entries, resolvedOptions, cancellationToken);
            }

            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static List<(string EntryName, string FilePath, bool Store, int Alignment)> CollectEntries(
        string root, ApkRepackOptions options, CancellationToken cancellationToken)
    {
        var result = new List<(string EntryName, string FilePath, bool Store, int Alignment)>();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Length == 0 || relative.StartsWith("../", StringComparison.Ordinal))
            {
                continue;
            }

            if (options.StripSignatureEntries && IsSignatureEntry(relative))
            {
                continue;
            }

            var fileName = Path.GetFileName(relative);
            if (options.StoreResourcesArsc && fileName.Equals("resources.arsc", StringComparison.OrdinalIgnoreCase))
            {
                result.Add((relative, file, true, 4));
            }
            else if (fileName.EndsWith(".so", StringComparison.OrdinalIgnoreCase) && options.NativeLibraryAlignmentBytes > 0)
            {
                result.Add((relative, file, true, options.NativeLibraryAlignmentBytes));
            }
            else
            {
                result.Add((relative, file, false, 0));
            }
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.EntryName, b.EntryName));
        if (result.Count > 0xFFFF)
        {
            throw new InvalidDataException($"Too many entries for a non-ZIP64 APK ({result.Count}). 条目过多，无法生成非 ZIP64 的 APK（{result.Count}）。");
        }

        return result;
    }

    private static bool IsSignatureEntry(string entryName)
    {
        if (!entryName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = entryName["META-INF/".Length..];
        if (name.Equals("MANIFEST.MF", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.EndsWith(".SF", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".EC", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteArchive(
        Stream output,
        string root,
        IReadOnlyList<(string EntryName, string FilePath, bool Store, int Alignment)> entries,
        ApkRepackOptions options,
        CancellationToken cancellationToken)
    {
        using var centralDirectory = new MemoryStream();
        var entryCount = 0;

        foreach (var (entryName, filePath, store, alignment) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nameBytes = Encoding.UTF8.GetBytes(entryName);
            var localHeaderOffset = checked((uint)output.Position);
            (uint Crc, uint CompressedSize, uint UncompressedSize) sizes;

            var extraBytes = BuildAlignmentExtra(output.Position + 30 + nameBytes.Length, alignment);

            WriteLocalHeader(output, nameBytes, extraBytes, store, localHeaderOffset);
            if (store)
            {
                sizes = CopyStored(output, filePath, cancellationToken);
            }
            else
            {
                sizes = CopyDeflated(output, filePath, options.CompressionLevel, cancellationToken);
            }

            PatchLocalHeader(output, localHeaderOffset, sizes);

            WriteCentralEntry(centralDirectory, entryName, nameBytes, store, localHeaderOffset, sizes);
            entryCount++;
        }

        var centralDirectoryOffset = output.Position;
        centralDirectory.Position = 0;
        centralDirectory.CopyTo(output);
        var centralDirectorySize = output.Position - centralDirectoryOffset;
        if (centralDirectoryOffset > uint.MaxValue || centralDirectorySize > uint.MaxValue)
        {
            throw new InvalidDataException("APK exceeds the 32-bit ZIP size limit. APK 超出 32 位 ZIP 大小上限。");
        }

        Span<byte> eocd = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(eocd, EocdSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.Slice(4, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.Slice(6, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.Slice(8, 2), (ushort)entryCount);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.Slice(10, 2), (ushort)entryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.Slice(12, 4), (uint)centralDirectorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.Slice(16, 4), (uint)centralDirectoryOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.Slice(20, 2), 0);
        output.Write(eocd);
    }

    private static void WriteLocalHeader(Stream output, byte[] nameBytes, byte[] extraBytes, bool store, uint localHeaderOffset)
    {
        Span<byte> header = stackalloc byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(header, LocalHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(4, 2), VersionNeeded);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(6, 2), Utf8Flag);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(8, 2), (ushort)(store ? 0 : 8));
        WriteDosTimestamp(header.Slice(10, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(14, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(18, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(22, 4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(26, 2), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(28, 2), (ushort)extraBytes.Length);
        output.Write(header);
        output.Write(nameBytes);
        if (extraBytes.Length > 0)
        {
            output.Write(extraBytes);
        }
    }

    private static void PatchLocalHeader(Stream output, uint localHeaderOffset, (uint Crc, uint CompressedSize, uint UncompressedSize) sizes)
    {
        Span<byte> patch = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(patch.Slice(0, 4), sizes.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(patch.Slice(4, 4), sizes.CompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(patch.Slice(8, 4), sizes.UncompressedSize);
        var saved = output.Position;
        output.Seek(localHeaderOffset + 14, SeekOrigin.Begin);
        output.Write(patch);
        output.Seek(saved, SeekOrigin.Begin);
    }

    private static void WriteCentralEntry(
        Stream centralDirectory,
        string entryName,
        byte[] nameBytes,
        bool store,
        uint localHeaderOffset,
        (uint Crc, uint CompressedSize, uint UncompressedSize) sizes)
    {
        Span<byte> header = stackalloc byte[46];
        BinaryPrimitives.WriteUInt32LittleEndian(header, CentralHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(4, 2), (3 << 8) | VersionNeeded);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(6, 2), VersionNeeded);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(8, 2), Utf8Flag);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(10, 2), (ushort)(store ? 0 : 8));
        WriteDosTimestamp(header.Slice(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), sizes.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(20, 4), sizes.CompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24, 4), sizes.UncompressedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(28, 2), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(30, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(32, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(34, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(36, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(38, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(42, 4), localHeaderOffset);
        centralDirectory.Write(header);
        centralDirectory.Write(nameBytes);
    }

    private static (uint Crc, uint CompressedSize, uint UncompressedSize) CopyStored(Stream output, string filePath, CancellationToken cancellationToken)
    {
        using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        if (input.Length > uint.MaxValue)
        {
            throw new InvalidDataException($"Entry '{filePath}' exceeds the 32-bit ZIP entry size limit. 条目 '{filePath}' 超出 32 位 ZIP 条目大小上限。");
        }

        var crc = new Crc32();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            crc.Append(buffer.AsSpan(0, read));
            output.Write(buffer.AsSpan(0, read));
        }

        return (crc.GetCurrentHashAsUInt32(), (uint)input.Length, (uint)input.Length);
    }

    private static (uint Crc, uint CompressedSize, uint UncompressedSize) CopyDeflated(
        Stream output, string filePath, CompressionLevel level, CancellationToken cancellationToken)
    {
        using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        if (input.Length > uint.MaxValue)
        {
            throw new InvalidDataException($"Entry '{filePath}' exceeds the 32-bit ZIP entry size limit. 条目 '{filePath}' 超出 32 位 ZIP 条目大小上限。");
        }

        var crc = new Crc32();
        long uncompressed = input.Length;
        var start = output.Position;
        using (var deflate = new DeflateStream(output, level, leaveOpen: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                crc.Append(buffer.AsSpan(0, read));
                deflate.Write(buffer.AsSpan(0, read));
            }
        }

        var compressedSize = output.Position - start;
        if (compressedSize > uint.MaxValue)
        {
            throw new InvalidDataException($"Entry '{filePath}' compressed size exceeds the 32-bit ZIP limit. 条目 '{filePath}' 压缩后大小超出 32 位 ZIP 上限。");
        }

        return (crc.GetCurrentHashAsUInt32(), (uint)compressedSize, (uint)uncompressed);
    }

    private static byte[] BuildAlignmentExtra(long headerEndOffset, int alignment)
    {
        if (alignment <= 1)
        {
            return [];
        }

        var misalignment = (int)(headerEndOffset % alignment);
        var padding = misalignment == 0 ? 0 : alignment - misalignment;
        if (padding > 0 && padding < 4)
        {
            padding += alignment;
        }

        if (padding == 0)
        {
            return [];
        }

        var extra = new byte[padding];
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(0, 2), AlignmentExtraFieldId);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(2, 2), (ushort)(padding - 4));
        return extra;
    }

    private static void WriteDosTimestamp(Span<byte> target)
    {
        const ushort dosDate = ((2020 - 1980) << 9) | (1 << 5) | 1;
        const ushort dosTime = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(0, 2), dosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(2, 2), dosDate);
    }
}