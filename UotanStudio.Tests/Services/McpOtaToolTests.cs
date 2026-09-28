using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Mcp;
using UotanStudio.Mcp.Tools;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class McpOtaToolTests : IDisposable
{
    private readonly string _root;

    public McpOtaToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotan-mcp-ota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task VerifyAsync_StoredPayloadPackage_ReportsValidWithStructuredProperties()
    {
        var sandbox = new PathSandbox(_root);
        var zip = await CreateOtaZipAsync("custom-value", "custom.txt");

        var result = await OtaToolHandlers.VerifyAsync(sandbox, zip, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.NotNull(result.Data);
        var payload = result.Data!.Value;
        Assert.True(payload.GetProperty("isValid").GetBoolean());
        Assert.Equal("custom-value", payload.GetProperty("properties").GetProperty("CUSTOM").GetString());
    }

    [Fact]
    public async Task VerifyAsync_NonZipInput_ReportsFailureEnvelopeInsteadOfGreenText()
    {
        var sandbox = new PathSandbox(_root);
        var notZip = Path.Combine(_root, "not-a-package.zip");
        await File.WriteAllBytesAsync(notZip, [1, 2, 3, 4, 5, 6, 7, 8], TestContext.Current.CancellationToken);

        var result = await OtaToolHandlers.VerifyAsync(sandbox, notZip, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.Data);
        Assert.False(result.Data!.Value.GetProperty("isValid").GetBoolean());
        Assert.True(result.Data!.Value.GetProperty("errors").GetArrayLength() > 0);
    }

    [Fact]
    public async Task VerifyAsync_PathOutsideSandbox_IsRejectedBeforeAnyRead()
    {
        var sandbox = new PathSandbox(_root);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OtaToolHandlers.VerifyAsync(
            sandbox, @"..\outside.zip", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PatchAsync_ReplacementMap_PublishesPatchedOutputAndKeepsInput()
    {
        var sandbox = new PathSandbox(_root);
        var zip = await CreateOtaZipAsync("original", "custom.txt");
        var replacement = Path.Combine(_root, "replacement.txt");
        await File.WriteAllTextAsync(replacement, "patched", TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "patched.zip");
        var replacements = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["custom.txt"] = replacement });

        var result = await OtaToolHandlers.PatchAsync(
            sandbox, zip, output, replacements, allowPayloadReplacement: false,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("original", await ReadEntryAsync(zip, "custom.txt"));
        Assert.Equal("patched", await ReadEntryAsync(output, "custom.txt"));
        Assert.Equal(
            "custom.txt",
            result.Data!.Value.GetProperty("replacedEntries")[0].GetString());
    }

    [Fact]
    public async Task PatchAsync_PreCanceledToken_AbortsBeforePublishingOutput()
    {
        var sandbox = new PathSandbox(_root);
        var zip = await CreateOtaZipAsync("original", "custom.txt");
        var replacement = Path.Combine(_root, "replacement.txt");
        await File.WriteAllTextAsync(replacement, "patched", TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "cancelled.zip");
        var replacements = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["custom.txt"] = replacement });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OtaToolHandlers.PatchAsync(
            sandbox, zip, output, replacements, allowPayloadReplacement: false, cts.Token));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task PatchAsync_PayloadSwapWithoutConsent_ThrowsBeforePublishingOutput()
    {
        var sandbox = new PathSandbox(_root);
        var zip = await CreateOtaZipAsync("original", "custom.txt");
        var fakePayload = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(fakePayload, [1, 2, 3], TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "swapped.zip");
        var replacements = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["payload.bin"] = fakePayload });

        await Assert.ThrowsAsync<InvalidDataException>(() => OtaToolHandlers.PatchAsync(
            sandbox, zip, output, replacements, allowPayloadReplacement: false,
            TestContext.Current.CancellationToken));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ConvertSuperAsync_UnsupportedCompressionName_FailsWithoutTouchingDisk()
    {
        var sandbox = new PathSandbox(_root);
        var super = Path.Combine(_root, "super.img");
        await File.WriteAllBytesAsync(super, new byte[4096], TestContext.Current.CancellationToken);

        var result = await OtaToolHandlers.ConvertSuperAsync(
            sandbox, super, Path.Combine(_root, "payload.bin"), 4096u, "Zstd", 0, string.Empty,
            progress: null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("compression", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "payload.bin")));
    }

    [Fact]
    public async Task ConvertSuperAsync_PreCanceledToken_AbortsConversion()
    {
        var sandbox = new PathSandbox(_root);
        var super = Path.Combine(_root, "cancelled-super.img");
        await File.WriteAllBytesAsync(super, new byte[4096], TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OtaToolHandlers.ConvertSuperAsync(
            sandbox, super, Path.Combine(_root, "cancelled-payload.bin"), 4096u, "None", 0, string.Empty,
            progress: null, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task RebuildAsync_PayloadReplacement_PublishesVerifiedOutput()
    {
        var sandbox = new PathSandbox(_root);
        var payload = CreatePayload("system", "body"u8.ToArray());
        var payloadPath = Path.Combine(_root, "replacement-payload.bin");
        await File.WriteAllBytesAsync(payloadPath, payload, TestContext.Current.CancellationToken);
        var zip = await CreateRebuildZipAsync(payload);
        var output = Path.Combine(_root, "rebuilt.zip");

        var result = await OtaToolHandlers.RebuildAsync(
            sandbox, zip, output, payloadPath, null, string.Empty, string.Empty,
            verifyOutput: true, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.True(File.Exists(output));
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.Value.GetProperty("verification").GetProperty("isValid").GetBoolean(),
            result.Summary);
    }

    [Fact]
    public async Task PrepareToolsAsync_PlanWithoutTargetDirectory_ReportsFailureEnvelope()
    {
        var sandbox = new PathSandbox(_root);
        var planJson = """{"outputDirectory":"","files":[]}""";

        var result = await OtaToolHandlers.PrepareToolsAsync(
            sandbox, planJson, string.Empty, progress: null, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("target directory", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrepareToolsAsync_MalformedPlanJson_ThrowsBeforeAnyDownload()
    {
        var sandbox = new PathSandbox(_root);

        await Assert.ThrowsAsync<JsonException>(() => OtaToolHandlers.PrepareToolsAsync(
            sandbox, "{ not-json", "tools", progress: null, TestContext.Current.CancellationToken));
    }

    private static byte[] CreatePayload(string partitionName, byte[] body)
    {
        var manifest = new DeltaArchiveManifest { BlockSize = 4096 };
        manifest.Partitions.Add(new PartitionUpdate { PartitionName = partitionName });
        var manifestBytes = manifest.ToByteArray();
        var payload = new byte[24 + manifestBytes.Length + body.Length];
        "CrAU"u8.CopyTo(payload);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(12), checked((ulong)manifestBytes.Length));
        manifestBytes.CopyTo(payload, 24);
        body.CopyTo(payload, 24 + manifestBytes.Length);
        return payload;
    }

    private async Task<string> CreateRebuildZipAsync(byte[] payload)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        var metadataSize = checked(24 + (int)BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(12, 8))
            + (int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(20, 4)));
        var properties =
            $"FILE_SIZE={payload.Length}{Environment.NewLine}" +
            $"FILE_HASH={Convert.ToBase64String(SHA256.HashData(payload))}{Environment.NewLine}" +
            $"METADATA_SIZE={metadataSize}{Environment.NewLine}" +
            $"METADATA_HASH={Convert.ToBase64String(SHA256.HashData(payload.AsSpan(0, metadataSize)))}{Environment.NewLine}";

        await using var output = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        await WriteEntryAsync(archive, "payload.bin", payload);
        await WriteEntryAsync(archive, "payload_properties.txt", properties);
        await WriteEntryAsync(
            archive,
            "META-INF/com/android/metadata",
            "ota-type=AB\nota-streaming-property-files=old\nota-property-files=old\n");
        await WriteEntryAsync(archive, "custom.txt", "preserved");
        return path;
    }

    private async Task<string> CreateOtaZipAsync(string custom, string member)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        var payload = new byte[24];
        Encoding.ASCII.GetBytes("CrAU").CopyTo(payload, 0);
        var hash = Convert.ToBase64String(SHA256.HashData(payload));

        await using var output = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        await WriteEntryAsync(archive, "payload.bin", payload);
        await WriteEntryAsync(
            archive,
            "payload_properties.txt",
            $"FILE_SIZE={payload.Length}{Environment.NewLine}FILE_HASH={hash}{Environment.NewLine}CUSTOM={custom}{Environment.NewLine}");
        await WriteEntryAsync(archive, member, custom);
        return path;
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string content)
        => await WriteEntryAsync(archive, name, Encoding.UTF8.GetBytes(content));

    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] content)
    {
        await using var stream = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
        await stream.WriteAsync(content, TestContext.Current.CancellationToken);
    }

    private static async Task<string> ReadEntryAsync(string zip, string name)
    {
        using var archive = ZipFile.OpenRead(zip);
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        await using var stream = entry!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}