using System.Runtime.ExceptionServices;
using UotanStudio.Firmware.Core;
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
    private static void DeleteLinkEntry(string link)
    {
        try
        {
            Directory.Delete(link);
        }
        catch (DirectoryNotFoundException)
        {
            File.Delete(link);
        }
        catch (IOException)
        {
            File.Delete(link);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}