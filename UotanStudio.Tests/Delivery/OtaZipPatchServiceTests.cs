using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Services.Delivery;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class OtaZipPatchServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-ota-patch-" + Guid.NewGuid().ToString("N"));

    public OtaZipPatchServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Verify_ValidStoredPayload_ReportsValidProperties()
    {
        var zip = await CreateZipAsync("original");

        var result = OtaZipPatchService.Verify(zip);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("original", result.Properties["CUSTOM"]);
    }

    [Fact]
    public async Task Patch_StoredMember_PublishesReplacementAndPreservesInput()
    {
        var zip = await CreateZipAsync("original");
        var replacement = Path.Combine(_root, "replacement.txt");
        await File.WriteAllTextAsync(replacement, "patched", TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "patched.zip");

        var result = await OtaZipPatchService.PatchAsync(zip, output,
            new OtaZipPatchOptions { ReplacementFiles = new Dictionary<string, string> { ["custom.txt"] = replacement } },
            TestContext.Current.CancellationToken);

        Assert.Equal(["custom.txt"], result.ReplacedEntries);
        Assert.Equal("original", await ReadEntryAsync(zip, "custom.txt"));
        Assert.Equal("patched", await ReadEntryAsync(output, "custom.txt"));
        Assert.True(OtaZipPatchService.Verify(output).IsValid);
    }

    [Fact]
    public async Task Patch_PayloadWithoutProperties_RejectsBeforePublishing()
    {
        var zip = await CreateZipAsync("original");
        var replacement = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(replacement, [1, 2, 3], TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "patched.zip");

        await Assert.ThrowsAsync<InvalidDataException>(() => OtaZipPatchService.PatchAsync(zip, output,
            new OtaZipPatchOptions { AllowPayloadReplacement = true, ReplacementFiles = new Dictionary<string, string> { ["payload.bin"] = replacement } },
            TestContext.Current.CancellationToken));
        Assert.False(File.Exists(output));
    }

    private async Task<string> CreateZipAsync(string custom)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        var payload = new byte[24];
        Encoding.ASCII.GetBytes("CrAU").CopyTo(payload, 0);
        var hash = Convert.ToBase64String(SHA256.HashData(payload));
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        await WriteEntryAsync(archive, "payload.bin", payload);
        await WriteEntryAsync(archive, "payload_properties.txt", $"FILE_SIZE={payload.Length}\nFILE_HASH={hash}\nCUSTOM={custom}\n");
        await WriteEntryAsync(archive, "custom.txt", custom);
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
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    public void Dispose() => Directory.Delete(_root, true);
}