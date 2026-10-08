using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Mcp;
using UotanStudio.Mcp.Tools;
using UotanStudio.Services.Delivery;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Verifies the MCP path sandbox containment and flash-tool gating.
/// <para>验证 MCP 路径沙箱包含关系与刷写工具门控。</para>
/// </summary>
public class McpSandboxTests : IDisposable
{
    private readonly string _root;

    public McpSandboxTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotanstudio-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Resolve_InsideRoot_ReturnsFullPath()
    {
        var sandbox = new PathSandbox(_root);
        var resolved = sandbox.Resolve("file.bin");
        Assert.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root)), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_TraversalEscape_Throws()
    {
        var sandbox = new PathSandbox(_root);
        Assert.Throws<UnauthorizedAccessException>(() => sandbox.Resolve(Path.Combine("..", "outside.bin")));
    }

    [Fact]
    public void Resolve_AbsoluteOutside_Throws()
    {
        var sandbox = new PathSandbox(_root);
        Assert.Throws<UnauthorizedAccessException>(() => sandbox.Resolve(Path.GetTempPath()));
    }

    [Fact]
    public void Resolve_SiblingRootPrefix_Throws()
    {
        var sandbox = new PathSandbox(_root);
        var sibling = _root + "2";
        Assert.Throws<UnauthorizedAccessException>(() => sandbox.Resolve(sibling));
    }

    [Fact]
    public void Resolve_MissingRequiredFile_Throws()
    {
        var sandbox = new PathSandbox(_root);
        Assert.Throws<FileNotFoundException>(() => sandbox.Resolve("missing.bin", mustExist: true));
    }

    [Fact]
    public void BuildTools_DefaultConfig_ExcludesFlashTools()
    {
        var config = new McpServerConfig { RootDirectory = _root, AllowFlash = false };
        var tools = new McpServerHost(config).BuildTools();
        var names = tools.Select(t => t.ProtocolTool.Name).ToList();
        Assert.Contains("list_formats", names);
        Assert.Contains("compress_file", names);
        Assert.Contains("delivery_validate", names);
        Assert.DoesNotContain("fastboot_flash", names);
        Assert.DoesNotContain("fastboot_erase", names);
        Assert.DoesNotContain("adb_reboot", names);
    }

    [Fact]
    public void BuildTools_DefaultConfig_RegistersQrPairingTools()
    {
        var config = new McpServerConfig { RootDirectory = _root, AllowFlash = false };
        var names = new McpServerHost(config).BuildTools().Select(t => t.ProtocolTool.Name).ToList();

        Assert.Contains("adb_qr_pairing_code", names);
        Assert.Contains("adb_pair_qr", names);
    }

    [Fact]
    public void BuildTools_AllowFlashConfig_IncludesFlashTools()
    {
        var config = new McpServerConfig { RootDirectory = _root, AllowFlash = true };
        var tools = new McpServerHost(config).BuildTools();
        var names = tools.Select(t => t.ProtocolTool.Name).ToList();
        Assert.Contains("fastboot_flash", names);
        Assert.Contains("fastboot_erase", names);
        Assert.Contains("adb_reboot", names);
    }

    [Fact]
    public void BuildTools_RegistersMiscTools_WithUniqueSnakeCaseNames()
    {
        var config = new McpServerConfig { RootDirectory = _root, AllowFlash = false };
        var names = new McpServerHost(config).BuildTools()
            .Select(t => t.ProtocolTool.Name)
            .ToList();

        foreach (var expected in new[]
        {
            "dex_disassemble", "dex_decompile", "dex_assemble", "dex_info",
            "axml_parse", "arsc_parse", "apk_strip_signatures",
        })
        {
            Assert.Contains(expected, names);
        }

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.Matches("^[a-z0-9]+(_[a-z0-9]+)*$", name));
    }

    [Fact]
    public void ListFormats_ReportsRegisteredHandlers()
    {
        var result = FirmwareToolHandlers.ListFormats();

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.Value.GetArrayLength() > 0);
    }

    [Fact]
    public void GetEnv_ReportsVersionHandlersAndCodecs()
    {
        var sandbox = new PathSandbox(_root);
        var result = EnvToolHandlers.GetEnv(sandbox, allowFlash: false);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var data = result.Data!.Value;
        Assert.Equal("uotanstudio", data.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("version").GetString()));
        Assert.False(data.GetProperty("allowFlash").GetBoolean());
        Assert.Equal(_root, data.GetProperty("root").GetString());
        Assert.True(data.GetProperty("formatHandlerCount").GetInt32() > 0);
        Assert.NotEmpty(data.GetProperty("writableFormats").EnumerateArray().ToList());
        Assert.Contains(data.GetProperty("compressionFormats").EnumerateArray(),
            e => e.GetString() == "Zstd");
    }

    [Fact]
    public void BuildTools_RegistersGetEnvTool()
    {
        var config = new McpServerConfig { RootDirectory = _root, AllowFlash = false };
        var names = new McpServerHost(config).BuildTools().Select(t => t.ProtocolTool.Name).ToList();
        Assert.Contains("get_env", names);
    }

    [Fact]
    public void BuildTools_RegistersExtractContainerEntriesTool()
    {
        var config = new McpServerConfig { RootDirectory = _root, AllowFlash = false };
        var names = new McpServerHost(config).BuildTools().Select(t => t.ProtocolTool.Name).ToList();
        Assert.Contains("extract_container_entries", names);
    }

    [Fact]
    public async Task ExtractContainerEntries_ZipMember_ProducesStandaloneImage()
    {
        var sandbox = new PathSandbox(_root);
        var zipPath = Path.Combine(_root, "rom.zip");
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        using (var fs = File.Create(zipPath))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var entryStream = archive.CreateEntry("images/system.img").Open();
            entryStream.Write(content);
        }

        var result = await FirmwareToolHandlers.ExtractAsync(sandbox, zipPath, ["images/system.img"], "extracted",
            fromPayloadOnly: false, maximumOutputBytes: ContainerEntryExtractor.DefaultMaximumOutputBytes,
            progress: null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Summary);
        Assert.NotNull(result.Data);
        var data = result.Data!.Value;
        Assert.Equal("Zip", data.GetProperty("kind").GetString());
        Assert.Equal("images/system.img", data.GetProperty("entries")[0].GetString());
        var outputPath = data.GetProperty("outputPaths")[0].GetString();
        Assert.NotNull(outputPath);
        Assert.True(content.AsSpan().SequenceEqual(File.ReadAllBytes(outputPath!)), "extracted bytes differ");
    }

    [Fact]
    public async Task ExtractContainerEntries_RejectsPathsOutsideSandbox()
    {
        var sandbox = new PathSandbox(_root);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => FirmwareToolHandlers.ExtractAsync(sandbox, Path.Combine("..", "evil.zip"), ["x"], "out", false, 1024 * 1024,
                progress: null, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompressionRoundTrip_ProducesIdenticalBytes()
    {
        var sandbox = new PathSandbox(_root);
        var input = Path.Combine(_root, "payload.bin");
        var compressed = Path.Combine(_root, "payload.bin.zst");
        var restored = Path.Combine(_root, "payload.restored.bin");
        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(input, payload);

        var compress = await CompressionToolHandlers.Compress(sandbox, input, compressed, "zstd", null, null,
            progress: null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(compress.Success, compress.Summary);

        var decompress = await CompressionToolHandlers.Decompress(sandbox, compressed, restored, null, 1024 * 1024,
            TestContext.Current.CancellationToken);
        Assert.True(decompress.Success, decompress.Summary);

        Assert.True(payload.AsSpan().SequenceEqual(File.ReadAllBytes(restored)));
    }

    [Fact]
    public void ListAlgorithms_ReportsCompressionPayloadShape()
    {
        var result = CompressionToolHandlers.ListAlgorithms();

        Assert.True(result.Success, result.Summary);
        Assert.NotNull(result.Data);
        var algorithms = result.Data!.Value;
        Assert.True(algorithms.GetArrayLength() > 0, result.Summary);
        var first = algorithms[0];
        Assert.True(first.TryGetProperty("format", out _));
        Assert.True(first.TryGetProperty("canCompress", out _));
        Assert.True(first.TryGetProperty("isStreaming", out _));
    }

    [Fact]
    public async Task UnpackTar_ReportsFirmwareDataEntries()
    {
        var sandbox = new PathSandbox(_root);
        var staged = Path.Combine(_root, "stage");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(staged, "b.txt"), "bravo");
        var tar = Path.Combine(_root, "payload.tar");
        TarFile.CreateFromDirectory(staged, tar, includeBaseDirectory: false);

        var result = await FirmwareToolHandlers.UnpackAsync(sandbox, tar, "unpacked", 1024 * 1024,
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Summary);
        Assert.NotNull(result.Data);
        var entries = result.Data!.Value.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
    }

    [Fact]
    public void DeliveryListOutputs_ReportsPlannedAndExistingPaths()
    {
        var sandbox = new PathSandbox(_root);
        var source = Path.Combine(_root, "boot.img");
        File.WriteAllBytes(source, new byte[8192]);
        var plan = new DeliveryPlan
        {
            Files = [new DeliveryFile { SourcePath = source, FileName = "boot.img", Partition = "boot" }],
            Txt = false,
            OutputDirectory = Path.Combine(_root, "output")
        };
        var planJson = JsonSerializer.Serialize(plan, DeliveryJsonContext.Default.DeliveryPlan);

        var result = DeliveryToolHandlers.ListOutputs(sandbox, planJson);
        Assert.True(result.Success, result.Summary);
        Assert.NotNull(result.Data);
        var data = result.Data!.Value;
        Assert.True(data.GetProperty("planned").GetArrayLength() > 0, result.Summary);
        Assert.Equal(0, data.GetProperty("existing").GetArrayLength());
    }

    [Fact]
    public void DeliveryValidate_PlanWithoutOptionalSections_ReportsValidationFailureInsteadOfCrashing()
    {
        var sandbox = new PathSandbox(_root);

        var result = DeliveryToolHandlers.Validate(sandbox, """{"outputDirectory":"","files":[]}""", skipSources: false);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
        Assert.DoesNotContain("NullReference", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("ArgumentNullException", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnpackRejectsPathsOutsideSandbox()
    {
        var sandbox = new PathSandbox(_root);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => FirmwareToolHandlers.UnpackAsync(sandbox, Path.Combine("..", "evil.bin"), "out", 1024 * 1024,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Parse_AcceptsRootAndFlashFlags_IgnoresUnknown()
    {
        var config = McpServerConfig.Parse(["--mcp-headless", "--mcp-root", _root, "--allow-flash", "--startui"]);
        Assert.True(config.AllowFlash);
        Assert.Equal(Path.GetFullPath(_root), config.RootDirectory);
        Assert.False(McpLauncher.IsHeadlessRequested(["--mcp"]));
        Assert.True(McpLauncher.IsHeadlessRequested(["--mcp-headless"]));
        Assert.True(McpLauncher.IsCompanionRequested(["--mcp"]));
        Assert.False(McpLauncher.IsCompanionRequested(["--mcp-headless"]));
    }
}