using System.Runtime.ExceptionServices;
using UotanStudio.Firmware.SharedKernel;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks quiet missing-path probes without weakening extraction link protection.</summary>
public sealed class ExtractionPathProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-path-probe-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FreshOutput_DoesNotThrowMissingPathExceptions()
    {
        var thread = Environment.CurrentManagedThreadId;
        var missing = 0;
        EventHandler<FirstChanceExceptionEventArgs> capture = (_, e) =>
        {
            if (Environment.CurrentManagedThreadId == thread &&
                e.Exception is FileNotFoundException or DirectoryNotFoundException) missing++;
        };
        AppDomain.CurrentDomain.FirstChanceException += capture;
        try
        {
            var path = FileExtraction.ResolvePath(_root, "new/nested/file.bin");
            FileExtraction.WriteAtomic(path, stream => stream.Write("complete"u8), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("complete", File.ReadAllText(path));
            Assert.Equal(0, missing);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= capture; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LinkWithMissingTargetOrChild_IsRejected(bool directoryLink, bool existingTarget)
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(_root, "outside");
        if (existingTarget) Directory.CreateDirectory(outside);
        var link = Path.Combine(_root, "link");
        try
        {
            if (directoryLink) Directory.CreateSymbolicLink(link, outside);
            else File.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Skip("Symbolic links are unavailable: " + ex.Message);
        }
        try
        {
            var entry = directoryLink ? "link/missing/child.bin" : "link";
            Assert.Throws<InvalidDataException>(() => FileExtraction.ResolvePath(_root, entry));
            Assert.Throws<InvalidDataException>(() => FileExtraction.WriteAtomic(Path.Combine(_root, entry), stream => stream.WriteByte(1), cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(outside, "missing", "child.bin")));
        }
        finally
        {
            if (directoryLink) Directory.Delete(link);
            else File.Delete(link);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}