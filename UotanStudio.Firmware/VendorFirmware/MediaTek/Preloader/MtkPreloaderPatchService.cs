using System.Globalization;

namespace UotanStudio.Firmware.VendorFirmware.MediaTek.Preloader;

/// <summary>
/// Applies the documented preloader fastboot-unlock rearrangement: the code starting at the declared offset is
/// moved to <c>0x2000</c>, the BRLYT offset bytes are rewritten, the flag block is copied to <c>0x1000</c> and
/// the lock byte at <c>0x104C</c> is cleared. The source region is never modified: the patch writes a new file
/// and can keep a backup beside it.
/// <para>应用有记录的预加载器 fastboot 解锁重排：把声明偏移起的代码搬到 <c>0x2000</c>，重写 BRLYT 偏移字节，
/// 复制标志块到 <c>0x1000</c> 并清零 <c>0x104C</c> 的锁字节。源区域绝不修改：补丁写出新文件，并可在旁保留备份。</para>
/// </summary>
public static class MtkPreloaderPatchService
{
    /// <summary>
    /// Writes an unlocked preloader to a new path.
    /// <para>把解锁后的预加载器写到新路径。</para>
    /// </summary>
    /// <param name="image">Parsed source region.<para>已解析的源区域。</para></param>
    /// <param name="outputPath">Destination path; must differ from the source.<para>目标路径；必须与源不同。</para></param>
    /// <param name="options">Patch options.<para>修补选项。</para></param>
    /// <param name="cancellationToken">Cancels the run.<para>取消本次运行。</para></param>
    /// <returns>The written output, backup and change notes.<para>已写输出、备份与变更说明。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> or <paramref name="options"/> is <see langword="null"/>.<para><paramref name="image"/> 或 <paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="outputPath"/> is blank or equals the source path.<para><paramref name="outputPath"/> 为空白或与源路径相同。</para></exception>
    /// <exception cref="InvalidDataException">The region is not patchable.<para>区域不可修补。</para></exception>
    /// <exception cref="IOException">The move or write failed.<para>搬移或写入失败。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.<para>已请求取消。</para></exception>
    public static async Task<MtkPreloaderPatchResult> PatchAsync(
        MtkPreloaderImage image,
        string outputPath,
        MtkPreloaderPatchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var sourcePath = image.FilePath;
        var destination = Path.GetFullPath(outputPath);
        if (string.Equals(destination, Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The patched preloader must be written to a new path.", nameof(outputPath));
        }

        MtkPreloaderReader.ValidatePatchable(image);
        var flagBlock = image.FlagBlock!;
        var notes = new List<string>();
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = destination + ".tmp";
        try
        {
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var size = image.FileSize;
            var codeOffset = image.CodeOffset;
            var moveLength = size - MtkPreloaderConstants.TrailerReserve - codeOffset;
            if (moveLength <= 0)
            {
                throw new InvalidDataException("The preloader code range is empty.");
            }

            var flagBytes = new byte[MtkPreloaderConstants.FlagBlockLength];
            await using (var flagSource = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            {
                flagSource.Position = flagBlock.Offset;
                await flagSource.ReadExactlyAsync(flagBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            var codeStaging = temporary + ".code";
            await using (var staged = new FileStream(codeStaging, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await using (var source = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                var buffer = new byte[Math.Min(moveLength, 1024 * 1024)];
                var remaining = moveLength;
                source.Position = codeOffset;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = (int)Math.Min(buffer.Length, remaining);
                    await source.ReadExactlyAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
                    await staged.WriteAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
                    remaining -= chunk;
                }
            }

            await using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            {
                var zeros = new byte[Math.Min(size - codeOffset, 1024 * 1024)];
                var zeroRemaining = size - codeOffset;
                while (zeroRemaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = (int)Math.Min(zeros.Length, zeroRemaining);
                    stream.Position = size - zeroRemaining;
                    await stream.WriteAsync(zeros.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
                    zeroRemaining -= chunk;
                }

                notes.Add(string.Format(CultureInfo.InvariantCulture, "code range 0x{0:X}:0x{1:X} zeroed", codeOffset, size));

                await using (var staged = new FileStream(codeStaging, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                {
                    var buffer = new byte[Math.Min(moveLength, 1024 * 1024)];
                    var remaining = moveLength;
                    var writePosition = MtkPreloaderConstants.CodeTarget;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var chunk = (int)Math.Min(buffer.Length, remaining);
                        await staged.ReadExactlyAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
                        stream.Position = writePosition;
                        await stream.WriteAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
                        writePosition += chunk;
                        remaining -= chunk;
                    }
                }

                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "code moved 0x{0:X}:0x{1:X} -> 0x{2:X}:0x{3:X}", codeOffset, codeOffset + moveLength,
                    MtkPreloaderConstants.CodeTarget, MtkPreloaderConstants.CodeTarget + moveLength));

                await WriteByteAsync(stream, MtkPreloaderConstants.CodeOffsetByte, 0x20, cancellationToken).ConfigureAwait(false);
                await WriteByteAsync(stream, MtkPreloaderConstants.CodeOffsetHighByte, 0x20, cancellationToken).ConfigureAwait(false);
                await WriteByteAsync(stream, MtkPreloaderConstants.CodeOffsetLow1, 0x10, cancellationToken).ConfigureAwait(false);
                await WriteByteAsync(stream, MtkPreloaderConstants.CodeOffsetLow2, 0x10, cancellationToken).ConfigureAwait(false);
                await WriteByteAsync(stream, MtkPreloaderConstants.CodeOffsetLow3, 0x10, cancellationToken).ConfigureAwait(false);
                await WriteByteAsync(stream, MtkPreloaderConstants.CodeOffsetLow4, 0x10, cancellationToken).ConfigureAwait(false);
                notes.Add("BRLYT offset rewritten to 0x2000");

                var target = MtkPreloaderConstants.FlagBlockTarget;
                if (target + flagBytes.Length <= size)
                {
                    stream.Position = target;
                    await stream.WriteAsync(flagBytes, cancellationToken).ConfigureAwait(false);
                    notes.Add(string.Format(CultureInfo.InvariantCulture,
                        "flag block copied 0x{0:X} -> 0x{1:X}", flagBlock.Offset, target));
                }

                if (options.UnlockFastboot && MtkPreloaderConstants.LockByteWriteOffset < size)
                {
                    await WriteByteAsync(stream, MtkPreloaderConstants.LockByteWriteOffset, 0x00, cancellationToken).ConfigureAwait(false);
                    notes.Add(string.Format(CultureInfo.InvariantCulture,
                        "fastboot lock state 0x{0:X2} -> 0x00", flagBlock.LockState));
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            string? backup = null;
            if (options.CreateBackup)
            {
                backup = BuildBackupPath(sourcePath);
                File.Copy(sourcePath, backup, overwrite: false);
            }

            File.Move(temporary, destination, overwrite: true);
            return new MtkPreloaderPatchResult(destination, backup, notes);
        }
        finally
        {
            TryDelete(temporary);
            TryDelete(temporary + ".code");
        }
    }

    private static async Task WriteByteAsync(Stream stream, long offset, byte value, CancellationToken cancellationToken)
    {
        stream.Position = offset;
        await stream.WriteAsync(new[] { value }.AsMemory(), cancellationToken).ConfigureAwait(false);
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
}