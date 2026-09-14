using System.IO.Compression;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;

/// <summary>
/// <para>JAR/ZIP 容器内 DEX 与资源条目的种类。</para>
/// The kind of an entry inside a JAR/ZIP container.
/// </summary>
public enum JarEntryKind
{
    /// <summary>
    /// <para>DEX 字节码条目（classes*.dex）。</para>
    /// A DEX bytecode entry (classes*.dex).
    /// </summary>
    Dex,

    /// <summary>
    /// <para>v1/JAR 签名工件（META-INF 下的清单、签名与证书），只读展示。</para>
    /// A v1/JAR signature artifact (manifest, signature or certificate under META-INF); read-only.
    /// </summary>
    Signature,

    /// <summary>
    /// <para>普通资源条目，可导出与替换。</para>
    /// A plain resource entry that can be extracted and replaced.
    /// </summary>
    Resource,
}

/// <summary>
/// <para>JAR/ZIP 容器内的条目摘要信息。</para>
/// Summary information of an entry inside a JAR/ZIP container.
/// </summary>
/// <param name="Name">
/// <para>条目完整名称（ZIP 内路径，正斜杠分隔）。</para>
/// The full entry name (ZIP-internal path, forward-slash separated).
/// </param>
/// <param name="Length">
/// <para>条目解压后的字节数。</para>
/// The uncompressed entry size in bytes.
/// </param>
/// <param name="Kind">
/// <para>条目种类。</para>
/// The entry kind.
/// </param>
public sealed record JarEntryInfo(string Name, long Length, JarEntryKind Kind);

/// <summary>
/// <para>JAR/ZIP 容器（含 .jar/.apk）中 DEX 与资源条目的读取、写回与替换。
/// 所有条目读取有界；写回经同目录临时文件重建 ZIP 后原子替换，仅修改目标条目。
/// 签名工件（META-INF）只读：写回会使既有 v1/v2/v3 签名失效，应由签名流程剥离重建。</para>
/// Reads, writes back and replaces DEX and resource entries inside a JAR/ZIP container
/// (.jar/.apk). All entry reads are bounded; write-back rebuilds the ZIP through a
/// same-directory temporary file and commits atomically, touching only the target entry.
/// Signature artifacts (META-INF) are read-only: any write invalidates existing v1/v2/v3
/// signatures, which the signing flow strips and rebuilds.
/// </summary>
public static class JarDexContainer
{
    /// <summary>
    /// <para>单个条目的解压大小上限（256 MiB，与 DEX/AXML/ARSC 元数据上限口径一致）。</para>
    /// The maximum uncompressed size of a single entry (256 MiB, aligned with the
    /// DEX/AXML/ARSC metadata cap).
    /// </summary>
    public const long MaxEntryBytes = 256L * 1024 * 1024;

    /// <summary>
    /// <para>容器内条目数量上限。</para>
    /// The maximum number of entries in one container.
    /// </summary>
    public const int MaxEntryCount = 65536;

    /// <summary>
    /// <para>判断给定路径扩展名是否为受支持的 DEX 容器（.jar/.apk）。</para>
    /// Whether the given path extension is a supported DEX container (.jar/.apk).
    /// </summary>
    /// <param name="path">
    /// <para>文件名或完整路径。</para>
    /// A file name or full path.
    /// </param>
    public static bool IsContainerPath(string? path)
    {
        var ext = Path.GetExtension(path ?? string.Empty);
        return ext.Equals(".jar", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".apk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <para>枚举容器内全部条目（按名称排序）；超出数量上限时抛出格式异常。</para>
    /// Enumerates all entries in the container (name-ordered); throws a format exception
    /// when the entry count exceeds the cap.
    /// </summary>
    /// <param name="path">
    /// <para>容器文件路径。</para>
    /// The container file path.
    /// </param>
    /// <returns>条目摘要列表。<para>The list of entry summaries.</para></returns>
    public static IReadOnlyList<JarEntryInfo> ListEntries(string path)
    {
        using var stream = OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaxEntryCount)
        {
            throw new InvalidDataException(
                $"容器条目数 {archive.Entries.Count} 超过上限 {MaxEntryCount}。/ Container entry count exceeds the cap.");
        }

        return archive.Entries
            .Select(e => new JarEntryInfo(e.FullName, e.Length, Classify(e.FullName)))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// <para>列出容器内全部 DEX 条目名（根级 classes*.dex，按名称自然排序）。</para>
    /// Lists all DEX entry names in the container (root-level classes*.dex, name-ordered).
    /// </summary>
    /// <param name="path">
    /// <para>容器文件路径。</para>
    /// The container file path.
    /// </param>
    /// <returns>DEX 条目名列表。<para>The list of DEX entry names.</para></returns>
    public static IReadOnlyList<string> ListDexEntries(string path)
    {
        using var stream = OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaxEntryCount)
        {
            throw new InvalidDataException(
                $"容器条目数 {archive.Entries.Count} 超过上限 {MaxEntryCount}。/ Container entry count exceeds the cap.");
        }

        return archive.Entries
            .Where(e => IsDexEntryName(e.FullName))
            .Select(e => e.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// <para>判断条目名是否为根级 classes*.dex（允许 classes.dex、classes2.dex…）。</para>
    /// Whether the entry name is a root-level classes*.dex (classes.dex, classes2.dex, ...).
    /// </summary>
    /// <param name="entryName">
    /// <para>条目名。</para>
    /// The entry name.
    /// </param>
    public static bool IsDexEntryName(string? entryName)
    {
        if (entryName is null || entryName.Contains('/'))
        {
            return false;
        }

        if (!entryName.EndsWith(".dex", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        const string prefix = "classes";
        return entryName.Length >= prefix.Length + 4
            && entryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && entryName[prefix.Length..^4].All(char.IsAsciiDigit);
    }

    /// <summary>
    /// <para>判断条目是否为 v1/JAR 签名工件（META-INF 下的清单、SF、RSA/DSA/EC 与 SF 以外的
    /// 标准签名扩展）。</para>
    /// Whether the entry is a v1/JAR signature artifact (manifest, SF, RSA/DSA/EC and related
    /// standard signature extensions under META-INF).
    /// </summary>
    /// <param name="entryName">
    /// <para>条目名。</para>
    /// The entry name.
    /// </param>
    public static bool IsSignatureEntry(string? entryName)
    {
        if (entryName is null || !entryName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = entryName["META-INF/".Length..];
        if (name.Equals("MANIFEST.MF", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dot = name.LastIndexOf('.');
        if (dot < 0)
        {
            return false;
        }

        return name.AsSpan(dot + 1).Equals(".SF".AsSpan().Slice(1), StringComparison.OrdinalIgnoreCase)
            || name[(dot + 1)..].Equals("RSA", StringComparison.OrdinalIgnoreCase)
            || name[(dot + 1)..].Equals("DSA", StringComparison.OrdinalIgnoreCase)
            || name[(dot + 1)..].Equals("EC", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <para>有界读取容器内指定条目的全部字节。</para>
    /// Reads the full bytes of one entry, with a bounded length.
    /// </summary>
    /// <param name="path">
    /// <para>容器文件路径。</para>
    /// The container file path.
    /// </param>
    /// <param name="entryName">
    /// <para>条目名。</para>
    /// The entry name.
    /// </param>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// The cancellation token.
    /// </param>
    /// <returns>条目字节。<para>The entry bytes.</para></returns>
    public static byte[] LoadEntry(string path, string entryName, CancellationToken cancellationToken = default)
    {
        using var stream = OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entry = FindEntry(archive, entryName);
        return ReadBounded(entry, entryName, cancellationToken);
    }

    /// <summary>
    /// <para>把修改后的 DEX 字节写回容器内指定条目：以同目录临时文件流式重建 ZIP（其余条目
    /// 按原始压缩方式逐条复制），全部成功后原子替换源文件。</para>
    /// Writes the modified DEX bytes back into one container entry: rebuilds the ZIP through a
    /// same-directory temporary file (other entries are copied one by one with their original
    /// compression method) and atomically replaces the source file on success.
    /// </summary>
    /// <param name="path">
    /// <para>容器文件路径。</para>
    /// The container file path.
    /// </param>
    /// <param name="entryName">
    /// <para>要替换的条目名。</para>
    /// The entry name to replace.
    /// </param>
    /// <param name="entryBytes">
    /// <para>新的条目字节（DEX 重建输出）。</para>
    /// The new entry bytes (the rebuilt DEX output).
    /// </param>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// The cancellation token.
    /// </param>
    public static void WriteBack(string path, string entryName, byte[] entryBytes, CancellationToken cancellationToken = default)
    {
        ReplaceEntry(path, entryName, entryBytes, cancellationToken);
    }

    /// <summary>
    /// <para>导出容器内的资源条目到目标文件（目标目录自动创建，写入有界并原子提交）。</para>
    /// Extracts a resource entry to the target file (the directory is created; the write is
    /// bounded and committed atomically).
    /// </summary>
    /// <param name="path">
    /// <para>容器文件路径。</para>
    /// The container file path.
    /// </param>
    /// <param name="entryName">
    /// <para>资源条目名。</para>
    /// The resource entry name.
    /// </param>
    /// <param name="outputPath">
    /// <para>输出文件路径。</para>
    /// The output file path.
    /// </param>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// The cancellation token.
    /// </param>
    public static void ExtractResource(string path, string entryName, string outputPath, CancellationToken cancellationToken = default)
    {
        if (IsSignatureEntry(entryName))
        {
            throw new InvalidOperationException($"签名条目 {entryName} 只读，不可导出编辑副本。/ Signature entry {entryName} is read-only.");
        }

        using var stream = OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entry = FindEntry(archive, entryName);
        var bytes = ReadBounded(entry, entryName, cancellationToken);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, outputPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { /* best effort 尽力清理 */ }
        }
    }

    /// <summary>
    /// <para>用本地文件内容替换容器内资源条目（读取有界，经临时文件原子写回）。</para>
    /// Replaces a resource entry with the content of a local file (bounded read, atomic
    /// write-back through a temporary file).
    /// </summary>
    /// <param name="path">
    /// <para>容器文件路径。</para>
    /// The container file path.
    /// </param>
    /// <param name="entryName">
    /// <para>资源条目名。</para>
    /// The resource entry name.
    /// </param>
    /// <param name="sourceFilePath">
    /// <para>替换内容的本地文件路径。</para>
    /// The local file providing the replacement content.
    /// </param>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// The cancellation token.
    /// </param>
    public static void ReplaceResource(string path, string entryName, string sourceFilePath, CancellationToken cancellationToken = default)
    {
        if (IsSignatureEntry(entryName))
        {
            throw new InvalidOperationException($"签名条目 {entryName} 只读，不可替换。/ Signature entry {entryName} is read-only.");
        }

        using var source = File.OpenRead(sourceFilePath);
        if (source.Length > MaxEntryBytes)
        {
            throw new InvalidDataException(
                $"替换内容 {source.Length} 字节超过单条目上限 {MaxEntryBytes}。/ Replacement exceeds the per-entry cap.");
        }

        var bytes = new byte[source.Length];
        source.ReadExactly(bytes);
        WriteBack(path, entryName, bytes, cancellationToken);
    }

    /// <summary>
    /// <para>把新建条目（含 DEX 与资源）连同全部既有条目写入新容器（临时文件原子提交）。</para>
    /// Writes a set of entries (DEX and resources) into a new container (atomic commit via a
    /// temporary file).
    /// </summary>
    /// <param name="outputPath">
    /// <para>输出容器路径。</para>
    /// The output container path.
    /// </param>
    /// <param name="entries">
    /// <para>条目名与内容的有序集合。</para>
    /// The ordered set of entry names and contents.
    /// </param>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// The cancellation token.
    /// </param>
    public static void WriteNew(string outputPath, IReadOnlyList<(string Name, byte[] Content)> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count > MaxEntryCount)
        {
            throw new InvalidDataException($"条目数超过上限 {MaxEntryCount}。/ Entry count exceeds the cap.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var (name, content) in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (content.Length > MaxEntryBytes)
                    {
                        throw new InvalidDataException(
                            $"条目 {name} 大小超过上限 {MaxEntryBytes}。/ Entry {name} exceeds the per-entry cap.");
                    }
                    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    using var output = entry.Open();
                    output.Write(content);
                }
            }
            File.Move(temporary, outputPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { /* best effort 尽力清理 */ }
        }
    }

    private static FileStream OpenRead(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"容器文件不存在: {path} / Container file not found: {path}", path);
        }
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
    }

    private static ZipArchiveEntry FindEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, entryName, StringComparison.Ordinal));
        if (entry is null)
        {
            throw new InvalidDataException($"容器内不存在条目 {entryName}。/ Entry {entryName} does not exist in the container.");
        }
        return entry;
    }

    private static byte[] ReadBounded(ZipArchiveEntry entry, string entryName, CancellationToken cancellationToken)
    {
        if (entry.Length > MaxEntryBytes)
        {
            throw new InvalidDataException(
                $"条目 {entryName} 解压大小 {entry.Length} 超过上限 {MaxEntryBytes}。/ Entry {entryName} exceeds the per-entry cap.");
        }

        var bytes = new byte[entry.Length];
        using var input = entry.Open();
        int read;
        var offset = 0;
        while ((read = input.Read(bytes, offset, bytes.Length - offset)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            offset += read;
        }
        if (offset != bytes.Length)
        {
            throw new EndOfStreamException(
                $"条目 {entryName} 提前结束（期望 {bytes.Length} 字节，实际 {offset}）。/ Entry {entryName} ended early.");
        }
        return bytes;
    }

    private static JarEntryKind Classify(string entryName) =>
        IsSignatureEntry(entryName) ? JarEntryKind.Signature
        : IsDexEntryName(entryName) ? JarEntryKind.Dex
        : JarEntryKind.Resource;

    private static void ReplaceEntry(string path, string entryName, byte[] replacementBytes, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"容器文件不存在: {path} / Container file not found: {path}", path);
        }

        var directory = Path.GetDirectoryName(fullPath)!;
        var temporary = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            using (var sourceStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            using (var sourceArchive = new ZipArchive(sourceStream, ZipArchiveMode.Read, leaveOpen: false))
            {
                if (sourceArchive.Entries.Count > MaxEntryCount)
                {
                    throw new InvalidDataException(
                        $"容器条目数超过上限 {MaxEntryCount}。/ Container entry count exceeds the cap.");
                }
                if (sourceArchive.Entries.All(e => !string.Equals(e.FullName, entryName, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException($"容器内不存在条目 {entryName}。/ Entry {entryName} does not exist in the container.");
                }

                using var targetStream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var targetArchive = new ZipArchive(targetStream, ZipArchiveMode.Create, leaveOpen: false);
                foreach (var entry in sourceArchive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var isTarget = string.Equals(entry.FullName, entryName, StringComparison.Ordinal);
                    var created = targetArchive.CreateEntry(
                        entry.FullName,
                        isTarget || entry.Length != entry.CompressedLength
                            ? CompressionLevel.Optimal
                            : CompressionLevel.NoCompression);
                    using var destination = created.Open();
                    if (isTarget)
                    {
                        destination.Write(replacementBytes);
                    }
                    else
                    {
                        using var input = entry.Open();
                        input.CopyTo(destination, 64 * 1024);
                    }
                }
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { /* best effort 尽力清理 */ }
        }
    }
}