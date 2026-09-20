using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class NativeFileSystemInteropTests
{
    [Theory]
    [InlineData(129L * 1024 * 1024 + 7, true)]
    [InlineData(129L * 1024 * 1024 + 7, false)]
    [InlineData(513L * 1024 * 1024 + 7, true)]
    [InlineData(32746L * 4096, false)]
    public void Ext4BackupSuperblock_NativeFsckAcceptsSecondGroup(long fileBytes, bool journal)
    {
        if (!WslTestHelpers.IsInteropEnabled())
            Assert.Skip("Native/WSL interop verification is opt-in; set UOTAN_TEST_WSL_INTEROP=1 to run it.");
        if (!WslTestHelpers.IsAlpineAvailable() || !WslTestHelpers.IsToolAvailable("e2fsck"))
            Assert.Skip("Native e2fsck is unavailable.");
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        var sourceFile = Path.Combine(source, "large.bin");
        using (var file = File.Create(sourceFile))
        {
            file.SetLength(fileBytes);
            for (var i = 0; (long)i * 1024 * 1024 + 4099 <= fileBytes; i++)
            {
                file.Position = (long)i * 1024 * 1024 + 4095;
                file.Write(BitConverter.GetBytes(i));
            }
        }
        var image = Path.Combine(temp.Path, "backup.img");
        Ext4Builder.Build(source, image, new Ext4Options { IncludeJournal = journal });
        var result = WslTestHelpers.RunWsl($"e2fsck -fn -b 32768 -B 4096 {WslTestHelpers.Quote(WslTestHelpers.ToWslPath(image))}", 120000);
        Assert.True(result.ExitCode == 0, result.Output);
        using var parser = new Ext4Parser(image);
        using var hash = SHA256.Create();
        using (var sink = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
        {
            parser.ReadFile("large.bin", sink);
            sink.FlushFinalBlock();
        }
        using var expected = File.OpenRead(sourceFile);
        Assert.Equal(SHA256.HashData(expected), hash.Hash);
    }

    private static readonly string[] PackedCaseNames =
    [
        "ext4-default", "ext4-no-journal", "erofs-flat", "erofs-lz4", "erofs-lzma",
        "erofs-deflate", "erofs-zstd", "erofs-lz4-full-index", "erofs-fragments-dedup",
        "erofs-metabox", "f2fs-flat", "f2fs-lz4", "f2fs-zstd", "f2fs-lzo"
    ];

    public static TheoryData<string> PackedCases => new(PackedCaseNames);

    [Theory]
    [MemberData(nameof(PackedCases))]
    public void PackedImage_ManagedExtractionMatchesSource(string name)
    {
        using var temp = new TempDirectory();
        FirmwareMountImages.Create(temp.Path, [name]);
        VerifyExtraction(name, Path.Combine(temp.Path, "images", name + ".img"),
            Path.Combine(temp.Path, "source"), Path.Combine(temp.Path, "extracted"));
    }

    [Theory]
    [InlineData("ext2")]
    [InlineData("ext3")]
    [InlineData("ext4")]
    [InlineData("erofs-flat")]
    [InlineData("erofs-lz4")]
    [InlineData("erofs-deflate")]
    [InlineData("f2fs")]
    public void NativeImage_ManagedExtractionMatchesSource(string format)
    {
        if (!WslTestHelpers.IsInteropEnabled())
            Assert.Skip("Native/WSL interop verification is opt-in; set UOTAN_TEST_WSL_INTEROP=1 to run it.");
        if (!WslTestHelpers.IsAlpineAvailable()) Assert.Skip("The configured WSL distribution is unavailable.");
        var tools = format.StartsWith("ext", StringComparison.Ordinal) ? new[] { "mke2fs" } :
            format.StartsWith("erofs", StringComparison.Ordinal) ? ["mkfs.erofs"] : ["mkfs.f2fs", "sload.f2fs"];
        foreach (var tool in tools)
            if (!WslTestHelpers.IsToolAvailable(tool)) Assert.Skip($"Native tool {tool} is unavailable.");
        using var temp = new TempDirectory();
        FirmwareMountImages.CreateSource(temp.Path);
        var source = Path.Combine(temp.Path, "source");
        var image = Path.Combine(temp.Path, "native.img");
        var imageArg = WslTestHelpers.ToWslPath(image);
        var sourceArg = WslTestHelpers.ToWslPath(source);
        if (!format.StartsWith("erofs", StringComparison.Ordinal))
        {
            using var output = File.Create(image);
            output.SetLength(128L * 1024 * 1024);
        }
        void Run(string executable, params string[] arguments)
        {
            var resolved = WslTestHelpers.RunWsl($"command -v {WslTestHelpers.Quote(executable)}");
            Assert.Equal(0, resolved.ExitCode);
            var result = WslTestHelpers.RunWslExecutable(resolved.Output.Trim(), arguments, 120000);
            Assert.True(result.ExitCode == 0, $"{executable}: {result.Output}");
        }
        if (format.StartsWith("ext", StringComparison.Ordinal))
            Run("mke2fs", "-q", "-F", "-t", format, "-b", "4096", "-d", sourceArg, imageArg);
        else if (format.StartsWith("erofs", StringComparison.Ordinal))
        {
            var compressor = format[6..];
            if (compressor != "flat")
            {
                var help = WslTestHelpers.RunWslExecutable("mkfs.erofs", ["--help"]).Output;
                if (!help.Contains(compressor, StringComparison.Ordinal)) Assert.Skip($"mkfs.erofs lacks {compressor}.");
            }
            Run("mkfs.erofs", [.. compressor == "flat" ? Array.Empty<string>() : new[] { "-z" + compressor }, imageArg, sourceArg]);
        }
        else
        {
            Run("mkfs.f2fs", "-f", imageArg);
            Run("sload.f2fs", "-f", sourceArg, imageArg);
        }
        VerifyExtraction(format, image, source, Path.Combine(temp.Path, "extracted"), native: true);
    }

    internal static void VerifyExtraction(string format, string image, string source, string destination, bool native = false)
    {
        if (format.StartsWith("ext", StringComparison.Ordinal))
        {
            using var parser = new Ext4Parser(image);
            parser.ExtractToDirectory(destination);
        }
        else if (format.StartsWith("erofs", StringComparison.Ordinal))
        {
            using var parser = new ErofsParser(image);
            parser.ExtractToDirectory(destination);
        }
        else
        {
            using var parser = new F2fsParser(image);
            parser.ExtractToDirectory(destination);
        }
        string[] Paths(string root, bool directories) => (directories
            ? Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            : Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(Paths(source, false), Paths(destination, false));
        // Native mkfs creates an empty lost+found recovery directory.
        Assert.Equal(Paths(source, true), Paths(destination, true).Where(path => !native || path != "lost+found").ToArray());
        foreach (var path in Paths(source, false))
        {
            using var expected = File.OpenRead(Path.Combine(source, path));
            using var actual = File.OpenRead(Path.Combine(destination, path));
            Assert.Equal(expected.Length, actual.Length);
            Assert.True(SHA256.HashData(expected).SequenceEqual(SHA256.HashData(actual)), $"Content differs: {format}/{path}");
        }
    }
}