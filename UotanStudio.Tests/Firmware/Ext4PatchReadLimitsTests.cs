using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class Ext4PatchReadLimitsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-ext4-budget-" + Guid.NewGuid().ToString("N"));

    public Ext4PatchReadLimitsTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(100, 2)]
    public void ListFiles_ExceededExplicitBudget_Rejects(int entries, long characters)
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "first"), "first");
        File.WriteAllText(Path.Combine(source, "second"), "second");
        var image = Path.Combine(_root, "test.img");
        Ext4Builder.Build(source, image);
        using var parser = new Ext4Parser(File.OpenRead(image), entries, characters, TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => parser.ListFiles());
    }

    [Fact]
    public void ReadFile_CancellationDuringCopy_StopsBeforeCompleteOutput()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "large"), new byte[3 * 1024 * 1024]);
        var image = Path.Combine(_root, "test.img");
        Ext4Builder.Build(source, image);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var parser = new Ext4Parser(File.OpenRead(image), 10, 1000, cancellation.Token);
        using var output = new CancellingOutput(cancellation);
        Assert.Throws<OperationCanceledException>(() => parser.ReadFile("large", output));
        Assert.InRange(output.Length, 1, 1024 * 1024);
    }

    [Fact]
    public void ListFiles_CancelAfterCachedEnumeration_StillThrows()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file"), "content");
        var image = Path.Combine(_root, "test.img");
        Ext4Builder.Build(source, image);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var parser = new Ext4Parser(File.OpenRead(image), 10, 1000, cancellation.Token);
        Assert.Single(parser.ListFiles());
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => parser.ListFiles());
    }

    private sealed class CancellingOutput(CancellationTokenSource cancellation) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            cancellation.Cancel();
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}