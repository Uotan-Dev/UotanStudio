using System.Text;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Conversion;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Metadata;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;
using Xunit;
using F2fsFormatHandler = UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.F2fsFormatHandler;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Verifies the structured <c>metadata.json</c> path: authoritative JSON over the legacy flat
/// sidecars, no retained original image, and lossless round-trips of service-style labels.
/// <para>验证结构化 <c>metadata.json</c> 路径：JSON 优先于旧版扁平附属文件、不保留原始镜像，
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
    public async Task Unpack_WritesMetadataJsonAndNoOriginalImage_WithFlatSidecars()
    {
        var (image, extracted) = await BuildAndUnpackAsync();
        var meta = RepackMetadata.Read(extracted)!;
        var id = meta.FileSystemPreservationId!;
        var state = Path.Combine(extracted, ".filesystem", id);

        Assert.True(File.Exists(Path.Combine(state, "metadata.json")));
        // No source image byte copy is retained; the structured document is authoritative.
        // 不保留源镜像字节副本，结构化文档是权威来源。
        Assert.Equal(["manifest.json", "metadata.json"], Directory.EnumerateFiles(state).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        // Flat sidecars coexist for backward compatibility.
        // 扁平附属文件保留以做向下兼容。
        Assert.True(File.Exists(Path.Combine(extracted, "system_fs_config")));
        Assert.True(File.Exists(Path.Combine(extracted, "system_file_contexts")));
        Assert.True(File.Exists(Path.Combine(extracted, "system_info")));
        // metadata.json captures the source rebuild parameters.
        // metadata.json 采集源重建参数。
        var document = FileSystemMetadataOverrides.TryLoadDocument(extracted, meta.SourceFileName!, id);
        Assert.NotNull(document);
        Assert.Equal("EXT4", document!.Rebuild?.Format);
        Assert.NotNull(document.Rebuild?.BlockSize);
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
    public async Task LoadTables_PrefersMetadataJson_AndFallsBackToFlatSidecars()
    {
        var (_, extracted) = await BuildAndUnpackAsync();
        var meta = RepackMetadata.Read(extracted)!;
        var id = meta.FileSystemPreservationId!;

        var json = FileSystemMetadataOverrides.LoadTables(extracted, meta.SourceFileName!, id);
        Assert.True(FileSystemMetadataOverrides.TryGetFsConfig(json.FsConfig, "/", out var jsonRoot));
        Assert.Equal(9000u, jsonRoot.Uid);
        Assert.True(FileSystemMetadataOverrides.TryGetLabel(json.FileContexts, "/data", out var jsonLabel));
        Assert.Equal("u:object_r:system_file:s0", jsonLabel);
        Assert.Equal("EXT4", json.Rebuild?.Format);

        // Deleting the JSON document reverts to parsing the flat sidecars, with identical tables.
        // 删除 JSON 文档后回退解析扁平附属文件，得到一致的表格；Rebuild 因无源镜像信息而为 null。
        File.Delete(Path.Combine(extracted, ".filesystem", id, "metadata.json"));
        var flat = FileSystemMetadataOverrides.LoadTables(extracted, meta.SourceFileName!, id);
        Assert.Null(flat.Rebuild);
        Assert.True(FileSystemMetadataOverrides.TryGetFsConfig(flat.FsConfig, "/", out var flatRoot));
        Assert.Equal(9000u, flatRoot.Uid);
        Assert.True(FileSystemMetadataOverrides.TryGetLabel(flat.FileContexts, "/data", out var flatLabel));
        Assert.Equal("u:object_r:system_file:s0", flatLabel);
    }

    [Fact]
    public async Task Rebuild_PreservesServiceStyleLabel_ViaJsonRoundTrip()
    {
        // A HAL service path containing a version token (@1.0) must survive the round trip
        // without being mangled by flat-text parsing; JSON stores it verbatim.
        // 含版本令牌（@1.0）的 HAL 服务路径必须无损往返，不被扁平文本解析破坏；JSON 原样保存。
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
    public async Task TamperedMetadataJson_FailsSidecarInvariant_AndNeverReplacesOutput()
    {
        var (_, extracted) = await BuildAndUnpackAsync();
        var meta = RepackMetadata.Read(extracted)!;
        var metadataJson = Path.Combine(extracted, ".filesystem", meta.FileSystemPreservationId!, "metadata.json");
        using (var file = new FileStream(metadataJson, FileMode.Open, FileAccess.Write))
            file.WriteByte((byte)'X'); // Replace the leading '{' so the hash diverges from the manifest sidecar.
        var output = Path.Combine(_root, "existing.img");
        File.WriteAllText(output, "keep-original-output");

        var result = await new Ext4FormatHandler().PackAsync(extracted, output, Token);
        Assert.False(result.Success);
        Assert.Equal("keep-original-output", File.ReadAllText(output));
    }

    [Fact]
    public async Task SparseRebuild_PreservesInnerFormatAndSparseBlockSize()
    {
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
        // The JSON document records the real inner filesystem and the sparse container block size.
        // JSON 文档记录真实内部文件系统与稀疏容器块大小。
        var document = FileSystemMetadataOverrides.TryLoadDocument(extracted, meta.SourceFileName!, meta.FileSystemPreservationId!);
        Assert.NotNull(document);
        Assert.Equal("Android Sparse Image (simg)", document!.Rebuild?.Format);
        Assert.Equal("EXT4", document.Rebuild?.InnerFormat);
        Assert.NotNull(document.Rebuild?.SparseBlockSize);

        File.Delete(sparse);
        File.Delete(raw);
        var output = Path.Combine(_root, "rebuilt-sparse.img");
        var packed = await new SparseFormatHandler().PackAsync(extracted, output, Token);
        Assert.True(packed.Success, packed.Summary);

        // Rebuild re-wraps the inner EXT4 into the sparse container; read the inner file back.
        // 重建将内部 EXT4 重新包裹进稀疏容器；读回内部文件验证。
        using var rebuilt = SparseReader.FromImageFile(output);
        using var logical = new SparseReadStream(rebuilt);
        using var parser = new Ext4Parser(logical);
        using var decoded = new MemoryStream();
        parser.ReadFile("data", decoded);
        Assert.Equal("json-sparse-content", Encoding.UTF8.GetString(decoded.ToArray()));
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
                UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Builder.F2fsBuilder.Build(source, image,
                    new UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.F2fsOptions { FsConfigPath = cfg, FileContextsPath = ctx });
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
            default: using (var parser = new UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser.F2fsParser(image)) parser.ReadFile(path, output); break;
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