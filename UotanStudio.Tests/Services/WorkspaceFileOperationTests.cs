using System.IO.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

public class WorkspaceFileOperationTests
{
    [Fact]
    public void TransferListCollisionDoesNotReplaceExistingDat()
    {
        using var temp = new TempDirectory();
        var input = temp.WriteBytes("system.img", Enumerable.Repeat((byte)1, 4096).ToArray());
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(Path.Combine(output, "system.transfer.list"));
        var original = Path.Combine(output, "system.new.dat");
        File.WriteAllBytes(original, [3, 2, 1]);
        Assert.Throws<IOException>(() => ImageOutputService.Build(new(input, false, temp.Path, output), "dat", 3));
        Assert.Equal(new byte[] { 3, 2, 1 }, File.ReadAllBytes(original));
        Assert.Empty(Directory.EnumerateDirectories(output, ".build-*"));
    }

    [Fact]
    public void SparseDatConversionUsesLogicalBytesAndReleasesInput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "system.img");
        using (var sparse = new SparseFile(4096, 4096))
        {
            sparse.AddFillChunk(0x01010101, 4096);
            using var stream = File.Create(input);
            sparse.WriteToStream(stream, sparse: true);
        }
        var output = Path.Combine(temp.Path, "output");
        Assert.True(ImageOutputService.Build(new(input, false, temp.Path, output), "dat", 3).Success);
        Assert.Equal(Enumerable.Repeat((byte)1, 4096), File.ReadAllBytes(Path.Combine(output, "system.new.dat")));
        using var exclusive = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Empty(Directory.EnumerateDirectories(output));
    }

    [Fact]
    public async Task SuccessfulExtractionReplacesContentsAndRemovesBackup()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "boot");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "old"), "old");
        var result = await ExtractionDirectory.ReplaceAsync(target, () =>
        {
            File.WriteAllText(Path.Combine(target, "new"), "new");
            return Task.FromResult(new FirmwareOpResult { Success = true });
        });
        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(target, "old")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "new")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(temp.Path, ".deleted")));
    }

    [Fact]
    public void ExtractDirectoryIsRejectedWithoutDeletingContents()
    {
        using var temp = new TempDirectory();
        var folder = Path.Combine(temp.Path, "odm (2).backup");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "original");
        var paths = new ImageOperationPaths(folder, true, temp.Path, Path.Combine(temp.Path, "output"));
        Assert.Equal("odm (2).backup", paths.Name);
        Assert.Throws<IOException>(paths.ValidateExtraction);
        Assert.Equal("original", File.ReadAllText(Path.Combine(folder, "keep.txt")));
    }

    [Theory]
    [InlineData("images")]
    [InlineData("output")]
    [InlineData(".uotan")]
    [InlineData(".deleted")]
    public void ReservedDirectoriesCannotBeOperationTargets(string name)
    {
        using var temp = new TempDirectory();
        var paths = new ImageOperationPaths(Path.Combine(temp.Path, name), true, temp.Path, temp.Path);
        Assert.Throws<IOException>(() => paths.UnpackedDirectory);
    }

    [Fact]
    public void ImageFileUnpacksBesideImagesDirectory()
    {
        using var temp = new TempDirectory();
        var input = temp.WriteBytes(Path.Combine("images", "boot.img"), [1]);
        var paths = new ImageOperationPaths(input, false, temp.Path, temp.Path);
        Assert.Equal(Path.Combine(temp.Path, "boot"), paths.UnpackedDirectory);
    }

    [Fact]
    public void NonImageFileUnpacksLocally()
    {
        using var temp = new TempDirectory();
        var unpacked = Path.Combine(temp.Path, "intermediate", "payload.bin");
        Directory.CreateDirectory(unpacked);
        var input = Path.Combine(unpacked, "boot.img");
        File.WriteAllBytes(input, [1]);
        var paths = new ImageOperationPaths(input, false, temp.Path, temp.Path);
        Assert.Equal(Path.Combine(unpacked, "boot"), paths.UnpackedDirectory);
    }

    [Fact]
    public void UnpackedDirectoryStillRejectsInvalidNames()
    {
        using var temp = new TempDirectory();
        var input = temp.WriteBytes("images", [1]);
        Assert.Throws<IOException>(() => new ImageOperationPaths(input, false, temp.Path, temp.Path).UnpackedDirectory);
    }

    [Fact]
    public void ExtractionCannotReplaceItsOwnInput()
    {
        using var temp = new TempDirectory();
        // 本地解压语义下目标为源文件同目录的同名子目录；无扩展名的源文件恰好等于目标路径时仍必须拒绝。
        // With local unpacking the target is a sibling directory named after the source; an
        // extension-less source file that equals the target path must still be rejected.
        var input = temp.WriteBytes("boot", "keep"u8.ToArray());
        Assert.Throws<IOException>(new ImageOperationPaths(input, false, temp.Path, temp.Path).ValidateExtraction);
        Assert.Equal("keep", File.ReadAllText(input));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedExtractionRestoresOriginalDirectory(bool throws)
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "boot");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "original"), "keep");
        async Task<FirmwareOpResult> Extract()
        {
            await Task.CompletedTask;
            File.WriteAllText(Path.Combine(target, "partial"), "incomplete");
            if (throws) throw new IOException("Truncated input");
            return FirmwareOpResult.Fail("Invalid input");
        }
        if (throws) await Assert.ThrowsAsync<IOException>(() => ExtractionDirectory.ReplaceAsync(target, Extract));
        else Assert.False((await ExtractionDirectory.ReplaceAsync(target, Extract)).Success);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "original")));
        Assert.False(File.Exists(Path.Combine(target, "partial")));
    }

    [Theory]
    [InlineData("dat")]
    [InlineData("br")]
    public void AndroidOutputContainsDataBlocksAndTransferList(string operation)
    {
        using var temp = new TempDirectory();
        var data = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251 + 1)).ToArray();
        var input = temp.WriteBytes("system.img", data);
        var output = Path.Combine(temp.Path, "output");
        var result = ImageOutputService.Build(new(input, false, temp.Path, output), operation, 3);
        Assert.True(result.Success, result.Summary);
        Assert.Equal(new[] { "4", "2", "0", "0", "new 2,0,2" }, File.ReadAllLines(Path.Combine(output, "system.transfer.list")));
        if (operation == "br")
        {
            using var file = File.OpenRead(Path.Combine(output, "system.new.dat.br"));
            using var brotli = new BrotliStream(file, CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            brotli.CopyTo(decoded);
            Assert.Equal(data, decoded.ToArray());
            Assert.False(File.Exists(Path.Combine(output, "system.new.dat")));
        }
        else Assert.Equal(data, File.ReadAllBytes(Path.Combine(output, "system.new.dat")));
        Assert.Empty(Directory.EnumerateDirectories(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransferPreservesContentsAndHonorsCut(bool move)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        var destination = Path.Combine(temp.Path, "destination");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "nested", "file"), "content");
        ExplorerFileOperations.Transfer(source, destination, move);
        Assert.Equal("content", File.ReadAllText(Path.Combine(destination, "source", "nested", "file")));
        Assert.Equal(!move, Directory.Exists(source));
    }

    [Fact]
    public void TransferRejectsSelfDescendantAndCollisionWithoutDataLoss()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        var child = Path.Combine(source, "child");
        Directory.CreateDirectory(child);
        var input = temp.WriteBytes("file", [1, 2, 3]);
        var existing = Path.Combine(child, "file");
        File.WriteAllBytes(existing, [4, 5]);
        Assert.Throws<IOException>(() => ExplorerFileOperations.Transfer(source, temp.Path, false));
        Assert.Throws<IOException>(() => ExplorerFileOperations.Transfer(source, child, true));
        Assert.Throws<IOException>(() => ExplorerFileOperations.Transfer(input, child, true));
        Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(existing));
        Assert.True(File.Exists(input));
        Assert.Empty(Directory.EnumerateDirectories(child));
    }
}