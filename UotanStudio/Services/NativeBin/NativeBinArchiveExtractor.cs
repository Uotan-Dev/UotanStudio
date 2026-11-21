using System.Formats.Tar;
using UotanStudio.Compress;
using UotanStudio.Compress.Zip;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Services.NativeBin;

/// <summary>
/// Extracts the libusb shared library from an upstream release archive (ZIP for Windows and macOS,
/// gzip-compressed tar for Linux) into the cache file, verifying the extracted bytes against the
/// expected SHA-256 so an untrusted mirror or archive layout cannot install a different binary.
/// <para>从 libusb 上游发布归档（Windows/macOS 为 ZIP，Linux 为 gzip 压缩的 tar）提取共享库到缓存文件，
/// 并对提取出的字节做期望 SHA-256 校验，使不可信镜像或异常归档布局无法装入不同的二进制。</para>
/// </summary>
/// <remarks>
/// Upstream archives carry the library under release-specific directories and, outside Windows,
/// under unversioned file names; candidates are matched by file name only, renamed on extraction
/// to the cache name, and accepted only when their SHA-256 equals the catalog expectation. The
/// archive stream is treated as untrusted input: entry paths are validated and every payload copy
/// is bounded.
/// <para>上游归档把库放在发布专属目录里，且除 Windows 外都是无版本文件名；候选仅按文件名匹配，
/// 提取时重命名为缓存名，且只有 SHA-256 等于清单期望值才接受。归档流按不可信输入处理：
/// 校验条目路径，所有负载复制都有字节上限。</para>
/// </remarks>
public static class NativeBinArchiveExtractor
{
    /// <summary>Upper bound for one archive entry payload; the libusb library is far smaller.
    /// <para>单个归档条目负载上限；libusb 库远小于该值。</para></summary>
    public const long MaximumEntryBytes = 4 * 1024 * 1024;

    private const int CopyBufferBytes = 128 * 1024;

    /// <summary>
    /// Extracts the matching library from <paramref name="archive"/> and atomically replaces
    /// <paramref name="destinationPath"/> when a candidate whose SHA-256 equals
    /// <paramref name="expectedSha256"/> is found.
    /// <para>从 <paramref name="archive"/> 中提取匹配的库；当存在 SHA-256 等于
    /// <paramref name="expectedSha256"/> 的候选时，原子替换 <paramref name="destinationPath"/>。</para>
    /// </summary>
    /// <param name="archive">Readable, seekable archive stream; it is neither closed nor repositioned beyond consumption.
    /// <para>可读、可定位的归档流；除正常消费外不关闭、不回位。</para></param>
    /// <param name="rid">Target runtime identifier, for instance <c>win-x64</c>; selects the archive format and inner file names.
    /// <para>目标运行标识，例如 <c>win-x64</c>；决定归档格式与内层文件名。</para></param>
    /// <param name="expectedSha256">Lowercase 64-char SHA-256 hex digest the extracted library must match.
    /// <para>提取出的库必须匹配的小写 64 字符 SHA-256 十六进制摘要。</para></param>
    /// <param name="destinationPath">Absolute cache file path; created if missing and replaced atomically on success.
    /// <para>缓存文件绝对路径；缺失时创建，成功时原子替换。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns><c>true</c> when a candidate matched and was installed; <c>false</c> when the archive is invalid
    /// or contains no candidate with the expected digest.
    /// <para>存在匹配候选并已安装时为 <c>true</c>；归档非法或没有摘要匹配的候选时为 <c>false</c>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="archive"/> is <see langword="null"/>.
    /// <para><paramref name="archive"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rid"/> has no catalog mapping.
    /// <para><paramref name="rid"/> 没有清单映射。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="destinationPath"/> is empty or <paramref name="expectedSha256"/> is not a 64-char hex digest.
    /// <para><paramref name="destinationPath"/> 为空，或 <paramref name="expectedSha256"/> 不是 64 字符十六进制摘要。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.
    /// <para><paramref name="ct"/> 被取消。</para></exception>
    public static async Task<bool> TryExtractAsync(
        Stream archive, string rid, string expectedSha256, string destinationPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("A destination path is required.", nameof(destinationPath));
        }

        if (expectedSha256.Length != 64 || !Uri.IsHexDigit(expectedSha256[0])
            || expectedSha256.Any(static c => !Uri.IsHexDigit(c)))
        {
            throw new ArgumentException("A 64-character hex SHA-256 digest is required.", nameof(expectedSha256));
        }

        if (NativeBinCatalog.ByRid(rid) is null)
        {
            throw new ArgumentOutOfRangeException(nameof(rid), $"No libusb entry for RID '{rid}'.");
        }

        var temporaryPath = destinationPath + $".extract-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (rid.StartsWith("linux", StringComparison.Ordinal))
            {
                if (!await TryExtractFromTarGzipAsync(archive, rid, expectedSha256, temporaryPath, ct).ConfigureAwait(false))
                {
                    return false;
                }
            }
            else if (!await TryExtractFromZipAsync(archive, rid, expectedSha256, temporaryPath, ct).ConfigureAwait(false))
            {
                return false;
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Returns the file names upstream archives may carry for a RID.
    /// <para>返回上游归档可能为某 RID 携带的文件名。</para></summary>
    private static string[] GetInnerFileNames(string rid) => rid.StartsWith("osx", StringComparison.Ordinal)
        ? ["libusb-1.0.dylib", "libusb-1.0.0.dylib"]
        : rid.StartsWith("linux", StringComparison.Ordinal)
            ? ["libusb-1.0.so", "libusb-1.0.so.0"]
            : ["libusb-1.0.dll"];

    private static async Task<bool> TryExtractFromZipAsync(
        Stream archive, string rid, string expectedSha256, string temporaryPath, CancellationToken ct)
    {
        var innerNames = GetInnerFileNames(rid);
        using var zipArchive = ZipContainer.OpenRead(archive, leaveOpen: true);
        foreach (var entry in zipArchive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Length == 0 || !MatchesInnerName(entry.FullName, innerNames))
            {
                continue;
            }

            var copied = false;
            await using (var target = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                try
                {
                    ZipContainer.CopyEntryTo(entry, target, MaximumEntryBytes, ct);
                    copied = true;
                }
                catch (InvalidDataException)
                {
                    copied = false;
                }
            }

            if (!copied)
            {
                continue;
            }

            if (await FileMatchesAsync(temporaryPath, expectedSha256, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> TryExtractFromTarGzipAsync(
        Stream archive, string rid, string expectedSha256, string temporaryPath, CancellationToken ct)
    {
        var innerNames = GetInnerFileNames(rid);
        await using var decompressed = CompressionService.CreateDecompressionStream(archive, CompressionFormat.Gzip);
        using var reader = new TarReader(decompressed);
        while (reader.GetNextEntry() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.EntryType != TarEntryType.RegularFile
                || entry.DataStream is not { } data
                || data.Length > MaximumEntryBytes
                || !MatchesInnerName(entry.Name, innerNames))
            {
                continue;
            }

            var copied = false;
            await using (var target = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                try
                {
                    await CopyBoundedAsync(data, target, ct).ConfigureAwait(false);
                    copied = true;
                }
                catch (InvalidDataException)
                {
                    copied = false;
                }
            }

            if (!copied)
            {
                continue;
            }

            if (await FileMatchesAsync(temporaryPath, expectedSha256, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Matches an archive entry path by its final segment; entries whose path is not safely relative are rejected.
    /// <para>按最后一段路径匹配归档条目；路径不是安全相对路径的条目直接拒绝。</para></summary>
    private static bool MatchesInnerName(string entryPath, string[] innerNames)
    {
        try
        {
            ZipContainer.NormalizeEntryPath(entryPath);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var name = entryPath.Replace('\\', '/').Split('/').Last();
        return innerNames.Contains(name, StringComparer.Ordinal);
    }

    private static async Task CopyBoundedAsync(Stream source, Stream target, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferBytes];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            copied += read;
            if (copied > MaximumEntryBytes)
            {
                throw new InvalidDataException("The archive entry exceeds the libusb payload bound.");
            }
        }
    }

    private static async Task<bool> FileMatchesAsync(string path, string expectedSha256, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await Sha256.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}