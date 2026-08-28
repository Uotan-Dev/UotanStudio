using System.Text;
using UotanStudio.Firmware.Erofs;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class ErofsParserStreamingTests
{
    private static byte[] BuildImage(TempDirectory temp, params (string Path, byte[] Content)[] files)
    {
        foreach (var file in files)
        {
            temp.WriteBytes(file.Path, file.Content);
        }

        return new ErofsBuilder().Build(temp.Path, new ErofsOptions { Compress = false });
    }

    [Fact]
    public void StreamConstructor_NonzeroBase_RoundTrips()
    {
        using var temp = new TempDirectory();
        var payload = Encoding.UTF8.GetBytes("based payload");
        var image = BuildImage(temp, ("file.txt", payload));
        using var stream = new MemoryStream();
        stream.Write(new byte[137]);
        stream.Write(image);
        stream.Position = 137;

        using var parser = new ErofsParser(stream);

        Assert.Equal(image.Length, parser.ImageSize);
        Assert.Equal(payload, parser.ReadFileBytes("file.txt"));
    }

    [Fact]
    public void FilePathConstructor_WhenValidationFails_ReleasesFileHandle()
    {
        using var temp = new TempDirectory();
        var imagePath = Path.Combine(temp.Path, "invalid.erofs");
        File.WriteAllBytes(imagePath, new byte[4096]);

        Assert.Throws<FormatException>(() => new ErofsParser(imagePath));
        File.Delete(imagePath);

        Assert.False(File.Exists(imagePath));
    }

    [Fact]
    public async Task SharedSeekableStream_ConcurrentReadsRemainConsistent()
    {
        using var temp = new TempDirectory();
        var first = new byte[2 * 1024 * 1024];
        var second = new byte[2 * 1024 * 1024];
        new Random(17).NextBytes(first);
        new Random(29).NextBytes(second);
        var image = BuildImage(temp, ("first.bin", first), ("second.bin", second));
        using var stream = new MemoryStream(image, writable: false);
        using var parser = new ErofsParser(stream);
        _ = parser.EnumerateFiles();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await Task.Yield();
            var expected = i % 2 == 0 ? first : second;
            Assert.Equal(expected, parser.ReadFileBytes(i % 2 == 0 ? "first.bin" : "second.bin"));
        }));
    }

    [Fact]
    public void ExtractToDirectory_OverwriteFalse_PreservesExistingTarget()
    {
        using var temp = new TempDirectory();
        var image = BuildImage(temp, ("file.txt", Encoding.UTF8.GetBytes("image payload")));
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "file.txt"), "existing", new UTF8Encoding(false));

        using var parser = new ErofsParser(image);
        parser.ExtractToDirectory(output, overwrite: false);

        Assert.Equal("existing", File.ReadAllText(Path.Combine(output, "file.txt")));
    }
}