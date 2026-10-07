using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Firmware.Patching;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ImageConfigurationEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-image-edit-test-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ImageConfigurationEditorTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("EXT4", "build.prop")]
    [InlineData("EROFS", "build.prop")]
    [InlineData("EXT4", "config_info")]
    [InlineData("EROFS", "config_info")]
    public async Task Rebuild_ExistingConfiguration_PreservesSourceAndUnrelatedContent(string format, string name)
    {
        var source = Build(format, name);
        var original = Read(source, format, name);
        var sourceHash = await HashAsync(source);
        var attributes = Attributes(source, format, name);
        var output = Path.Combine(_root, "patched.img");
        using var replacement = new MemoryStream(Encoding.UTF8.GetBytes("ro.test=patched\n"));

        await ImageConfigurationEditor.RebuildAsync(source, format, "/" + name, replacement, output,
            64 * 1024 * 1024, 1024 * 1024, Token);

        Assert.Equal("ro.test=patched\n", Read(output, format, name));
        Assert.Equal("unrelated", Read(output, format, "keep"));
        Assert.Equal(original, Read(source, format, name));
        Assert.Equal(sourceHash, await HashAsync(source));
        Assert.Equal(attributes, Attributes(output, format, name));
        Assert.True(replacement.CanRead);
    }

    [Theory]
    [InlineData("EXT4", true)]
    [InlineData("EROFS", true)]
    [InlineData("EXT4", false)]
    [InlineData("EROFS", false)]
    public async Task Rebuild_ExceededBudget_PreservesExistingOutput(string format, bool capacity)
    {
        var source = Build(format, "build.prop");
        var output = Path.Combine(_root, "patched.img");
        await File.WriteAllTextAsync(output, "existing", Token);
        using var replacement = new MemoryStream(Encoding.UTF8.GetBytes("patched"));

        if (capacity)
        {
            await Assert.ThrowsAsync<IOException>(() => ImageConfigurationEditor.RebuildAsync(source, format,
                "/build.prop", replacement, output, 1, 1024 * 1024, Token));
        }
        else
        {
            await Assert.ThrowsAsync<PatchingCorruptedException>(() => ImageConfigurationEditor.RebuildAsync(source, format,
                "/build.prop", replacement, output, 64 * 1024 * 1024, 1, Token));
        }

        Assert.Equal("existing", await File.ReadAllTextAsync(output, Token));
        Assert.Equal("ro.test=original\n", Read(source, format, "build.prop"));
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task RebuildMany_TwoConfigurations_VerifiesAndPublishesBoth(string format)
    {
        var source = Build(format, "build.prop");
        var originalHash = await HashAsync(source);
        var output = Path.Combine(_root, "batch.img");
        using var first = new MemoryStream(Encoding.UTF8.GetBytes("first"));
        using var second = new MemoryStream(Encoding.UTF8.GetBytes("second"));
        await ImageConfigurationEditor.RebuildManyAsync(source, format,
            [new("/build.prop", first), new("/keep", second)], output, 64 * 1024 * 1024, 1024 * 1024, Token);
        Assert.Equal("first", Read(output, format, "build.prop"));
        Assert.Equal("second", Read(output, format, "keep"));
        Assert.Equal(originalHash, await HashAsync(source));
        Assert.True(first.CanRead);
        Assert.True(second.CanRead);
    }

    [Theory]
    [InlineData("/build.prop")]
    [InlineData("build.prop")]
    public async Task RebuildMany_DuplicateTarget_RejectsBeforeConsumingStreams(string duplicate)
    {
        using var first = new MemoryStream([1]);
        using var second = new MemoryStream([2]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ImageConfigurationEditor.RebuildManyAsync("unused", "EXT4",
            [new("/build.prop", first), new(duplicate, second)], Path.Combine(_root, "unused"), 1024, 1024, Token));
        Assert.Equal(0, first.Position);
        Assert.Equal(0, second.Position);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task RebuildMany_LaterTargetMissing_PreservesOutputAndReplacementStreams(string format)
    {
        var source = Build(format, "build.prop");
        var output = Path.Combine(_root, "batch.img");
        await File.WriteAllTextAsync(output, "existing", Token);
        using var first = new MemoryStream([1]);
        using var second = new MemoryStream([2]);
        await Assert.ThrowsAsync<FileNotFoundException>(() => ImageConfigurationEditor.RebuildManyAsync(source, format,
            [new("/build.prop", first), new("/missing", second)], output, 64 * 1024 * 1024, 1024 * 1024, Token));
        Assert.Equal("existing", await File.ReadAllTextAsync(output, Token));
        Assert.Equal(0, first.Position);
        Assert.Equal(0, second.Position);
    }

    [Theory]
    [InlineData("EXT4", "/")]
    [InlineData("EROFS", "/")]
    [InlineData("EXT4", "/build.prop")]
    [InlineData("EROFS", "/build.prop")]
    public async Task RebuildMany_AttributesOnly_VerifiesModeAndPreservesContent(string format, string target)
    {
        var source = Build(format, "build.prop");
        var sourceHash = await HashAsync(source);
        var output = Path.Combine(_root, "attributes.img");
        ImageFileAttributes before;
        using (var image = File.OpenRead(source)) { before = ImageAttributeReader.Read(image, format, target, Token); }
        await ImageConfigurationEditor.RebuildManyAsync(source, format, [], output, 64 * 1024 * 1024, 1024 * 1024, Token,
            [new FileSystemAttributeEdit(target, 1000, 2000, 0x1E8, before.Uid, before.Gid, (ushort)(before.Mode & 0xFFF))]);
        using var result = File.OpenRead(output);
        Assert.Equal(new ImageFileAttributes(1000, 2000, (ushort)((before.Mode & 0xF000) | 0x1E8)),
            ImageAttributeReader.Read(result, format, target, Token));
        Assert.True(result.CanRead);
        Assert.Equal("ro.test=original\n", Read(output, format, "build.prop"));
        Assert.Equal(sourceHash, await HashAsync(source));
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task RebuildMany_ContentAndAttributes_VerifiesBoth(string format)
    {
        var source = Build(format, "build.prop");
        var output = Path.Combine(_root, "combined.img");
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("patched"));
        await ImageConfigurationEditor.RebuildManyAsync(source, format, [new("/build.prop", content)], output,
            64 * 1024 * 1024, 1024 * 1024, Token, [new("/build.prop", 123, 456, 0x1A0)]);
        Assert.Equal("patched", Read(output, format, "build.prop"));
        using var image = File.OpenRead(output);
        Assert.Equal(new ImageFileAttributes(123, 456, 0x81A0), ImageAttributeReader.Read(image, format, "/build.prop", Token));
    }

    [Fact]
    public async Task RebuildMany_AccessAcl_PublishesChmodMaskAndPreservesDefaultAcl()
    {
        var token = TestContext.Current.CancellationToken;
        var tree = Path.Combine(_root, "source");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(Path.Combine(tree, "build.prop"), "unchanged", token);
        var acl = Convert.FromHexString("0200000001000700FFFFFFFF02000600E803000004000700FFFFFFFF10000700FFFFFFFF20000700FFFFFFFF");
        var expected = Convert.FromHexString("0200000001000600FFFFFFFF02000600E803000004000700FFFFFFFF10000400FFFFFFFF20000000FFFFFFFF");
        var source = Path.Combine(_root, "source.img");
        new ErofsBuilder().Build(tree, source, new ErofsOptions
        {
            FsConfig = new Dictionary<string, UotanStudio.Firmware.FileSystem.Metadata.FsConfigEntry>
            {
                ["build.prop"] = new(0, 0, 0x1FF)
            },
            FileXattrs = new Dictionary<string, IReadOnlyList<UotanStudio.Firmware.FileSystem.Erofs.Models.ErofsXattrEntry>>
            {
                [""] = [new() { Name = "system.posix_acl_default", Value = acl }],
                ["build.prop"] = [new() { Name = "system.posix_acl_access", Value = acl }, new() { Name = "user.note", Value = [1, 2, 3] }]
            }
        });
        var sourceHash = await HashAsync(source);
        var output = Path.Combine(_root, "acl-edited.img");

        await ImageConfigurationEditor.RebuildManyAsync(source, "EROFS", [], output, 64 * 1024 * 1024, 1024 * 1024,
            token, [new("/build.prop", 0, 0, 0x1A0)]);

        using var parser = new ErofsParser(output);
        var entries = parser.EnumerateEntries();
        var file = entries.Single(entry => entry.Path == "build.prop");
        Assert.Equal(0x81A0, file.Mode);
        Assert.Equal(expected, file.Xattrs!.Single(attribute => attribute.Name == "system.posix_acl_access").Value);
        Assert.Equal(new byte[] { 1, 2, 3 }, file.Xattrs!.Single(attribute => attribute.Name == "user.note").Value);
        Assert.Equal(acl, entries.Single(entry => entry.Path == ".").Xattrs!.Single(attribute => attribute.Name == "system.posix_acl_default").Value);
        Assert.Equal("unchanged", Read(output, "EROFS", "build.prop"));
        Assert.Equal(sourceHash, await HashAsync(source));
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task ReadXattr_BinaryEmptyAndMissing_ReturnsDistinctValues(string format)
    {
        var tree = Path.Combine(_root, "xattr-source");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(Path.Combine(tree, "build.prop"), "original", Token);
        var output = Path.Combine(_root, "xattrs.img");
        Dictionary<string, byte[]> attributes = new() { ["user.binary"] = [0, 255, 1, 128], ["user.empty"] = [] };
        if (format == "EROFS")
        {
            new ErofsBuilder().Build(tree, output, new ErofsOptions
            {
                FileXattrs = new Dictionary<string, IReadOnlyList<UotanStudio.Firmware.FileSystem.Erofs.Models.ErofsXattrEntry>>
                {
                    ["build.prop"] = [.. attributes.Select(pair => new UotanStudio.Firmware.FileSystem.Erofs.Models.ErofsXattrEntry { Name = pair.Key, Value = pair.Value })]
                }
            });
        }
        else
        {
            Dictionary<string, string> projection = new() { ["build.prop"] = "file:original" };
            var manifest = new FileSystemPreservationData
            {
                Projection = projection,
                ParsedEntries = [new() { Path = ".", Mode = 0x41ED, Inode = 2 },
                    new() { Path = "build.prop", Mode = 0x81A4, Inode = 12, Xattrs = attributes }]
            };
            var plan = new FileSystemEditPlan(tree, manifest, projection);
            using var scope = plan.Enter();
            Ext4Builder.Build(tree, output);
        }
        using var image = File.OpenRead(output);
        var value = ImageAttributeReader.ReadXattr(image, format, "/build.prop", "user.binary", Token);
        Assert.Equal(new byte[] { 0, 255, 1, 128 }, value);
        Assert.Empty(Assert.IsType<byte[]>(ImageAttributeReader.ReadXattr(image, format, "/build.prop", "user.empty", Token)));
        Assert.Null(ImageAttributeReader.ReadXattr(image, format, "/build.prop", "user.absent", Token));
        value![0] = 42;
        Assert.Equal(0, ImageAttributeReader.ReadXattr(image, format, "/build.prop", "user.binary", Token)![0]);
        Assert.True(image.CanRead);
        Assert.Throws<InvalidDataException>(() => ImageAttributeReader.ReadXattr(image, format, "../build.prop", "user.binary", Token));
        Assert.Throws<InvalidDataException>(() => ImageAttributeReader.ReadXattr(image, format, "/build.prop", "user.bad\0", Token));
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task RebuildMany_UserXattr_VerifiesSetEmptyDeleteAndGuardFailure(string format)
    {
        var source = Build(format, "build.prop");
        var output = Path.Combine(_root, "user-xattr.img");
        await ImageConfigurationEditor.RebuildManyAsync(source, format, [], output, 64 * 1024 * 1024, 1024 * 1024,
            Token, xattrEdits: [new("/build.prop", "user.patch", [0, 255], true)]);
        using (var image = File.OpenRead(output))
        {
            Assert.Equal(new byte[] { 0, 255 }, ImageAttributeReader.ReadXattr(image, format, "/build.prop", "user.patch", Token));
        }
        var outputHash = await HashAsync(output);
        await Assert.ThrowsAsync<InvalidDataException>(() => ImageConfigurationEditor.RebuildManyAsync(output, format, [],
            output, 64 * 1024 * 1024, 1024 * 1024, Token, xattrEdits: [new("/build.prop", "user.patch", [], true, [1])]));
        Assert.Equal(outputHash, await HashAsync(output));
        var empty = Path.Combine(_root, "empty-xattr.img");
        await ImageConfigurationEditor.RebuildManyAsync(output, format, [], empty, 64 * 1024 * 1024, 1024 * 1024,
            Token, xattrEdits: [new("/build.prop", "user.patch", [], true, [0, 255])]);
        using (var image = File.OpenRead(empty))
        {
            Assert.Empty(Assert.IsType<byte[]>(ImageAttributeReader.ReadXattr(image, format, "/build.prop", "user.patch", Token)));
        }
        var deleted = Path.Combine(_root, "deleted-xattr.img");
        await ImageConfigurationEditor.RebuildManyAsync(empty, format, [], deleted, 64 * 1024 * 1024, 1024 * 1024,
            Token, xattrEdits: [new("/build.prop", "user.patch", null, true, [])]);
        using var result = File.OpenRead(deleted);
        Assert.Null(ImageAttributeReader.ReadXattr(result, format, "/build.prop", "user.patch", Token));
        Assert.Equal("ro.test=original\n", Read(deleted, format, "build.prop"));
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task RebuildMany_ContentAndUserXattr_VerifiesBothAndPreservesSource(string format)
    {
        var source = Build(format, "build.prop");
        var sourceHash = await HashAsync(source);
        var output = Path.Combine(_root, "content-xattr.img");
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("patched"));

        await ImageConfigurationEditor.RebuildManyAsync(source, format, [new("/build.prop", content)], output,
            64 * 1024 * 1024, 1024 * 1024, Token,
            xattrEdits: [new("/build.prop", "user.patch", [0, 255, 128], true)]);

        Assert.Equal("patched", Read(output, format, "build.prop"));
        using var image = File.OpenRead(output);
        Assert.Equal(new byte[] { 0, 255, 128 },
            ImageAttributeReader.ReadXattr(image, format, "/build.prop", "user.patch", Token));
        Assert.Equal(sourceHash, await HashAsync(source));
    }

    private string Build(string format, string name)
    {
        var tree = Path.Combine(_root, "source");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, name), "ro.test=original\n");
        File.WriteAllText(Path.Combine(tree, "keep"), "unrelated");
        var image = Path.Combine(_root, "system.img");
        if (format == "EXT4") { Ext4Builder.Build(tree, image); }
        else { new ErofsBuilder().Build(tree, image, new ErofsOptions { Compress = true }); }
        return image;
    }

    [Theory]
    [InlineData("EXT4", "../build.prop")]
    [InlineData("EROFS", "../build.prop")]
    [InlineData("EXT4", "/missing")]
    [InlineData("EROFS", "/missing")]
    public async Task Rebuild_InvalidTarget_DoesNotPublish(string format, string path)
    {
        var source = Build(format, "build.prop");
        var output = Path.Combine(_root, "patched.img");
        using var replacement = new MemoryStream([1, 2, 3]);

        if (path.Contains("..", StringComparison.Ordinal))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => ImageConfigurationEditor.RebuildAsync(source,
                format, path, replacement, output, 64 * 1024 * 1024, 1024 * 1024, Token));
        }
        else
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() => ImageConfigurationEditor.RebuildAsync(source,
                format, path, replacement, output, 64 * 1024 * 1024, 1024 * 1024, Token));
        }
        Assert.False(File.Exists(output));
        Assert.Equal(0, replacement.Position);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, Token));
    }

    private static string Read(string image, string format, string path)
    {
        using var input = File.OpenRead(image);
        using var output = new MemoryStream();
        ImageConfigurationReader.Extract(input, format, path, output, Token);
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private static (ushort Mode, uint Uid, uint Gid) Attributes(string image, string format, string path)
    {
        using var input = File.OpenRead(image);
        if (format == "EXT4")
        {
            using var parser = new Ext4Parser(input);
            var entry = parser.ListFiles().Single(entry => entry.Path == path);
            return (entry.Mode, entry.Uid, entry.Gid);
        }
        using var erofs = new ErofsParser(input);
        var item = erofs.EnumerateFiles().Single(entry => entry.Path == path);
        return (item.Mode, item.Uid, item.Gid);
    }

    public void Dispose() => Directory.Delete(_root, true);
}