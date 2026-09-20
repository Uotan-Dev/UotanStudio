using System.Text.Json;
using UotanStudio.Cli;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Projects;
using Xunit;

namespace UotanStudio.Tests.Cli;

/// <summary>
/// End-to-end tests for the CLI delivery and project command groups wired on top of the public
/// facades. Validation, inspection and the archive/extract round-trip run fully offline; the
/// network-bound <c>delivery build</c>/<c>prepare-tools</c> paths are intentionally not exercised here.
/// <para>基于公共门面构建的 CLI 交付/工程命令组的端到端测试。校验、检视与归档/解包回环完全离线运行；
/// 依赖网络的 <c>delivery build</c>/<c>prepare-tools</c> 路径有意不在此执行。</para>
/// </summary>
public sealed class DeliveryProjectCliTests : IDisposable
{
    private readonly string _base;

    public DeliveryProjectCliTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "uotanstudio-cli-delproj", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    [Fact]
    public async Task DeliveryValidate_MissingPlanFile_ReturnsUsageError()
    {
        var (code, _) = await RunAsync(["delivery", "validate", Path.Combine(_base, "absent.json")]);
        Assert.Equal(3, code);
    }

    [Fact]
    public async Task DeliveryValidate_MalformedJson_ReturnsUsageError()
    {
        var path = Path.Combine(_base, "bad.json");
        await File.WriteAllTextAsync(path, "{ this is not json", TestContext.Current.CancellationToken);
        var (code, _) = await RunAsync(["delivery", "validate", path]);
        Assert.Equal(3, code);
    }

    [Fact]
    public async Task DeliveryValidate_PlanWithoutFiles_FailsValidation()
    {
        var path = WritePlan(new DeliveryPlan { OutputDirectory = Path.Combine(_base, "out") });
        var (code, output) = await RunAsync(["delivery", "validate", path, "--skip-sources"]);
        Assert.Equal(4, code);
        Assert.Contains("delivery files", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeliveryValidate_SingleImagePlan_Passes()
    {
        var path = WritePlan(new DeliveryPlan
        {
            OutputDirectory = Path.Combine(_base, "out"),
            Files = [new DeliveryFile { FileName = "boot.img", Partition = "boot" }],
        });
        var (code, output) = await RunAsync(["delivery", "validate", path, "--skip-sources"]);
        Assert.Equal(0, code);
        Assert.Contains("valid", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProjectInspect_NewSolution_PrintsSummary()
    {
        var solutionPath = await CreateSolutionAsync();
        var (code, output) = await RunAsync(["project", "inspect", solutionPath]);
        Assert.Equal(0, code);
        Assert.Contains("Solution: sample", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectInspect_MissingFile_ReturnsUsageError()
    {
        var (code, _) = await RunAsync(["project", "inspect", Path.Combine(_base, "absent.uotan")]);
        Assert.Equal(3, code);
    }

    [Fact]
    public async Task ProjectArchiveThenExtract_RoundTripsSolution()
    {
        var solutionPath = await CreateSolutionAsync();
        var package = Path.Combine(_base, "pkg", "sample.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);

        var (archiveCode, _) = await RunAsync(["project", "archive", solutionPath, package]);
        Assert.Equal(0, archiveCode);
        Assert.True(File.Exists(package));

        var destination = Path.Combine(_base, "pkg", "extracted");
        var (extractCode, extractOutput) = await RunAsync(["project", "extract", package, destination]);
        Assert.Equal(0, extractCode);
        Assert.Contains("Extracted solution", extractOutput, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(destination, "sample.uotan")));

        // The extracted solution must itself be inspectable.
        var (inspectCode, inspectOutput) = await RunAsync(["project", "inspect", Path.Combine(destination, "sample.uotan")]);
        Assert.Equal(0, inspectCode);
        Assert.Contains("Solution: sample", inspectOutput, StringComparison.Ordinal);
    }

    private async Task<string> CreateSolutionAsync()
    {
        var projectRoot = Path.Combine(_base, "sol");
        Directory.CreateDirectory(projectRoot);
        var session = new ProjectSession();
        var solutionPath = Path.Combine(projectRoot, "sample.uotan");
        await session.CreateNewSolutionAsync(solutionPath, TestContext.Current.CancellationToken);
        return solutionPath;
    }

    private string WritePlan(DeliveryPlan plan)
    {
        var path = Path.Combine(_base, "plan-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan, DeliveryJsonContext.Default.DeliveryPlan));
        return path;
    }

    private static async Task<(int Code, string Output)> RunAsync(string[] args)
    {
        using var outputWriter = new StringWriter();
        using var errorWriter = new StringWriter();
        var code = await CliProgram.RunAsync(args, outputWriter, errorWriter, TestContext.Current.CancellationToken);
        return (code, outputWriter.ToString() + errorWriter.ToString());
    }
}