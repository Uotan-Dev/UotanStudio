using System.IO.Compression;
using UotanStudio.Projects;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

public class ProjectArchiveServiceTests
{
    [Fact]
    public async Task ExportIncludesMetadataAndNeverIncludesOwnOutput()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, ".uotan", "metadata"));
        File.WriteAllText(Path.Combine(temp.Path, ".uotan", "metadata", "config.json"), "{}");
        temp.WriteBytes("project.uotan", [1, 2, 3]);
        var output = temp.WriteBytes("export.zip", [9]);
        await ProjectArchiveService.ExportAsync(temp.Path, output, TestContext.Current.CancellationToken);
        using var zip = ZipFile.OpenRead(output);
        Assert.NotNull(zip.GetEntry(".uotan/metadata/config.json"));
        Assert.NotNull(zip.GetEntry("project.uotan"));
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("export.zip", StringComparison.Ordinal));
        using var reader = new StreamReader(zip.GetEntry(".uotan/metadata/config.json")!.Open());
        Assert.Equal("{}", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledExportPreservesExistingArchiveAndCleansTemporaryOutput()
    {
        using var temp = new TempDirectory();
        var output = temp.WriteBytes("export.zip", [1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ProjectArchiveService.ExportAsync(temp.Path, output, cancellation.Token));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.export-*"));
    }
}