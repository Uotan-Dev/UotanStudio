using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ErofsPatchReadLimitsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-erofs-budget-" + Guid.NewGuid().ToString("N"));
    public ErofsPatchReadLimitsTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(100, 2)]
    public void EnumerateFiles_ExceededExplicitBudget_Rejects(int entries, long characters)
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "first"), "first");
        File.WriteAllText(Path.Combine(source, "second"), "second");
        var image = Path.Combine(_root, "test.img");
        new ErofsBuilder().Build(source, image, new ErofsOptions { Compress = false });
        using var input = File.OpenRead(image);
        using var parser = new ErofsParser(input, entries, characters, TestContext.Current.CancellationToken);
        Assert.Throws<FileSystemCorruptedException>(parser.EnumerateFiles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadFile_CancellationDuringOutput_StopsAndRejectsCachedEnumeration(bool compressed)
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "large"), new byte[3 * 1024 * 1024]);
        var image = Path.Combine(_root, "test.img");
        new ErofsBuilder().Build(source, image, new ErofsOptions { Compress = compressed });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var input = File.OpenRead(image);
        using var parser = new ErofsParser(input, 10, 1000, cancellation.Token);
        Assert.Single(parser.EnumerateFiles());
        using var output = new CancellingOutput(cancellation);
        Assert.Throws<OperationCanceledException>(() => parser.ReadFile("large", output));
        Assert.InRange(output.Length, 1, 1024 * 1024);
        Assert.Throws<OperationCanceledException>(parser.EnumerateFiles);
    }

    private sealed class CancellingOutput(CancellationTokenSource cancellation) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            base.Write(buffer);
            cancellation.Cancel();
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}