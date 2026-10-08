using System.Text;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using Xunit;
using F2fsFormatHandler = UotanStudio.Firmware.FileSystem.F2fs.F2fsFormatHandler;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Verifies the structured <c>metadata.json</c> path: authoritative JSON over the flat
/// sidecars, no retained original image, and lossless round-trips of service-style labels.
/// <para>验证结构化 <c>metadata.json</c> 路径：JSON 优先于扁平附属文件、不保留原始镜像，
/// 以及服务风格标签的无损往返。</para>
/// </summary>
[Collection("Workspace state")]
public sealed class MetadataJsonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-metadata-json-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public MetadataJsonTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Unpack_WritesSingleMetadataFileAndNoPreservationDirectory()
    {
        var (image, extracted) = await BuildAndUnpackAsync();
        var meta = RepackMetadata.Read(extracted)!;

        var document = FileSystemMetadataOverrides.TryLoadDocument(meta);
        Assert.NotNull(document);
        Assert.Equal("EXT4", document!.Rebuild?.Format);
        Assert.NotNull(document.Rebuild?.BlockSize);
        Assert.Equal("system", document.Partition);
        Assert.NotEmpty(document.Projection);
        Assert.Equal(64, document.SourceHash.Length);
        Assert.Equal(64, document.ConfigurationHash.Length);
        Assert.True(File.Exists(Path.Combine(extracted, "system_fs_config")));
        Assert.True(File.Exists(Path.Combine(extracted, "system_file_contexts")));
        Assert.True(File.Exists(Path.Combine(extracted, "system_info")));
        Assert.False(Directory.Exists(Path.Combine(extracted, ".filesystem")));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(extracted).Select(Path.GetFileName),
            name => name is "manifest.json" or ".uotanstudio-repack.json");
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    [InlineData("F2FS")]
    public async Task Pack_WithMetadataJsonButNoSourceImage_RebuildsIdenticalContent(string format)
    {
        var (image, extracted) = await BuildAndUnpackAsync(format);
        File.Delete(image);
        var output = Path.Combine(_root, "rebuilt.img");
        IFormatHandler handler = format switch
        {
            "EXT4" => new Ext4FormatHandler(),
            "EROFS" => new ErofsFormatHandler(),
            _ => new F2fsFormatHandler(),
        };
        var result = await handler.PackAsync(extracted, output, Token);
        Assert.True(result.Success, result.Summary);
        Assert.Equal("json-metadata-content", ReadImageFile(format, output, "data"));
    }

    [Fact]
    public async Task LoadTables_MissingStructuredPayload_FailsInsteadOfFallingBackToFlatSidecars()
    {
        var (_, extracted) = await BuildAndUnpackAsync();
        var meta = RepackMetadata.Read(extracted)!;

        var json = FileSystemMetadataOverrides.LoadTables(meta);
        Assert.True(FileSystemMetadataOverrides.TryGetFsConfig(json.FsConfig, "/", out var jsonRoot));
        Assert.Equal(9000u, jsonRoot.Uid);
        Assert.True(FileSystemMetadataOverrides.TryGetLabel(json.FileContexts, "/data", out var jsonLabel));
        Assert.Equal("u:object_r:system_file:s0", jsonLabel);
        Assert.Equal("EXT4", json.Rebuild?.Format);

        var metadataPath = Path.Combine(extracted, RepackMetadata.FileName);
        var document = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
        document.Remove("fileSystemPreservation");
        File.WriteAllText(metadataPath, document.ToJsonString());

        Assert.True(File.Exists(Path.Combine(extracted, "system_fs_config")));
        Assert.True(File.Exists(Path.Combine(extracted, "system_file_contexts")));
        Assert.True(File.Exists(Path.Combine(extracted, "system_info")));
        var error = Assert.Throws<InvalidDataException>(
            () => FileSystemMetadataOverrides.LoadTables(RepackMetadata.Read(extracted)!));
        Assert.Contains("structured rebuild metadata is missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repack_PreservedExtraction_NeverReadsSelfGeneratedSidecars()
    {
        var (_, extracted) = await BuildAndUnpackAsync();
        var sidecar = Path.Combine(extracted, "system_fs_config");
        File.WriteAllLines(sidecar, ["/ 12345 12345 0700 0", "/data 12345 12345 0700 0"]);

        var output = Path.Combine(_root, "sidecar-guard.img");
        var result = await new Ext4FormatHandler().PackAsync(extracted, output, Token);
        Assert.True(result.Success, result.Summary);
        using (var parser = new Ext4Parser(output))
        {
            var entries = parser.ListEntriesWithRoot().ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            var data = entries["data"];
            Assert.Equal(9000u, data.Uid);
            Assert.Equal(9001u, data.Gid);
            Assert.Equal(0x1A0, data.Mode & 0x0FFF);
            var root = entries.Single(pair => pair.Key is "." or "/" or "").Value;
            Assert.Equal(9000u, root.Uid);
            Assert.Equal(0x1ED, root.Mode & 0x0FFF);
        }

        var metadataPath = Path.Combine(extracted, RepackMetadata.FileName);
        var document = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
        document.Remove("fileSystemPreservation");
        File.WriteAllText(metadataPath, document.ToJsonString());

        var options = FileSystemPackingOptions.OptionsFor(extracted, "EXT4");
        Assert.False(options.ContainsKey("FsConfigPath"));
        Assert.False(options.ContainsKey("FileContextsPath"));

        var withoutPayload = Path.Combine(_root, "sidecar-without-payload.img");
        var failure = await new Ext4FormatHandler().PackAsync(extracted, withoutPayload, Token);
        Assert.False(failure.Success);
        Assert.Contains("preservation data is missing", failure.Summary, StringComparison.Ordinal);
        Assert.False(File.Exists(withoutPayload));
    }

    [Fact]
    public async Task ErofsRepack_WithPackingOverrides_StillUsesStructuredPayload()
    {
        var (_, extracted) = await BuildAndUnpackAsync("EROFS");
        File.WriteAllLines(Path.Combine(extracted, "system_fs_config"),
            ["/ 12345 12345 0700 0", "/data 12345 12345 0700 0"]);
        File.WriteAllLines(Path.Combine(extracted, "system_file_contexts"),
            ["/data u:object_r:stale_label:s0"]);
        Assert.Equal("erofs", RepackMetadata.Read(extracted)!.PackingOptions["VolumeName"]);
        using var overrides = RepackMetadata.UsePackingOverrides(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ErofsAlgorithm"] = "lz4hc",
                ["ErofsCompressionLevel"] = "9",
                ["VolumeName"] = "erofs-ovr",
            });

        var output = Path.Combine(_root, "erofs-overrides.img");
        var result = await new ErofsFormatHandler().PackAsync(extracted, output, Token);

        Assert.True(result.Success, result.Summary);
        using var parser = new ErofsParser(output);
        Assert.Equal("erofs-ovr", parser.VolumeName);
        var entries = parser.EnumerateEntries().ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var data = entries["data"];
        Assert.Equal(9000u, data.Uid);
        Assert.Equal(9001u, data.Gid);
        Assert.Equal(0x1A0, data.Mode & 0x0FFF);
        var label = Assert.Single(data.Xattrs!, xattr => xattr.Name == "security.selinux");
        Assert.Equal("u:object_r:system_file:s0", Encoding.UTF8.GetString(label.Value).TrimEnd('\0'));
    }

    [Fact]
    public async Task F2fsRepack_FromPreservedExtraction_WritesPayloadUidGidModeAndLabel()
    {
        var (_, extracted) = await BuildAndUnpackAsync("F2FS");
        var fsConfig = Path.Combine(extracted, "system_fs_config");
        var fileContexts = Path.Combine(extracted, "system_file_contexts");
        Assert.True(File.Exists(fsConfig));
        Assert.True(File.Exists(fileContexts));
        File.WriteAllLines(fsConfig, ["/ 12345 12345 0700 0", "/data 12345 12345 0700 0"]);
        File.WriteAllLines(fileContexts, ["/data u:object_r:stale_label:s0"]);

        var output = Path.Combine(_root, "f2fs-payload-attributes.img");
        var result = await new F2fsFormatHandler().PackAsync(extracted, output, Token);

        Assert.True(result.Success, result.Summary);
        using var parser = new UotanStudio.Firmware.FileSystem.F2fs.F2fsParser(output);
        var entries = parser.ListEntriesWithRoot().ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var data = entries["data"];
        Assert.Equal(9000u, data.Uid);
        Assert.Equal(9001u, data.Gid);
        Assert.Equal(0x1A0, data.Mode & 0x0FFF);
        Assert.NotNull(data.Xattrs);
        Assert.True(data.Xattrs!.TryGetValue("security.selinux", out var label));
        Assert.Equal("u:object_r:system_file:s0", Encoding.UTF8.GetString(label).TrimEnd('\0'));
        var root = entries["."];
        Assert.Equal(9000u, root.Uid);
        Assert.Equal(9001u, root.Gid);
        Assert.Equal(0x1ED, root.Mode & 0x0FFF);
    }

    [Fact]
    public async Task F2fsRepack_MissingPayload_FailsWithoutReadingExportedSidecars()
    {
        var (_, extracted) = await BuildAndUnpackAsync("F2FS");
        File.WriteAllLines(Path.Combine(extracted, "system_fs_config"),
            ["/ 12345 12345 0700 0", "/data 12345 12345 0700 0"]);
        File.WriteAllLines(Path.Combine(extracted, "system_file_contexts"), ["/data u:object_r:stale_label:s0"]);

        var metadataPath = Path.Combine(extracted, RepackMetadata.FileName);
        var document = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
        document.Remove("fileSystemPreservation");
        File.WriteAllText(metadataPath, document.ToJsonString());

        var options = FileSystemPackingOptions.OptionsFor(extracted, "F2FS");
        Assert.False(options.ContainsKey("FsConfigPath"));
        Assert.False(options.ContainsKey("FileContextsPath"));

        var output = Path.Combine(_root, "f2fs-without-payload.img");
        var failure = await new F2fsFormatHandler().PackAsync(extracted, output, Token);
        Assert.False(failure.Success);
        Assert.Contains("preservation data is missing", failure.Summary, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Rebuild_PreservesServiceStyleLabel_ViaJsonRoundTrip()
    {
        const string label = "u:object_r:hal_broadcastradio_default:s0";
        const string relative = "bin/hw/android.hardware.broadcastradio@1.0-service";
        var cfg = Path.Combine(_root, "fs_config");
        File.WriteAllLines(cfg, [relative + " 9000 9001 0755"]);
        var ctx = Path.Combine(_root, "file_contexts");
        File.WriteAllLines(ctx, [relative + "  " + label]);
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "bin", "hw"));
        File.WriteAllText(Path.Combine(source, "bin", "hw", "android.hardware.broadcastradio@1.0-service"), "service");
        var image = Path.Combine(_root, "system.img");
        Ext4Builder.Build(source, image, new Ext4Options { FsConfigPath = cfg, FileContextsPath = ctx });
        var extracted = await UnpackAsync(new Ext4FormatHandler(), image);
        File.Delete(image);

        var output = Path.Combine(_root, "rebuilt.img");
        var result = await new Ext4FormatHandler().PackAsync(extracted, output, Token);
        Assert.True(result.Success, result.Summary);

        using var parser = new Ext4Parser(output);
        var entry = Assert.Single(parser.ListEntriesWithRoot(), e => e.Path == relative);
        Assert.Equal(9000u, entry.Uid);
        Assert.Equal(9001u, entry.Gid);
        Assert.Equal(493, entry.Mode & 0x0FFF);
        Assert.NotNull(entry.Xattrs);
        Assert.True(entry.Xattrs!.TryGetValue("security.selinux", out var value));
        Assert.Equal(label, Encoding.UTF8.GetString(value).TrimEnd('\0'));
    }

    [Fact]
    public async Task TamperedMetadataJson_FailsDocumentDigest_AndNeverReplacesOutput()
    {
        var (_, extracted) = await BuildAndUnpackAsync();
        var metadataPath = Path.Combine(extracted, RepackMetadata.FileName);
        var document = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
        var payload = document["fileSystemPreservation"]!.AsObject();
        payload["fsConfig"]!.AsObject().First().Value!.AsObject()["uid"] = 0;
        File.WriteAllText(metadataPath, document.ToJsonString());
        var output = Path.Combine(_root, "existing.img");
        File.WriteAllText(output, "keep-original-output");

        var result = await new Ext4FormatHandler().PackAsync(extracted, output, Token);
        Assert.False(result.Success);
        Assert.Equal("keep-original-output", File.ReadAllText(output));
    }

    [Fact]
    public async Task SparseRebuild_PreservesInnerFormatAndSparseBlockSize()
    {
        FirmwareFormatRegistry.RegisterAll();
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "data"), "json-sparse-content");
        var raw = Path.Combine(_root, "system.raw");
        Ext4Builder.Build(source, raw);
        var sparse = Path.Combine(_root, "system.img");
        SparseImageConverter.ConvertRawToSparse(raw, sparse);

        var extracted = Path.Combine(_root, "system");
        var unpacked = await new SparseFormatHandler().UnpackAsync(sparse, extracted, cancellationToken: Token);
        Assert.True(unpacked.Success, unpacked.Summary);
        var meta = RepackMetadata.Read(extracted)!;
        Assert.Equal("Android Sparse Image (simg)", meta.FormatName);
        var document = FileSystemMetadataOverrides.TryLoadDocument(meta);
        Assert.NotNull(document);
        Assert.Equal("Android Sparse Image (simg)", document!.Rebuild?.Format);
        Assert.Equal("EXT4", document.Rebuild?.InnerFormat);
        Assert.NotNull(document.Rebuild?.SparseBlockSize);

        File.Delete(sparse);
        File.Delete(raw);
        var output = Path.Combine(_root, "rebuilt-sparse.img");
        var packed = await new SparseFormatHandler().PackAsync(extracted, output, Token);
        Assert.True(packed.Success, packed.Summary);

        using var rebuilt = SparseReader.FromImageFile(output);
        using var logical = new SparseReadStream(rebuilt);
        using var parser = new Ext4Parser(logical);
        using var decoded = new MemoryStream();
        parser.ReadFile("data", decoded);
        Assert.Equal("json-sparse-content", Encoding.UTF8.GetString(decoded.GetBuffer(), 0, (int)decoded.Length));
    }

    private async Task<string> UnpackAsync(IFormatHandler handler, string image)
    {
        var extracted = Path.Combine(_root, "system-" + Guid.NewGuid().ToString("N"));
        var result = await handler.UnpackAsync(image, extracted, cancellationToken: Token);
        Assert.True(result.Success, result.Summary);
        return extracted;
    }

    private async Task<(string Image, string Extracted)> BuildAndUnpackAsync(string format = "EXT4")
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "data"), "json-metadata-content");
        var cfg = Path.Combine(_root, "fs_config");
        File.WriteAllLines(cfg, ["/ 9000 9001 0755", "/data 9000 9001 0640"]);
        var ctx = Path.Combine(_root, "file_contexts");
        File.WriteAllLines(ctx, ["/  u:object_r:rootfs:s0", "/data u:object_r:system_file:s0"]);
        var image = Path.Combine(_root, "system.img");
        IFormatHandler handler;
        switch (format)
        {
            case "EXT4":
                Ext4Builder.Build(source, image, new Ext4Options { FsConfigPath = cfg, FileContextsPath = ctx });
                handler = new Ext4FormatHandler();
                break;
            case "EROFS":
                new ErofsBuilder().Build(source, image, new ErofsOptions { FsConfigPath = cfg, FileContextsPath = ctx });
                handler = new ErofsFormatHandler();
                break;
            default:
                new F2fsFormatHandler();
                UotanStudio.Firmware.FileSystem.F2fs.F2fsBuilder.Build(source, image,
                    new UotanStudio.Firmware.FileSystem.F2fs.F2fsOptions { FsConfigPath = cfg, FileContextsPath = ctx });
                handler = new F2fsFormatHandler();
                break;
        }
        var extracted = await UnpackAsync(handler, image);
        return (image, extracted);
    }

    private static string ReadImageFile(string format, string image, string path)
    {
        using var output = new MemoryStream();
        switch (format)
        {
            case "EXT4": using (var parser = new Ext4Parser(image)) parser.ReadFile(path, output); break;
            case "EROFS": using (var parser = new ErofsParser(image)) parser.ReadFile(path, output); break;
            default: using (var parser = new UotanStudio.Firmware.FileSystem.F2fs.F2fsParser(image)) parser.ReadFile(path, output); break;
        }
        output.Position = 0;
        return new StreamReader(output).ReadToEnd();
    }

    public void Dispose()
    {
        _scope.Dispose();
        Directory.Delete(_root, true);
    }
}