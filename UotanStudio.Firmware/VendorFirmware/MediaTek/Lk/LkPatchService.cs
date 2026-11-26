using System.Globalization;
using System.Text;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.VendorFirmware.MediaTek.Lk;

/// <summary>
/// Applies the documented LK binary patches and, when explicitly requested, rewrites the <c>cert2</c>
/// header/image digests. Every run writes a new file; the source image is never modified in place.
/// <para>应用有记录的 LK 二进制补丁，并在显式请求时重写 <c>cert2</c> 的头部/镜像摘要。每次运行都写出新文件，
/// 绝不原地修改源镜像。</para>
/// </summary>
public static class LkPatchService
{
    /// <summary>
    /// The default patch set, mirroring the categories documented by the reference tool.
    /// <para>默认补丁集，对应参考工具记录的类别。</para>
    /// </summary>
    public static IReadOnlyList<LkBinaryPatch> DefaultPatches { get; } =
    [
        new("fastboot", "2DE9F04FADF5AC5D", "00207047"),
        new("fastboot", "F0B5ADF5925D", "00207047"),
        new("dm_verity", "30B583B002AB0022", "00207047"),
        new("orange_state", "08B50A4B7B441B681B68022B", "00207047"),
        new("orange_state", "08B50E4B7B441B681B68022B", "00207047"),
        new("red_state", "F0B5002489B0", "00207047"),
    ];

    /// <summary>
    /// Gets the category names offered by the default patch set.
    /// <para>获取默认补丁集提供的类别名称。</para>
    /// </summary>
    public static IReadOnlyList<string> Categories { get; } =
        [.. DefaultPatches.Select(static patch => patch.Category).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Applies the selected patches and writes a new image.
    /// <para>应用所选补丁并写出新镜像。</para>
    /// </summary>
    /// <param name="image">Parsed source image.<para>已解析的源镜像。</para></param>
    /// <param name="outputPath">Destination path; must differ from the source.<para>目标路径；必须与源不同。</para></param>
    /// <param name="options">Patch options.<para>修补选项。</para></param>
    /// <param name="cancellationToken">Cancels the run.<para>取消本次运行。</para></param>
    /// <returns>The written output, backup and the reported changes.<para>已写出的输出、备份与已报告变更。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> or <paramref name="options"/> is <see langword="null"/>.<para><paramref name="image"/> 或 <paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="outputPath"/> is blank or equals the source path.<para><paramref name="outputPath"/> 为空白或与源路径相同。</para></exception>
    /// <exception cref="InvalidDataException">A needle is missing or ambiguous, or a certificate cannot be rewritten.<para>needle 缺失或有歧义，或证书无法重写。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.<para>已请求取消。</para></exception>
    public static async Task<LkPatchResult> PatchAsync(
        LkImage image,
        string outputPath,
        LkPatchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var sourcePath = image.FilePath;
        var destination = Path.GetFullPath(outputPath);
        if (string.Equals(destination, Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The patched image must be written to a new path.", nameof(outputPath));
        }

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var changes = new List<LkPatchChange>();
        var temporary = destination + ".tmp";
        try
        {
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var selected = SelectCategories(options.Categories);
            await ApplyBinaryPatchesAsync(temporary, image, selected, options, changes, cancellationToken).ConfigureAwait(false);
            if (options.ApplyCertBypass)
            {
                await ApplyCertificateBypassAsync(temporary, image, changes, cancellationToken).ConfigureAwait(false);
            }

            string? backup = null;
            if (options.CreateBackup)
            {
                backup = BuildBackupPath(sourcePath);
                File.Copy(sourcePath, backup, overwrite: false);
            }

            File.Move(temporary, destination, overwrite: true);
            return new LkPatchResult(destination, backup, changes);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static IReadOnlyList<LkBinaryPatch> SelectCategories(IReadOnlyList<string> requested)
    {
        if (requested.Count == 0)
        {
            return DefaultPatches;
        }

        var accepted = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
        var selected = DefaultPatches.Where(patch => accepted.Contains(patch.Category)).ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidDataException("No known LK patch category was selected.");
        }

        return selected;
    }

    private static async Task ApplyBinaryPatchesAsync(
        string path,
        LkImage image,
        IReadOnlyList<LkBinaryPatch> patches,
        LkPatchOptions options,
        List<LkPatchChange> changes,
        CancellationToken cancellationToken)
    {
        _ = options;
        var targets = image.Entries
            .Where(static entry => entry.Kind != LkImageEntryKind.Certificate)
            .ToArray();
        if (targets.Length == 0)
        {
            throw new InvalidDataException("The LK image carries no patchable partition.");
        }

        foreach (var patch in patches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var needle = Convert.FromHexString(patch.Needle);
            var replacement = Convert.FromHexString(patch.Replacement);
            if (replacement.Length == 0 || replacement.Length > needle.Length)
            {
                throw new InvalidDataException(
                    $"Patch '{patch.Category}' must replace between 1 and {needle.Length} leading bytes.");
            }

            var applied = false;
            foreach (var entry in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hits = FindOccurrences(path, entry, needle, cancellationToken);
                if (hits.Count == 0)
                {
                    continue;
                }

                if (hits.Count > 1)
                {
                    throw new InvalidDataException(
                        $"Patch '{patch.Category}' matches {hits.Count} locations in '{entry.Name}'; refusing an ambiguous write.");
                }

                var offset = hits[0];
                await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    stream.Position = offset;
                    await stream.WriteAsync(replacement, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                changes.Add(new LkPatchChange(patch.Category, entry.Name, offset,
                    $"{patch.Needle} -> {patch.Replacement}"));
                applied = true;
            }

            _ = applied;
        }
    }

    private static List<long> FindOccurrences(string path, LkImageEntry entry, byte[] needle, CancellationToken cancellationToken)
    {
        var hits = new List<long>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        stream.Position = entry.DataOffset;
        var window = new byte[Math.Min(entry.DataSize, 4L * 1024 * 1024)];
        if (window.Length < needle.Length)
        {
            return hits;
        }

        var carry = 0;
        long windowBase = entry.DataOffset;
        var remaining = entry.DataSize;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = (int)Math.Min(window.Length - carry, remaining);
            if (want <= 0)
            {
                break;
            }

            var read = stream.Read(window, carry, want);
            if (read <= 0)
            {
                break;
            }

            remaining -= read;
            var total = carry + read;
            var scan = window.AsSpan(0, total);
            var searchStart = 0;
            while (true)
            {
                var found = scan[searchStart..].IndexOf(needle);
                if (found < 0)
                {
                    break;
                }

                hits.Add(windowBase + searchStart + found);
                searchStart += found + 1;
            }

            if (total <= needle.Length - 1)
            {
                carry = total;
                continue;
            }

            var retained = needle.Length - 1;
            scan[^retained..].CopyTo(window);
            windowBase += total - retained;
            carry = retained;
        }

        return hits;
    }

    private static async Task ApplyCertificateBypassAsync(
        string path,
        LkImage image,
        List<LkPatchChange> changes,
        CancellationToken cancellationToken)
    {
        var certificates = image.Entries
            .Where(static entry => entry.Kind == LkImageEntryKind.Certificate)
            .ToArray();
        if (certificates.Length == 0)
        {
            changes.Add(new LkPatchChange("cert_bypass", "-", -1, "no certificate entry"));
            return;
        }

        foreach (var certificate in certificates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = image.Entries.FirstOrDefault(entry =>
                entry.Kind != LkImageEntryKind.Certificate
                && string.Equals(entry.CertificateName, certificate.Name, StringComparison.Ordinal));
            if (owner is null)
            {
                continue;
            }

            var der = new byte[certificate.DataSize];
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                stream.Position = certificate.DataOffset;
                await stream.ReadExactlyAsync(der, cancellationToken).ConfigureAwait(false);
            }

            var headerHash = ComputeHeaderHash(path, owner, cancellationToken);
            var dataHash = ComputePaddedDataHash(path, owner, cancellationToken);
            var rewritten = LkCertificateDigestRewriter.ReplaceDigests(der, headerHash, dataHash);
            if (rewritten.Length != der.Length)
            {
                throw new InvalidDataException("The rewritten certificate changed length; refusing to write.");
            }

            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                stream.Position = certificate.DataOffset;
                await stream.WriteAsync(rewritten, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            changes.Add(new LkPatchChange("cert_bypass", certificate.Name, certificate.DataOffset,
                $"header {Convert.ToHexString(headerHash)}, data {Convert.ToHexString(dataHash)}"));
        }
    }

    private static byte[] ComputeHeaderHash(string path, LkImageEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var header = new byte[LkImageConstants.HeaderSize];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
        stream.Position = entry.HeaderOffset;
        stream.ReadExactly(header);
        return Sha256.ComputeHash(header);
    }

    private static byte[] ComputePaddedDataHash(string path, LkImageEntry entry, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        stream.Position = entry.DataOffset;
        using var hash = Sha256.CreateAccumulator();
        var buffer = new byte[81920];
        var remaining = entry.DataSize;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = (int)Math.Min(buffer.Length, remaining);
            var read = stream.Read(buffer, 0, want);
            if (read <= 0)
            {
                throw new InvalidDataException("An LK entry payload ended before its declared length.");
            }

            hash.Append(buffer.AsSpan(0, read));
            remaining -= read;
        }

        var padding = (16 - (int)(entry.DataSize % 16)) % 16;
        if (padding > 0)
        {
            hash.Append(new byte[padding]);
        }

        return hash.GetHashAndReset();
    }

    private static string BuildBackupPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(directory, $"{stem}_backup_{stamp}{extension}");
        var counter = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{stem}_backup_{stamp}_{counter}{extension}");
            counter++;
        }

        return candidate;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static string DescribeEntry(LkImageEntry entry)
    {
        var builder = new StringBuilder();
        builder.Append(entry.Name).Append(' ')
            .Append(entry.DataSize.ToString(CultureInfo.InvariantCulture)).Append(" bytes @ 0x")
            .Append(entry.MemoryAddress.ToString("X8", CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}