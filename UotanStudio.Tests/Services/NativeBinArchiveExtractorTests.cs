using System.Formats.Tar;
using System.IO.Compression;
using UotanStudio.Hash.Digests;
using UotanStudio.Services.NativeBin;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

public class NativeBinArchiveExtractorTests
{
    private static readonly byte[] LibraryBytes = [0x7f, 0x45, 0x4c, 0x46, 0x01, 0x02, 0x03, 0x04, 0xde, 0xad, 0xbe, 0xef];

    private static string Sha256Hex(byte[] bytes)
    {
        return Convert.ToHexString(Sha256.ComputeHash(bytes)).ToLowerInvariant();
    }

    private static string Sha256HexFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(Sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    private static byte[] BuildZip(params (string FullName, byte[] Bytes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (fullName, bytes) in entries)
            {
                var entry = zip.CreateEntry(fullName);
                using var writer = entry.Open();
                writer.Write(bytes);
            }
        }

        return stream.ToArray();
    }

    private static byte[] BuildTarGzip(params (string Name, byte[] Bytes)[] entries)
    {
        using var tarStream = new MemoryStream();
        using (var tarWriter = new TarWriter(tarStream, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                var source = new MemoryStream(bytes);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = source,
                };
                tarWriter.WriteEntry(entry);
            }
        }

        tarStream.Position = 0;
        using var gzip = new MemoryStream();
        using (var compressor = new System.IO.Compression.GZipStream(gzip, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            tarStream.CopyTo(compressor);
        }

        return gzip.ToArray();
    }

    [Fact]
    public async Task Zip_MatchingCandidate_InDestinationDirectory_IsExtracted()
    {
        using var temp = new TempDirectory();
        var destination = System.IO.Path.Combine(temp.Path, "libusb-1.0.dll");
        var bytes = BuildZip(
            ("libusb-1.0.30-bin-win-x64/README.txt", [0x00]),
            ("libusb-1.0.30-bin-win-x64/VS2019/MS64/dll/libusb-1.0.dll", LibraryBytes),
            ("libusb-1.0.30-bin-win-x64/MinGW64/dll/libusb-1.0.dll", [0x01, 0x02]));
        using var archive = new MemoryStream(bytes);

        var result = await NativeBinArchiveExtractor.TryExtractAsync(
            archive, "win-x64", Sha256Hex(LibraryBytes), destination, TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(LibraryBytes, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Zip_NoMatchingHash_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        var destination = System.IO.Path.Combine(temp.Path, "libusb-1.0.dll");
        var bytes = BuildZip(("libusb-1.0.30-bin-win-x64/VS2019/MS64/dll/libusb-1.0.dll", LibraryBytes));
        using var archive = new MemoryStream(bytes);

        var wrongDigest = Convert.ToHexString(Sha256.ComputeHash([0x99])).ToLowerInvariant();
        var result = await NativeBinArchiveExtractor.TryExtractAsync(archive, "win-x64", wrongDigest, destination, TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Zip_PathTraversalEntry_IsRejected()
    {
        using var temp = new TempDirectory();
        var destination = System.IO.Path.Combine(temp.Path, "libusb-1.0.dll");
        var bytes = BuildZip(("..\\evil\\libusb-1.0.dll", LibraryBytes));
        using var archive = new MemoryStream(bytes);

        var result = await NativeBinArchiveExtractor.TryExtractAsync(
            archive, "win-x64", Sha256Hex(LibraryBytes), destination, TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task TarGzip_UnversionedEntry_IsRenamedAndExtracted()
    {
        using var temp = new TempDirectory();
        var destination = System.IO.Path.Combine(temp.Path, "nested", "libusb-1.0.so.0");
        var bytes = BuildTarGzip(("libusb-1.0.30-bin-linux-x64/libusb-1.0.so", LibraryBytes));
        using var archive = new MemoryStream(bytes);

        var result = await NativeBinArchiveExtractor.TryExtractAsync(
            archive, "linux-x64", Sha256Hex(LibraryBytes), destination, TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(LibraryBytes, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(Directory.GetFiles(System.IO.Path.GetDirectoryName(destination)!), static f => f.Contains(".extract-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TarGzip_CorruptStream_Throws()
    {
        using var temp = new TempDirectory();
        var destination = System.IO.Path.Combine(temp.Path, "libusb-1.0.so.0");
        using var archive = new MemoryStream([0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeBinArchiveExtractor.TryExtractAsync(
            archive, "linux-x64", Convert.ToHexString(Sha256.ComputeHash(LibraryBytes)).ToLowerInvariant(), destination, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task UnknownRid_Throws()
    {
        using var temp = new TempDirectory();
        var destination = System.IO.Path.Combine(temp.Path, "libusb-1.0.dll");
        using var archive = new MemoryStream([0x00]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NativeBinArchiveExtractor.TryExtractAsync(
            archive, "freebsd-x64", Sha256Hex(LibraryBytes), destination));
    }

    [Fact]
    public void Catalog_Hashes_MatchVendoredSha256Sums()
    {
        var sumsPath = FindRepoFile("UotanStudio.Comm/Native/LibUsb/SHA256SUMS");
        var sums = File.ReadAllLines(sumsPath)
            .Where(static line => line.Length > 0)
            .ToDictionary(
                static line => line.Split("  ", 2)[1].Trim().Replace('\\', '/'),
                static line => line.Split("  ", 2)[0],
                StringComparer.OrdinalIgnoreCase);
        var repoRoot = System.IO.Path.GetDirectoryName(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(
                System.IO.Path.GetDirectoryName(sumsPath))))!;

        foreach (var entry in NativeBinCatalog.All)
        {
            var vendored = System.IO.Path.Combine(
                repoRoot, "UotanStudio.Comm", "Native", "LibUsb", entry.Rid, entry.FileName);
            Assert.True(File.Exists(vendored), $"Missing vendored file for '{entry.Rid}': {vendored}");
            Assert.Equal(Sha256HexFile(vendored), entry.ExpectedSha256);
            Assert.Contains($"{entry.Rid}/{entry.FileName}".Replace('\\', '/'), sums.Keys, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine(directory, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = System.IO.Path.GetDirectoryName(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        }

        throw new FileNotFoundException($"'{relativePath}' was not found above the test output directory.");
    }
}