using System.IO.Compression;
using UotanStudio.Firmware.Core.IO;

namespace UotanStudio.Firmware.AndroidMeta.Dex;

/// <summary>
/// The kind of an entry inside a JAR/ZIP container.
/// <para>JAR/ZIP 容器内 DEX 与资源条目的种类。</para>
/// </summary>
public enum JarEntryKind
{
    /// <summary>
    /// A DEX bytecode entry (classes*.dex).
    /// <para>DEX 字节码条目（classes*.dex）。</para>
    /// </summary>
    Dex,

    /// <summary>
    /// A v1/JAR signature artifact (manifest, signature or certificate under META-INF); read-only.
    /// <para>v1/JAR 签名工件（META-INF 下的清单、签名与证书），只读展示。</para>
    /// </summary>
    Signature,

    /// <summary>
    /// A plain resource entry that can be extracted and replaced.
    /// <para>普通资源条目，可导出与替换。</para>
    /// </summary>
    Resource,
}

/// <summary>
/// Summary information of an entry inside a JAR/ZIP container.
/// <para>JAR/ZIP 容器内的条目摘要信息。</para>
/// </summary>
/// <param name="Name">
/// The full entry name (ZIP-internal path, forward-slash separated).
/// <para>条目完整名称（ZIP 内路径，正斜杠分隔）。</para>
/// </param>
/// <param name="Length">
/// The uncompressed entry size in bytes.
/// <para>条目解压后的字节数。</para>
/// </param>
/// <param name="Kind">
/// The entry kind.
/// <para>条目种类。</para>
/// </param>
public sealed record JarEntryInfo(string Name, long Length, JarEntryKind Kind);

/// <summary>
/// Reads, writes back and replaces DEX and resource entries inside a JAR/ZIP container (.jar/.apk). All entry reads are bounded; write-back rebuilds the ZIP through a same-directory temporary file and commits atomically. META-INF signatures are read-only, since any write invalidates existing signatures.
/// <para>读取、回写与替换 JAR/ZIP 容器（.jar/.apk）内的 DEX 与资源条目。所有条目读取有界；回写通过同目录临时文件重建 ZIP 并原子提交。META-INF 签名只读，任何写入都会使既有签名失效。</para>
/// </summary>
public static class JarDexContainer
{
    /// <summary>
    /// The maximum uncompressed size of a single entry (256 MiB, aligned with the
    /// DEX/AXML/ARSC metadata cap).
    /// <para>单个条目的解压大小上限（256 MiB，与 DEX/AXML/ARSC 元数据上限口径一致）。</para>
    /// </summary>
    public const long MaxEntryBytes = 256L * 1024 * 1024;

    /// <summary>
    /// The maximum number of entries in one container.
    /// <para>容器内条目数量上限。</para>
    /// </summary>
    public const int MaxEntryCount = 65536;

    /// <summary>
    /// Whether the given path extension is a supported DEX container (.jar/.apk).
    /// <para>判断给定路径扩展名是否为受支持的 DEX 容器（.jar/.apk）。</para>
    /// </summary>
    /// <param name="path">
    /// A file name or full path.
    /// <para>文件名或完整路径。</para>
    /// </param>
    /// <returns><see langword="true"/> for a <c>.jar</c> or <c>.apk</c> extension, compared without
    /// case sensitivity; a <see langword="null"/> value or an extension-less name gives
    /// <see langword="false"/>. <para>扩展名为 <c>.jar</c> 或 <c>.apk</c> 时返回
    /// <see langword="true"/>（比较忽略大小写）；值为 <see langword="null"/> 或没有扩展名时返回
    /// <see langword="false"/>。</para></returns>
    public static bool IsContainerPath(string? path)
    {
        var ext = Path.GetExtension(path ?? string.Empty);
        return ext.Equals(".jar", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".apk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enumerates all entries in the container (name-ordered); throws a format exception
    /// when the entry count exceeds the cap.
    /// <para>枚举容器内全部条目（按名称排序）；超出数量上限时抛出格式异常。</para>
    /// </summary>
    /// <param name="path">
    /// The container file path.
    /// <para>容器文件路径。</para>
    /// </param>
    /// <returns>The list of entry summaries. <para>条目摘要列表。</para></returns>
    /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.
    /// <para><paramref name="path"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The container holds more than
    /// <see cref="MaxEntryCount"/> entries or its central directory cannot be read.
    /// <para>容器条目数超过 <see cref="MaxEntryCount"/>，或中央目录无法读取。</para></exception>
    public static IReadOnlyList<JarEntryInfo> ListEntries(string path)
    {
        using var stream = OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaxEntryCount)
        {
            throw new InvalidDataException(
                $"容器条目数 {archive.Entries.Count} 超过上限 {MaxEntryCount}。/ Container entry count exceeds the cap.");
        }

        return [.. archive.Entries
            .Select(e => new JarEntryInfo(e.FullName, e.Length, Classify(e.FullName)))
            .OrderBy(e => e.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Lists all DEX entry names in the container (root-level classes*.dex, name-ordered).
    /// <para>列出容器内全部 DEX 条目名（根级 classes*.dex，按名称自然排序）。</para>
    /// </summary>
    /// <param name="path">
    /// The container file path.
    /// <para>容器文件路径。</para>
    /// </param>
    /// <returns>The list of DEX entry names. <para>DEX 条目名列表。</para></returns>
    /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.
    /// <para><paramref name="path"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The container holds more than
    /// <see cref="MaxEntryCount"/> entries or its central directory cannot be read.
    /// <para>容器条目数超过 <see cref="MaxEntryCount"/>，或中央目录无法读取。</para></exception>
    public static IReadOnlyList<string> ListDexEntries(string path)
    {
        using var stream = OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaxEntryCount)
        {
            throw new InvalidDataException(
                $"容器条目数 {archive.Entries.Count} 超过上限 {MaxEntryCount}。/ Container entry count exceeds the cap.");
        }

        return [.. archive.Entries
            .Where(e => IsDexEntryName(e.FullName))
            .Select(e => e.FullName)
            .OrderBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether the entry name is a root-level classes*.dex (classes.dex, classes2.dex, ...).
    /// <para>判断条目名是否为根级 classes*.dex（允许 classes.dex、classes2.dex…）。</para>
    /// </summary>
    /// <param name="entryName">
    /// The entry name.
    /// <para>条目名。</para>
    /// </param>
    /// <returns><see langword="true"/> only for such a name; a <see langword="null"/> value, a name
    /// containing <c>/</c>, or extra characters between <c>classes</c> and <c>.dex</c> that are not
    /// ASCII digits give <see langword="false"/>. <para>只有满足上述名称时返回
    /// <see langword="true"/>；值为 <see langword="null"/>、名称包含 <c>/</c>，或 <c>classes</c> 与
    /// <c>.dex</c> 之间存在非 ASCII 数字字符时返回 <see langword="false"/>。</para></returns>
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
    /// Whether the entry is a v1/JAR signature artifact (manifest, SF, RSA/DSA/EC and related
    /// standard signature extensions under META-INF).
    /// <para>判断条目是否为 v1/JAR 签名工件（META-INF 下的清单、SF、RSA/DSA/EC 与 SF 以外的
    /// 标准签名扩展）。</para>
    /// </summary>
    /// <param name="entryName">
    /// The entry name.
    /// <para>条目名。</para>
    /// </param>
    /// <returns><see langword="true"/> for <c>META-INF/MANIFEST.MF</c> or a <c>META-INF/</c> entry
    /// whose extension is <c>SF</c>, <c>RSA</c>, <c>DSA</c> or <c>EC</c>; comparison ignores case and
    /// a <see langword="null"/> value gives <see langword="false"/>.
    /// <para><c>META-INF/MANIFEST.MF</c>，或扩展名为 <c>SF</c>、<c>RSA</c>、<c>DSA</c>、<c>EC</c>
    /// 的 <c>META-INF/</c> 条目返回 <see langword="true"/>；比较忽略大小写，值为
    /// <see langword="null"/> 时返回 <see langword="false"/>。</para></returns>
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
    /// Reads the full bytes of one entry, with a bounded length.
    /// <para>有界读取容器内指定条目的全部字节。</para>
    /// </summary>
    /// <param name="path">
    /// The container file path.
    /// <para>容器文件路径。</para>
    /// </param>
    /// <param name="entryName">
    /// The entry name.
    /// <para>条目名。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The cancellation token.
    /// <para>取消令牌。</para>
    /// </param>
    /// <returns>The entry bytes. <para>条目字节。</para></returns>
    /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.
    /// <para><paramref name="path"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException"><paramref name="entryName"/> is absent, or its
    /// recorded uncompressed size exceeds <see cref="MaxEntryBytes"/>.
    /// <para>容器中不存在 <paramref name="entryName"/>，或其记录的非压缩大小超过
    /// <see cref="MaxEntryBytes"/>。</para></exception>
    /// <exception cref="EndOfStreamException">The entry produced fewer bytes than its recorded
    /// size. <para>条目实际读出的字节少于记录的大小。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was
    /// cancelled mid-read. <para>读取过程中 <paramref name="cancellationToken"/> 被取消。</para></exception>
    public static byte[] LoadEntry(string path, string entryName, CancellationToken cancellationToken = default)
    {
        using var stream = OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entry = FindEntry(archive, entryName);
        return ReadBounded(entry, entryName, cancellationToken);
    }

    /// <summary>
    /// Writes modified DEX bytes back into one container entry.
    /// <para>把修改后的 DEX 字节写回容器内指定条目。</para>
    /// </summary>
    /// <param name="path">
    /// The container file path.
    /// <para>容器文件路径。</para>
    /// </param>
    /// <param name="entryName">
    /// The entry name to replace.
    /// <para>要替换的条目名。</para>
    /// </param>
    /// <param name="entryBytes">
    /// The new entry bytes (the rebuilt DEX output).
    /// <para>新的条目字节（DEX 重建输出）。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The cancellation token.
    /// <para>取消令牌。</para>
    /// </param>
    public static void WriteBack(string path, string entryName, byte[] entryBytes, CancellationToken cancellationToken = default)
    {
        ReplaceEntry(path, entryName, entryBytes, cancellationToken);
    }

    /// <summary>
    /// Extracts a resource entry to the target file (the directory is created; the write is
    /// bounded and committed atomically).
    /// <para>导出容器内的资源条目到目标文件（目标目录自动创建，写入有界并原子提交）。</para>
    /// </summary>
    /// <param name="path">
    /// The container file path.
    /// <para>容器文件路径。</para>
    /// </param>
    /// <param name="entryName">
    /// The resource entry name.
    /// <para>资源条目名。</para>
    /// </param>
    /// <param name="outputPath">
    /// The output file path.
    /// <para>输出文件路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The cancellation token.
    /// <para>取消令牌。</para>
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
            AtomicPublish.Publish(temporary, outputPath);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    /// <summary>
    /// Replaces a resource entry with the content of a local file (bounded read, atomic
    /// write-back through a temporary file).
    /// <para>用本地文件内容替换容器内资源条目（读取有界，经临时文件原子写回）。</para>
    /// </summary>
    /// <param name="path">
    /// The container file path.
    /// <para>容器文件路径。</para>
    /// </param>
    /// <param name="entryName">
    /// The resource entry name.
    /// <para>资源条目名。</para>
    /// </param>
    /// <param name="sourceFilePath">
    /// The local file providing the replacement content.
    /// <para>替换内容的本地文件路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The cancellation token.
    /// <para>取消令牌。</para>
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
    /// Writes a set of entries (DEX and resources) into a new container (atomic commit via a
    /// temporary file).
    /// <para>把新建条目（含 DEX 与资源）连同全部既有条目写入新容器（临时文件原子提交）。</para>
    /// </summary>
    /// <param name="outputPath">
    /// The output container path.
    /// <para>输出容器路径。</para>
    /// </param>
    /// <param name="entries">
    /// The ordered set of entry names and contents.
    /// <para>条目名与内容的有序集合。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The cancellation token.
    /// <para>取消令牌。</para>
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
            AtomicPublish.Publish(temporary, outputPath);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
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
            AtomicPublish.Publish(temporary, fullPath);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }
}