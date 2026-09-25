using System.Text;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Conversion;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks image content and Linux attributes after host edits, additions and deletions.</summary>
[Collection("Workspace state")]
public sealed class FileSystemEditingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "uotan-edit-tests-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void MovingDirectoryRetainsEncodedChildNames()
    {
        var directory = Path.Combine(root, "renamed");
        Directory.CreateDirectory(directory);
        var child = Path.Combine(directory, "uotan-name-encoded");
        File.WriteAllText(child, "data");
        var manifest = new FileSystemPreservationManifest
        {
            ParsedEntries = [new() { Path = ".", Mode = 0x41ED, Uid = 123, Gid = 456 }],
            ImagePaths = new() { ["original/colon:name"] = "original/uotan-name-encoded" }
        };
        var plan = new FileSystemEditPlan(root, manifest, new() { ["renamed"] = "directory", ["renamed/uotan-name-encoded"] = "file:data" });
        var entry = plan.Get(child);
        Assert.Equal("renamed/colon:name", entry.Path);
        Assert.Equal(123u, entry.Uid);
        Assert.Equal(0x81A4, entry.Mode);
    }

    [Theory]
    [InlineData("EROFS")]
    [InlineData("F2FS")]
    public async Task NewFileUsesDirectoryDefaultAclWithoutDirectoryExecuteBits(string format)
    {
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        using var aclStream = new MemoryStream();
        using (var writer = new BinaryWriter(aclStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(format == "EROFS" ? 2u : 1u);
            foreach (var (tag, permissions) in new[] { (1, 7), (4, 5), (32, 0) })
            {
                writer.Write((ushort)tag); writer.Write((ushort)permissions);
                if (format == "EROFS") writer.Write(uint.MaxValue);
            }
        }
        var acl = aclStream.ToArray();
        var image = Path.Combine(root, "system.img");
        IFormatHandler handler;
        if (format == "EROFS")
        {
            new ErofsBuilder().Build(source, image, new ErofsOptions { FileXattrs = new Dictionary<string, IReadOnlyList<ErofsXattrEntry>> { [""] = [new() { Name = "system.posix_acl_default", Value = acl }] } });
            handler = new ErofsFormatHandler();
        }
        else
        {
            F2fsBuilder.Build(source, image, new F2fsOptions { FileXattrs = new Dictionary<string, Dictionary<string, byte[]>> { [""] = new() { ["system.posix_acl_default"] = acl } } });
            handler = new F2fsFormatHandler();
        }
        var extraction = Path.Combine(root, "extracted");
        Assert.True((await handler.UnpackAsync(image, extraction, cancellationToken: Token)).Success);
        File.WriteAllText(Path.Combine(extraction, "new"), "new");
        Directory.CreateDirectory(Path.Combine(extraction, "directory"));
        var output = Path.Combine(root, "edited.img");
        var packed = await handler.PackAsync(extraction, output, Token);
        Assert.True(packed.Success, packed.Summary);
        var entries = Read(output, format);
        Assert.Equal(0x81A0, entries["new"].Mode);
        Assert.Equal(0x41E8, entries["directory"].Mode);
        Assert.False(entries["new"].Xattrs!.ContainsKey("system.posix_acl_default"));
        Assert.True(entries["new"].Xattrs!.ContainsKey("system.posix_acl_access"));
        Assert.Equal(acl, entries["directory"].Xattrs!["system.posix_acl_default"]);
    }

    [Fact]
    public async Task F2fsExternalXattrsSurviveAndAreInheritedByNewFiles()
    {
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "original"), new byte[4 * 1024 * 1024 + 37]);
        var attributes = new Dictionary<string, byte[]>
        {
            ["security.selinux"] = Encoding.UTF8.GetBytes("u:object_r:system_file:s0\0"),
            ["user.binary"] = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray()
        };
        var image = Path.Combine(root, "system.img");
        F2fsBuilder.Build(source, image, new F2fsOptions { FileXattrs = new Dictionary<string, Dictionary<string, byte[]>> { [""] = attributes, ["original"] = attributes } });
        var handler = new F2fsFormatHandler();
        var extraction = Path.Combine(root, "extracted");
        var unpack = await handler.UnpackAsync(image, extraction, cancellationToken: Token);
        Assert.True(unpack.Success, unpack.Summary);
        File.WriteAllText(Path.Combine(extraction, "new"), "new data");
        Directory.CreateDirectory(Path.Combine(extraction, "newdir"));
        var output = Path.Combine(root, "edited.img");
        var packed = await handler.PackAsync(extraction, output, Token);
        Assert.True(packed.Success, packed.Summary);
        foreach (var entry in Read(output, "F2FS").Values)
            foreach (var pair in attributes) Assert.Equal(pair.Value, entry.Xattrs![pair.Key]);
    }

    [Theory]
    [InlineData("EXT4", false)]
    [InlineData("EXT4", true)]
    [InlineData("EROFS", false)]
    [InlineData("EROFS", true)]
    [InlineData("F2FS", false)]
    [InlineData("F2FS", true)]
    public async Task EditsInheritSourceAttributesAndReachImage(string format, bool sparse)
    {
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "bin"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        Directory.CreateDirectory(Path.Combine(source, "etc", "selinux"));
        File.WriteAllText(Path.Combine(source, "etc", "selinux", "plat_file_contexts"),
            "/system/bin/policy-added -- u:object_r:custom_exec:s0\n/system/bin/original -- u:object_r:must_not_relabel_exec:s0\n");
        File.WriteAllText(Path.Combine(source, "bin", "original"), "old");
        File.WriteAllText(Path.Combine(source, "bin", "remove"), "remove");
        var config = Path.Combine(root, "fs_config");
        File.WriteAllText(config, "/ 11 12 0755\n/bin 21 22 0750\n/bin/original 70001 80001 0751\n/bin/remove 70001 80001 0751\n/empty 31 32 0770\n");
        var contexts = Path.Combine(root, "file_contexts");
        File.WriteAllText(contexts, "/ u:object_r:rootfs:s0\n/bin u:object_r:bin_dir:s0\n/bin/original u:object_r:exec_file:s0\n/bin/remove u:object_r:exec_file:s0\n/empty u:object_r:empty_dir:s0\n");
        var image = Path.Combine(root, "system.img");
        IFormatHandler handler;
        switch (format)
        {
            case "EXT4": Ext4Builder.Build(source, image, new Ext4Options { FsConfigPath = config, FileContextsPath = contexts }); handler = new Ext4FormatHandler(); break;
            case "EROFS": new ErofsBuilder().Build(source, image, new ErofsOptions { FsConfigPath = config, FileContextsPath = contexts }); handler = new ErofsFormatHandler(); break;
            default: F2fsBuilder.Build(source, image, new F2fsOptions { FsConfigPath = config, FileContextsPath = contexts }); handler = new F2fsFormatHandler(); break;
        }
        if (sparse)
        {
            var converted = Path.Combine(root, "system.simg");
            SparseImageConverter.ConvertRawToSparse(image, converted);
            File.Delete(image);
            image = converted;
            handler = new SparseFormatHandler();
        }
        var extraction = Path.Combine(root, "extracted");
        var unpack = await handler.UnpackAsync(image, extraction, cancellationToken: Token);
        Assert.True(unpack.Success, unpack.Summary);
        File.Delete(image);
        var content = Enumerable.Range(0, 8193).Select(i => (byte)(i * 31)).ToArray();
        File.WriteAllBytes(Path.Combine(extraction, "bin", "original"), content);
        File.WriteAllBytes(Path.Combine(extraction, "bin", "added"), content);
        File.WriteAllText(Path.Combine(extraction, "bin", "policy-added"), "#!/system/bin/sh\necho policy\n");
        File.Delete(Path.Combine(extraction, "bin", "remove"));
        Directory.CreateDirectory(Path.Combine(extraction, "empty", "newdir", "nested"));
        File.WriteAllText(Path.Combine(extraction, "empty", "newdir", "nested", "newfile"), "new");
        File.WriteAllText(Path.Combine(extraction, "empty", "new-script"), "#!/system/bin/sh\necho ready\n");
        var elf = new byte[120];
        "\u007fELF"u8.CopyTo(elf);
        elf[4] = 2; elf[5] = 1; elf[16] = 3;
        elf[32] = 64; elf[54] = 56; elf[56] = 1;
        File.WriteAllBytes(Path.Combine(extraction, "empty", "new-library.so"), elf);
        elf[64] = 3;
        File.WriteAllBytes(Path.Combine(extraction, "empty", "new-pie"), elf);
        var output = Path.Combine(root, "rebuilt.img");
        var packed = await handler.PackAsync(extraction, output, Token);
        Assert.True(packed.Success, packed.Summary);
        var inspected = output;
        if (sparse)
        {
            inspected = Path.Combine(root, "rebuilt-raw.img");
            SparseImageConverter.ConvertSparseToRaw([output], inspected);
        }
        var entries = Read(inspected, format);
        Assert.Equal("u:object_r:custom_exec:s0", Label(entries["bin/policy-added"]));
        Assert.DoesNotContain("bin/remove", entries.Keys);
        Assert.DoesNotContain(entries.Keys, p => p.Contains(".filesystem", StringComparison.Ordinal));
        foreach (var path in new[] { "bin/original", "bin/added" })
        {
            var entry = entries[path];
            Assert.Equal(70001u, entry.Uid);
            Assert.Equal(80001u, entry.Gid);
            Assert.Equal(0x81E9, entry.Mode);
            Assert.Equal("u:object_r:exec_file:s0", Label(entry));
            Assert.Equal(content, ReadData(inspected, format, path));
        }
        var directory = entries["empty/newdir/nested"];
        Assert.Equal(31u, directory.Uid);
        Assert.Equal(0x41F8, directory.Mode);
        Assert.Equal("u:object_r:empty_dir:s0", Label(directory));
        var file = entries["empty/newdir/nested/newfile"];
        Assert.Equal(31u, file.Uid);
        Assert.Equal(0x81B0, file.Mode);
        Assert.Equal("u:object_r:empty_dir:s0", Label(file));
        Assert.Equal(0x81F8, entries["empty/new-script"].Mode);
        Assert.Equal(0x81F8, entries["empty/new-pie"].Mode);
        Assert.Equal(0x81B0, entries["empty/new-library.so"].Mode);
        Assert.True((await handler.PackAsync(extraction, output, Token)).Success);
        File.Delete(Path.Combine(extraction, "bin", "original"));
        File.Delete(Path.Combine(extraction, "bin", "added"));
        Directory.Delete(Path.Combine(extraction, "empty"), recursive: true);
        File.WriteAllText(Path.Combine(extraction, "bin", "replacement"), "replaced the entire directory contents");
        Assert.True((await handler.PackAsync(extraction, output, Token)).Success);
        if (sparse) SparseImageConverter.ConvertSparseToRaw([output], inspected);
        entries = Read(inspected, format);
        Assert.DoesNotContain("empty", entries.Keys);
        Assert.Equal(70001u, entries["bin/replacement"].Uid);
        Assert.Equal("u:object_r:exec_file:s0", Label(entries["bin/replacement"]));
        var successfulOutput = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(output));
        File.WriteAllText(Directory.GetFiles(Path.Combine(extraction, "etc", "selinux")).Single(), "/system/.* <<none>>\n");
        var rejected = await handler.PackAsync(extraction, output, Token);
        Assert.False(rejected.Success);
        Assert.Equal(successfulOutput, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(output)));
    }

    private static string Label(PreservedInodeEntry entry) => Encoding.UTF8.GetString(entry.Xattrs!["security.selinux"]).TrimEnd('\0');
    private static Dictionary<string, PreservedInodeEntry> Read(string path, string format)
    {
        if (format == "EXT4") { using var p = new Ext4Parser(path); return p.ListEntriesWithRoot().ToDictionary(e => e.Path, e => new PreservedInodeEntry { Mode = e.Mode, Uid = e.Uid, Gid = e.Gid, Xattrs = e.Xattrs?.ToDictionary(x => x.Key, x => x.Value) }); }
        if (format == "EROFS") { using var p = new ErofsParser(path); return p.EnumerateEntries().ToDictionary(e => e.Path, e => new PreservedInodeEntry { Mode = e.Mode, Uid = e.Uid, Gid = e.Gid, Xattrs = e.Xattrs?.ToDictionary(x => x.Name, x => x.Value) }); }
        using var f = new F2fsParser(path); return f.ListEntriesWithRoot().ToDictionary(e => e.Path, e => new PreservedInodeEntry { Mode = e.Mode, Uid = e.Uid, Gid = e.Gid, Xattrs = e.Xattrs?.ToDictionary(x => x.Key, x => x.Value) });
    }
    private static byte[] ReadData(string image, string format, string path)
    {
        using var data = new MemoryStream();
        if (format == "EXT4") { using var p = new Ext4Parser(image); p.ReadFile(path, data); }
        else if (format == "EROFS") { using var p = new ErofsParser(image); p.ReadFile(path, data); }
        else { using var p = new F2fsParser(image); p.ReadFile(path, data); }
        return data.ToArray();
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}