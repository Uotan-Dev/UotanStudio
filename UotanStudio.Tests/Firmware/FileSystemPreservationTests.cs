using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.Extraction;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Verifies exact recovery and fail-closed publication when the host cannot express source semantics.</summary>
[Collection("Workspace state")]
public sealed class FileSystemPreservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-preserve-test-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public FileSystemPreservationTests() => Directory.CreateDirectory(_root);

    private async Task<(IFormatHandler Handler, string Image, string Extracted)> ExtractAsync(string format)
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "data"), "original-content");
        var image = Path.Combine(_root, "system.img");
        IFormatHandler handler;
        switch (format)
        {
            case "EXT4": Ext4Builder.Build(source, image); handler = new Ext4FormatHandler(); break;
            case "EROFS": new ErofsBuilder().Build(source, image); handler = new ErofsFormatHandler(); break;
            default: F2fsBuilder.Build(source, image); handler = new F2fsFormatHandler(); break;
        }
        var extracted = Path.Combine(_root, "system");
        var result = await handler.UnpackAsync(image, extracted, cancellationToken: Token);
        Assert.True(result.Success, result.Summary);
        return (handler, image, extracted);
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    [InlineData("F2FS")]
    public async Task Rebuild_DoesNotDependOnExternalSource_AndSupportsEditedContent(string format)
    {
        var (handler, image, extracted) = await ExtractAsync(format);
        File.Delete(image);
        var output = Path.Combine(_root, "rebuilt.img");
        var restored = await handler.PackAsync(extracted, output, Token);
        Assert.True(restored.Success, restored.Summary);
        Assert.Equal("original-content", ReadImageFile(format, output, "data"));

        Assert.Throws<FileSystemUnsupportedFeatureException>(() =>
        {
            if (format == "EXT4") Ext4Builder.Build(extracted, output);
            else if (format == "EROFS") new ErofsBuilder().Build(extracted, output);
            else F2fsBuilder.Build(extracted, output);
        });
        Assert.Equal("original-content", ReadImageFile(format, output, "data"));
        File.WriteAllText(Path.Combine(extracted, "data"), "changed-content");
        var changed = await handler.PackAsync(extracted, output, Token);
        Assert.True(changed.Success, changed.Summary);
        Assert.Equal("changed-content", ReadImageFile(format, output, "data"));
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task TryRestore_PartitionCapacity_RejectsOversizeAndPreservesOutput(string format)
    {
        var (_, _, extracted) = await ExtractAsync(format);
        await File.WriteAllTextAsync(Path.Combine(extracted, "data"), "patched-content", Token);
        var reference = Path.Combine(_root, "reference.img");
        Assert.True(FileSystemPreservation.TryRestore(extracted, reference, format, Token));
        var capacity = new FileInfo(reference).Length;
        var output = Path.Combine(_root, "bounded.img");
        await File.WriteAllTextAsync(output, "keep-original-output", Token);

        Assert.Throws<IOException>(() => FileSystemPreservation.TryRestore(extracted, output, format, Token,
            maximumOutputBytes: capacity - 1));

        Assert.Equal("keep-original-output", await File.ReadAllTextAsync(output, Token));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
        Assert.True(FileSystemPreservation.TryRestore(extracted, output, format, Token, maximumOutputBytes: capacity));
        Assert.Equal(capacity, new FileInfo(output).Length);
        Assert.Equal("patched-content", ReadImageFile(format, output, "data"));
    }

    [Fact]
    public async Task TryRestore_F2fsSparseWithinPartitionCapacity_PublishesValidatedOutput()
    {
        FirmwareFormatRegistry.RegisterAll();
        var (_, _, extracted) = await ExtractAsync("F2FS");
        await File.WriteAllTextAsync(Path.Combine(extracted, "data"), "patched-content", Token);
        using var overrides = RepackMetadata.UsePackingOverrides(new Dictionary<string, string>
        {
            ["OutputSparseContainer"] = "1",
        });
        var reference = Path.Combine(_root, "reference.sparse.img");
        Assert.True(FileSystemPreservation.TryRestore(extracted, reference, "F2FS", Token));
        var capacity = new FileInfo(reference).Length;
        var output = Path.Combine(_root, "bounded.sparse.img");
        await File.WriteAllTextAsync(output, "keep-original-output", Token);

        Assert.True(FileSystemPreservation.TryRestore(
            extracted,
            output,
            "F2FS",
            Token,
            maximumOutputBytes: capacity));

        Assert.Equal(capacity, new FileInfo(output).Length);
        var decoded = Path.Combine(_root, "bounded.raw.img");
        SparseImageConverter.ConvertSparseToRaw([output], decoded);
        Assert.Equal("patched-content", ReadImageFile("F2FS", decoded, "data"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task TryRestore_ExplicitAttributes_ChangesInodeWithoutChangingContent(string format)
    {
        var (_, _, extracted) = await ExtractAsync(format);
        var output = Path.Combine(_root, "attributes.img");
        Assert.True(FileSystemPreservation.TryRestore(extracted, output, format, Token,
            attributeEdits: [new("/data", 1234, 2345, 0x1ED)]));
        Assert.Equal("original-content", ReadImageFile(format, output, "data"));
        if (format == "EXT4")
        {
            using var parser = new Ext4Parser(output);
            var entry = parser.ListFiles().Single(entry => entry.Path == "data");
            Assert.Equal(1234u, entry.Uid);
            Assert.Equal(2345u, entry.Gid);
            Assert.Equal(0x81ED, entry.Mode);
        }
        else
        {
            using var parser = new ErofsParser(output);
            var entry = parser.EnumerateFiles().Single(entry => entry.Path == "data");
            Assert.Equal(1234u, entry.Uid);
            Assert.Equal(2345u, entry.Gid);
            Assert.Equal(0x81ED, entry.Mode);
        }
        var unchanged = Path.Combine(_root, "unchanged.img");
        Assert.True(FileSystemPreservation.TryRestore(extracted, unchanged, format, Token));
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task TryRestore_AttributeGuardFails_PreservesExistingOutput(string format)
    {
        var (_, _, extracted) = await ExtractAsync(format);
        var output = Path.Combine(_root, "attributes.img");
        await File.WriteAllTextAsync(output, "existing", Token);
        Assert.Throws<InvalidDataException>(() => FileSystemPreservation.TryRestore(extracted, output, format, Token,
            attributeEdits: [new("/data", 1234, 2345, 0x1ED, ExpectedUid: uint.MaxValue)]));
        Assert.Equal("existing", await File.ReadAllTextAsync(output, Token));
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task TryRestore_UserXattr_WritesActualBytesAndHonorsAbsenceGuard(string format)
    {
        var (handler, _, extracted) = await ExtractAsync(format);
        var output = Path.Combine(_root, "xattr.img");
        Assert.True(FileSystemPreservation.TryRestore(extracted, output, format, Token,
            xattrEdits: [new("/data", "user.patch", [0, 255, 128], true)]));
        using (var image = File.OpenRead(output))
        {
            Assert.Equal(new byte[] { 0, 255, 128 }, UotanStudio.Firmware.Patching.ImageAttributeReader.ReadXattr(
                image, format, "/data", "user.patch", Token));
        }
        var secondTree = Path.Combine(_root, "second");
        Assert.True((await handler.UnpackAsync(output, secondTree, cancellationToken: Token)).Success);
        var removed = Path.Combine(_root, "removed.img");
        Assert.True(FileSystemPreservation.TryRestore(secondTree, removed, format, Token,
            xattrEdits: [new("/data", "user.patch", null, true, [0, 255, 128])]));
        using var result = File.OpenRead(removed);
        Assert.Null(UotanStudio.Firmware.Patching.ImageAttributeReader.ReadXattr(result, format, "/data", "user.patch", Token));
        Assert.Equal("original-content", ReadImageFile(format, removed, "data"));
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("manifest")]
    [InlineData("missing")]
    [InlineData("config")]
    public async Task InvalidOrChangedState_NeverReplacesExistingOutput(string mutation)
    {
        var (handler, _, extracted) = await ExtractAsync("EROFS");
        var metadata = RepackMetadata.Read(extracted)!;
        var state = Path.Combine(extracted, ".filesystem", metadata.FileSystemPreservationId!);
        switch (mutation)
        {
            case "metadata":
                using (var file = new FileStream(Path.Combine(state, "metadata.json"), FileMode.Open, FileAccess.Write)) file.WriteByte((byte)'X');
                break;
            case "manifest": File.AppendAllText(Path.Combine(state, "manifest.json"), " "); break;
            case "missing": File.Delete(Path.Combine(state, "manifest.json")); break;
            case "config":
                metadata.PackingOptions["BlockSize"] = "8192";
                await metadata.SaveAsync(extracted, Token);
                break;
        }
        var output = Path.Combine(_root, "existing.img");
        File.WriteAllText(output, "keep-original-output");
        var result = await handler.PackAsync(extracted, output, Token);
        Assert.False(result.Success);
        Assert.Equal("keep-original-output", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public void PortablePaths_DisambiguateNamesAndParentsWithoutChangingSafeNames()
    {
        var paths = new PortableExtractionPaths(_root, ["Case/a", "case/b", "ordinary", "CON", "a\\b", "trailing.", "x:y"]);
        Assert.NotEqual(Path.GetDirectoryName(paths.Resolve("Case/a"))!.ToUpperInvariant(), Path.GetDirectoryName(paths.Resolve("case/b"))!.ToUpperInvariant());
        Assert.NotEqual(Path.GetDirectoryName(paths.Resolve("Case/a")), Path.GetDirectoryName(paths.Resolve("case/b")));
        Assert.Equal(Path.Combine(_root, "ordinary"), paths.Resolve("ordinary"));
        foreach (var name in new[] { "CON", "a\\b", "trailing.", "x:y" })
            Assert.StartsWith("uotan-name-", Path.GetFileName(paths.Resolve(name)));
        Assert.Throws<InvalidDataException>(() => new PortableExtractionPaths(_root, ["../escape"]));
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public void Builder_PreservesHighIdsAndZeroPermissionsIncludingRoot(string format)
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "data"), "payload");
        var config = Path.Combine(_root, "fs_config");
        File.WriteAllText(config, "/ 70000 80000 0000\n/data 70001 80001 0000\n");
        var image = Path.Combine(_root, "test.img");
        if (format == "EXT4")
        {
            Ext4Builder.Build(source, image, new Ext4Options { FsConfigPath = config });
            using var parser = new Ext4Parser(image);
            foreach (var entry in parser.ListEntriesWithRoot())
            {
                Assert.Equal(entry.Path == "." ? 70000u : 70001u, entry.Uid);
                Assert.Equal(entry.Path == "." ? 80000u : 80001u, entry.Gid);
                Assert.Equal(0, entry.Mode & 0x0FFF);
                Assert.Equal(entry.Path == "." ? 0x4000 : 0x8000, entry.Mode & 0xF000);
            }
        }
        else
        {
            new ErofsBuilder().Build(source, image, new ErofsOptions { FsConfigPath = config });
            using var parser = new ErofsParser(image);
            foreach (var entry in parser.EnumerateEntries())
            {
                Assert.Equal(entry.Path == "." ? 70000u : 70001u, entry.Uid);
                Assert.Equal(entry.Path == "." ? 80000u : 80001u, entry.Gid);
                Assert.Equal(0, entry.Mode & 0x0FFF);
            }
        }
    }

    private static string ReadImageFile(string format, string image, string path)
    {
        using var output = new MemoryStream();
        switch (format)
        {
            case "EXT4": using (var parser = new Ext4Parser(image)) parser.ReadFile(path, output); break;
            case "EROFS": using (var parser = new ErofsParser(image)) parser.ReadFile(path, output); break;
            default: using (var parser = new F2fsParser(image)) parser.ReadFile(path, output); break;
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