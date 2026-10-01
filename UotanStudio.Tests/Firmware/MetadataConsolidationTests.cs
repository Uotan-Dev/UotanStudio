using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using Xunit;

namespace UotanStudio.Tests.Firmware;

[Collection("Workspace state")]
public sealed class MetadataConsolidationTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsWindows()
        ? Path.GetPathRoot(Environment.SystemDirectory)! : Path.GetTempPath(), "uotanstudio-metacons-" + Guid.NewGuid().ToString("N"));
    private readonly string _metadataRoot;
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private string Image => Path.Combine(_root, "system.img");
    private string Extracted => Path.Combine(_root, "system.img.unpacked", "system.img");

    public MetadataConsolidationTests()
    {
        _metadataRoot = Path.Combine(_root, ".uotan", "metadata");
        Directory.CreateDirectory(Path.Combine(_root, "source"));
        File.WriteAllText(Path.Combine(_root, "source", "hello.txt"), "consolidated metadata test");
        RepackMetadata.DefaultMetadataRoot = null;
    }

    public void Dispose()
    {
        RepackMetadata.DefaultMetadataRoot = null;
        Directory.Delete(_root, recursive: true);
    }

    private async Task BuildAndUnpackWithProjectAsync()
    {
        RepackMetadata.DefaultMetadataRoot = _metadataRoot;
        Ext4Builder.Build(Path.Combine(_root, "source"), Image);
        var handler = new Ext4FormatHandler();
        var unpacked = await handler.UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(unpacked.Success, unpacked.Summary);
    }

    [Fact]
    public async Task Unpack_WithDefaultRoot_WritesNothingToExtractionDirectory()
    {
        await BuildAndUnpackWithProjectAsync();
        Assert.False(File.Exists(Path.Combine(Extracted, RepackMetadata.FileName)));
        Assert.True(File.Exists(Path.Combine(_metadataRoot, "system", RepackMetadata.FileName)));
    }

    [Fact]
    public async Task Read_WithDefaultRoot_FollowsFallbackChain()
    {
        await BuildAndUnpackWithProjectAsync();
        var consolidated = Path.Combine(_metadataRoot, "system", RepackMetadata.FileName);
        Assert.Equal(consolidated, RepackMetadata.ResolveActualMetadataPath(Extracted));
        var metadata = RepackMetadata.Read(Extracted);
        Assert.NotNull(metadata);
        Assert.Equal("EXT4", metadata.FormatName);
    }

    [Fact]
    public async Task Unpack_WithDefaultRoot_GeneratesFilesystemSidecars()
    {
        await BuildAndUnpackWithProjectAsync();
        var metaDir = Path.Combine(_metadataRoot, "system");
        Assert.True(File.Exists(Path.Combine(metaDir, "system_fs_config")));
        Assert.True(File.Exists(Path.Combine(metaDir, "system_file_contexts")));
        Assert.True(File.Exists(Path.Combine(metaDir, "system_info")));
        var fsConfig = File.ReadAllText(Path.Combine(metaDir, "system_fs_config"));
        Assert.Contains("/hello.txt 0 0 420 0", fsConfig.ReplaceLineEndings());
        var contexts = File.ReadAllText(Path.Combine(metaDir, "system_file_contexts"));
        Assert.Empty(contexts);
        var info = File.ReadAllText(Path.Combine(metaDir, "system_info"));
        Assert.Contains("system.", info);
    }

    [Fact]
    public async Task OptionsFor_WithDefaultRoot_UsesSnapshotInsteadOfReferenceSidecars()
    {
        await BuildAndUnpackWithProjectAsync();
        var options = FileSystemPackingOptions.OptionsFor(Extracted, "EXT4");
        Assert.False(options.ContainsKey("FsConfigPath"));
        Assert.False(options.ContainsKey("FileContextsPath"));
        Assert.Equal("4096", options["BlockSize"]);
    }

    [Fact]
    public async Task Unpack_WithoutProject_WritesMetadataIntoExtractionDirectory()
    {
        Ext4Builder.Build(Path.Combine(_root, "source"), Image);
        var result = await new Ext4FormatHandler().UnpackAsync(Image, Extracted, cancellationToken: Cancellation);
        Assert.True(result.Success, result.Summary);
        Assert.True(File.Exists(Path.Combine(Extracted, RepackMetadata.FileName)));
        var metadata = RepackMetadata.Read(Extracted);
        Assert.NotNull(metadata);
        Assert.Equal("EXT4", metadata.FormatName);
    }

    [Fact]
    public void MetadataOverrides_ParseFsConfigModesOctalAndDecimal()
    {
        var config = Path.Combine(_root, "p_fs_config");
        File.WriteAllLines(config,
        [
            "# comment",
            "/bin/sh 0 2000 0755 0",
            "/system/xbin/su 0 0 493 0",
        ]);
        var table = FileSystemMetadataOverrides.LoadFsConfig(config);
        Assert.Equal(2, table.Count);
        Assert.True(FileSystemMetadataOverrides.TryGetFsConfig(table, "/bin/sh", out var sh));
        Assert.Equal(0u, sh.Uid);
        Assert.Equal(2000u, sh.Gid);
        Assert.Equal((ushort)493, sh.Mode);
        Assert.True(FileSystemMetadataOverrides.TryGetFsConfig(table, "system/xbin/su", out var su));
        Assert.Equal((ushort)493, su.Mode);
    }

    [Fact]
    public void MetadataOverrides_ParseFileContexts()
    {
        var contexts = Path.Combine(_root, "p_file_contexts");
        File.WriteAllLines(contexts,
        [
            "/            u:object_r:rootfs:s0",
            "/system/bin/sh    u:object_r:shell_exec:s0",
        ]);
        var table = FileSystemMetadataOverrides.LoadFileContexts(contexts);
        Assert.True(FileSystemMetadataOverrides.TryGetLabel(table, "/system/bin/sh", out var label));
        Assert.Equal("u:object_r:shell_exec:s0", label);
        Assert.True(FileSystemMetadataOverrides.TryGetLabel(table, string.Empty, out var root));
        Assert.Equal("u:object_r:rootfs:s0", root);
    }

    [Fact]
    public void MetadataOverrides_LoadPartitionInfoStripsPrefix()
    {
        var info = Path.Combine(_root, "p_info");
        File.WriteAllLines(info,
        [
            "# Partition: system",
            "system.block_size=4096",
            "system.uuid=abc",
        ]);
        var table = FileSystemMetadataOverrides.LoadPartitionInfo(info);
        Assert.Equal("4096", table["block_size"]);
        Assert.Equal("abc", table["uuid"]);
    }

    [Fact]
    public void Ext4Builder_ConsumesFsConfigAndFileContexts()
    {
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(Path.Combine(staging, "bin"));
        File.WriteAllText(Path.Combine(staging, "bin", "sh"), "#!/system/bin/sh");
        var fsConfig = Path.Combine(_root, "in_fs_config");
        File.WriteAllLines(fsConfig, ["/ 0 0 493 0", "/bin 0 2000 488 0", "/bin/sh 1000 1001 493 0"]);
        var contexts = Path.Combine(_root, "in_file_contexts");
        File.WriteAllLines(contexts, ["/bin/sh    u:object_r:shell_exec:s0"]);

        Ext4Builder.Build(staging, Image, new Ext4Options
        {
            FsConfigPath = fsConfig,
            FileContextsPath = contexts,
        });

        using var parser = new Ext4Parser(Image);
        var entries = parser.ListFiles();
        var sh = Assert.Single(entries, e => e.Path == "bin/sh");
        Assert.Equal(1000u, sh.Uid);
        Assert.Equal(1001u, sh.Gid);
        Assert.Equal(493, sh.Mode & 0x0FFF);
        Assert.Equal(0x8000, sh.Mode & 0xF000);
        Assert.NotNull(sh.Xattrs);
        Assert.True(sh.Xattrs!.TryGetValue("security.selinux", out var value));
        Assert.Equal("u:object_r:shell_exec:s0", System.Text.Encoding.UTF8.GetString(value).TrimEnd('\0'));
        var bin = Assert.Single(entries, e => e.Path == "bin");
        Assert.Equal(2000u, bin.Gid);
        Assert.Equal(488, bin.Mode & 0x0FFF);
        Assert.Equal(0x4000, bin.Mode & 0xF000);
    }

    [Fact]
    public void ErofsBuilder_ConsumesFsConfig()
    {
        var staging = Path.Combine(_root, "staging-erofs");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "run.sh"), "echo hi");
        var fsConfig = Path.Combine(_root, "in_fs_config");
        File.WriteAllLines(fsConfig, ["/run.sh 1000 1001 384 0"]);

        new ErofsBuilder().Build(staging, Image, new ErofsOptions { FsConfigPath = fsConfig });

        using var parser = new ErofsParser(Image);
        var entry = Assert.Single(parser.EnumerateFiles(), e => e.Path == "run.sh");
        Assert.Equal(1000u, entry.Uid);
        Assert.Equal(1001u, entry.Gid);
        Assert.Equal((ushort)384, entry.Mode & 0xFFF);
    }
}