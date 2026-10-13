using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ErofsCorpusCompactIndexRebuildTests
{
    private const string CorpusVariable = "UOTAN_EROFS_CORPUS";
    private const int CompactIndexLayout = 3;
    private const int MaximumRebuildAttempts = 8;

    [Fact]
    public async Task PreservedRebuild_OfCorpusCompactIndexImage_ReExtractsEveryFileByteIdentical()
    {
        var directory = Environment.GetEnvironmentVariable(CorpusVariable);
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory),
            $"Set {CorpusVariable} to a directory of real EROFS images to run the compact-index preserved-rebuild acceptance.");

        var images = Directory.GetFiles(directory!, "*.img")
            .OrderBy(path => new FileInfo(path).Length)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(images);

        var attempts = new List<string>();
        var skipped = new List<string>();
        var rebuiltImages = 0;
        var verifiedFiles = 0;
        long verifiedBytes = 0;

        foreach (var imagePath in images)
        {
            var name = Path.GetFileName(imagePath);
            string[] compactFiles;
            try
            {
                compactFiles = CompactIndexFiles(imagePath);
            }
            catch (Exception ex) when (ex is FormatException or FileSystemException or NotSupportedException
                or InvalidOperationException or EndOfStreamException)
            {
                skipped.Add($"{name}: {ex.GetType().Name} {ex.Message}");
                continue;
            }

            if (compactFiles.Length == 0)
            {
                skipped.Add($"{name}: no Layout: {CompactIndexLayout} inode");
                continue;
            }

            if (attempts.Count >= MaximumRebuildAttempts)
            {
                skipped.Add($"{name}: rebuild attempt budget reached");
                continue;
            }

            attempts.Add(name);
            using var temp = new TempDirectory();
            var extracted = Path.Combine(temp.Path, "extracted");
            var rebuiltPath = Path.Combine(temp.Path, "rebuilt.img");
            var handler = new ErofsFormatHandler();
            var unpacked = await handler.UnpackAsync(
                imagePath, extracted, cancellationToken: TestContext.Current.CancellationToken);
            if (!unpacked.Success)
            {
                skipped.Add($"{name}: unpack failed: {unpacked.Summary}");
                continue;
            }

            var packed = await handler.PackAsync(
                extracted, rebuiltPath, TestContext.Current.CancellationToken);
            if (!packed.Success)
            {
                skipped.Add($"{name}: preserved rebuild failed: {packed.Summary}");
                continue;
            }

            var rebuiltCompact = CompactIndexFiles(rebuiltPath);
            Assert.Equal(
                compactFiles.Order(StringComparer.Ordinal).ToArray(),
                rebuiltCompact.Order(StringComparer.Ordinal).ToArray());
            Assert.NotEmpty(rebuiltCompact);

            using var source = new ErofsParser(File.OpenRead(imagePath));
            using var rebuilt = new ErofsParser(File.OpenRead(rebuiltPath));
            var rebuiltPaths = rebuilt.EnumerateFiles()
                .Where(entry => !entry.IsDirectory)
                .Select(entry => entry.Path)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var entry in source.EnumerateFiles())
            {
                if (entry.IsDirectory)
                {
                    continue;
                }

                Assert.Contains(entry.Path, rebuiltPaths);
                var sourceBytes = source.ReadFileBytes(entry.Path);
                var rebuiltBytes = rebuilt.ReadFileBytes(entry.Path);
                Assert.Equal(sourceBytes.Length, rebuiltBytes.Length);
                var sourceDigest = MD5.HashData(sourceBytes);
                var rebuiltDigest = MD5.HashData(rebuiltBytes);
                Assert.True(
                    sourceDigest.AsSpan().SequenceEqual(rebuiltDigest),
                    $"MD5 mismatch for '{entry.Path}' in '{name}': source " +
                    $"{Convert.ToHexString(sourceDigest)}, rebuilt {Convert.ToHexString(rebuiltDigest)}.");
                verifiedFiles++;
                verifiedBytes += sourceBytes.Length;
            }

            rebuiltImages++;
            break;
        }

        Assert.True(
            rebuiltImages > 0,
            $"No corpus image carrying a Layout: {CompactIndexLayout} inode could be preserved-rebuilt. " +
            $"Attempted: {string.Join(" | ", attempts)}. Skipped: {string.Join(" | ", skipped)}");
        Assert.True(verifiedFiles > 0, $"The preserved rebuild of '{attempts[^1]}' compared no file.");
        Assert.True(verifiedBytes > 0, $"The preserved rebuild of '{attempts[^1]}' compared no content byte.");
    }

    private static string[] CompactIndexFiles(string imagePath)
    {
        var compact = new List<string>();
        using var parser = new ErofsParser(File.OpenRead(imagePath));
        using var handle = File.OpenHandle(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = RandomAccess.GetLength(handle);
        foreach (var entry in parser.EnumerateFiles())
        {
            if (entry.IsDirectory || (entry.Nid >> 63) != 0)
            {
                continue;
            }

            var offset = parser.Superblock.GetInodeOffset(entry.Nid);
            if (offset < 0 || offset + 2 > length)
            {
                continue;
            }

            var head = new byte[2];
            RandomAccess.Read(handle, head, offset);
            if (((BinaryPrimitives.ReadUInt16LittleEndian(head) >> 1) & 7) == CompactIndexLayout)
            {
                compact.Add(entry.Path);
            }
        }

        return [.. compact];
    }
}