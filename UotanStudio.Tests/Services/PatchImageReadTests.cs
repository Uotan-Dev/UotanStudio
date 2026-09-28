using System.Text;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Services.Patching;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PatchImageReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-patch-image-" + Guid.NewGuid().ToString("N"));
    public PatchImageReadTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task ReadImageText_FileBackedImage_PreservesTextAndTracksExternalChanges(string format)
    {
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "build.prop"), "# keep\r\nro.test=1\r\n", new UTF8Encoding(false), token);
        var image = Path.Combine(_root, "system.img");
        if (format == "EXT4") { Ext4Builder.Build(source, image); }
        else { new ErofsBuilder().Build(source, image, new ErofsOptions { Compress = true }); }
        await using var run = new PatchRun();
        var text = await run.ReadImageTextAsync(image, format, "/build.prop", token);
        Assert.Equal("# keep\r\nro.test=1\r\n", text.Text);
        Assert.Equal("utf8", text.Encoding);
        await Assert.ThrowsAsync<InvalidDataException>(() => run.ReadImageTextAsync(image, format, "../build.prop", token));
        await using (var writer = File.Open(image, FileMode.Open, FileAccess.Write))
        {
            writer.Position = writer.Length - 1;
            writer.WriteByte(0x55);
        }
        await Assert.ThrowsAsync<PatchConflictException>(() => run.ReadImageTextAsync(image, format, "/build.prop", token));
    }

    [Theory]
    [InlineData("EXT4", false)]
    [InlineData("EXT4", true)]
    [InlineData("EROFS", false)]
    [InlineData("EROFS", true)]
    public async Task ReadImageText_StagedImage_ReadsPendingContentBeforePublication(string format, bool preview)
    {
        var token = TestContext.Current.CancellationToken;
        var image = await BuildImageAsync(format);
        var pending = Path.Combine(_root, "pending.img");
        await using var run = new PatchRun(preview);
        await run.StageCopyAsync(image, pending, PatchFileMode.Add, "", token);

        var document = await run.ReadImageTextAsync(pending, format, "/build.prop", token);

        Assert.Equal("ro.test=1\n", document.Text);
        Assert.False(File.Exists(pending));
        await run.CompleteAsync(true, token);
        Assert.Equal(!preview, File.Exists(pending));
        Assert.Equal(preview ? PatchRunOutcome.Previewed : PatchRunOutcome.Applied, run.Outcome);
    }

    [Theory]
    [InlineData("EXT4", false)]
    [InlineData("EXT4", true)]
    [InlineData("EROFS", false)]
    [InlineData("EROFS", true)]
    public async Task Complete_ImageChangedAfterExtraction_RejectsPublication(string format, bool preview)
    {
        var token = TestContext.Current.CancellationToken;
        var image = await BuildImageAsync(format);
        var target = Path.Combine(_root, "output.prop");
        await File.WriteAllTextAsync(target, "original", token);
        await using var run = new PatchRun(preview);
        var document = await run.ReadImageTextAsync(image, format, "/build.prop", token);
        await run.StageTextAsync(target, document.Text, document.Encoding, PatchFileMode.Replace, token);
        var timestamp = File.GetLastWriteTimeUtc(image);
        using (var writer = File.Open(image, FileMode.Open, FileAccess.ReadWrite))
        {
            writer.Position = writer.Length - 1;
            var previous = writer.ReadByte();
            writer.Position--;
            writer.WriteByte((byte)(previous ^ 0xFF));
        }
        File.SetLastWriteTimeUtc(image, timestamp);

        await Assert.ThrowsAsync<PatchConflictException>(() => run.CompleteAsync(true, token));

        Assert.Equal(PatchRunOutcome.Conflict, run.Outcome);
        Assert.Equal("original", await File.ReadAllTextAsync(target, token));
    }

    [Theory]
    [InlineData("EXT4", false)]
    [InlineData("EROFS", false)]
    [InlineData("EXT4", true)]
    [InlineData("EROFS", true)]
    public async Task StageImageText_Rebuild_UsesOverlayAndPublishesOnlyOnCompletion(string format, bool preview)
    {
        var token = TestContext.Current.CancellationToken;
        var image = await BuildImageAsync(format);
        await using var run = new PatchRun(preview);
        var before = await run.ReadImageTextAsync(image, format, "/build.prop", token);
        Assert.True(await run.StageImageTextAsync(image, format, "/build.prop", "ro.test=2\n", "utf8",
            before.Sha256, 64 * 1024 * 1024, 1024 * 1024, token));
        Assert.Equal("ro.test=2\n", (await run.ReadImageTextAsync(image, format, "/build.prop", token)).Text);
        await using (var physical = new PatchRun())
        {
            Assert.Equal(before.Text, (await physical.ReadImageTextAsync(image, format, "/build.prop", token)).Text);
        }
        Assert.False(await run.StageImageTextAsync(image, format, "/build.prop", "ro.test=2\n", "utf8",
            "", 64 * 1024 * 1024, 1024 * 1024, token));
        await run.CompleteAsync(true, token);
        await using var published = new PatchRun();
        Assert.Equal(preview ? before.Text : "ro.test=2\n",
            (await published.ReadImageTextAsync(image, format, "/build.prop", token)).Text);
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task StageImageText_OldHashMismatch_LeavesImageUnstaged(string format)
    {
        var token = TestContext.Current.CancellationToken;
        var image = await BuildImageAsync(format);
        await using var run = new PatchRun();
        var hash = await run.HashAsync(image, token);
        await Assert.ThrowsAsync<PatchConflictException>(() => run.StageImageTextAsync(image, format,
            "/build.prop", "changed", "utf8", new string('0', 64), 64 * 1024 * 1024, 1024 * 1024, token));
        Assert.Equal(hash, await run.HashAsync(image, token));
        await run.CompleteAsync(false, token);
        Assert.Empty(run.Changes);
    }

    [Theory]
    [InlineData("EXT4", false)]
    [InlineData("EROFS", false)]
    [InlineData("EXT4", true)]
    [InlineData("EROFS", true)]
    public async Task Complete_StagedImageThenFailure_PreservesPhysicalImage(string format, bool externalChange)
    {
        var token = TestContext.Current.CancellationToken;
        var image = await BuildImageAsync(format);
        await using var run = new PatchRun();
        await run.StageImageTextAsync(image, format, "/build.prop", "patched", "utf8", "",
            64 * 1024 * 1024, 1024 * 1024, token);
        if (externalChange)
        {
            using var writer = File.Open(image, FileMode.Open, FileAccess.ReadWrite);
            writer.Position = writer.Length - 1;
            var last = writer.ReadByte();
            writer.Position--;
            writer.WriteByte((byte)(last ^ 0xFF));
        }
        string physicalHash;
        await using (var observer = new PatchRun())
        {
            physicalHash = await observer.HashAsync(image, token);
        }
        if (externalChange)
        {
            await Assert.ThrowsAsync<PatchConflictException>(() => run.CompleteAsync(true, token));
            Assert.Equal(PatchRunOutcome.Conflict, run.Outcome);
        }
        else
        {
            await run.CompleteAsync(false, token);
            Assert.Equal(PatchRunOutcome.Discarded, run.Outcome);
        }
        await using var after = new PatchRun();
        Assert.Equal(physicalHash, await after.HashAsync(image, token));
        if (!externalChange)
        {
            Assert.Equal("ro.test=1\n", (await after.ReadImageTextAsync(image, format, "/build.prop", token)).Text);
        }
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task StageImageText_SecondDifferentEdit_PreservesFirstStagedEdit(string format)
    {
        var token = TestContext.Current.CancellationToken;
        var image = await BuildImageAsync(format);
        await using var run = new PatchRun();
        await run.StageImageTextAsync(image, format, "/build.prop", "first", "utf8", "",
            64 * 1024 * 1024, 1024 * 1024, token);
        await run.StageImageTextAsync(image, format, "/other.prop", "second", "utf8", "",
            64 * 1024 * 1024, 1024 * 1024, token);
        Assert.Equal("first", (await run.ReadImageTextAsync(image, format, "/build.prop", token)).Text);
        await run.CompleteAsync(true, token);
        Assert.Single(run.Changes);
        await using var after = new PatchRun();
        Assert.Equal("first", (await after.ReadImageTextAsync(image, format, "/build.prop", token)).Text);
        Assert.Equal("second", (await after.ReadImageTextAsync(image, format, "/other.prop", token)).Text);
    }

    [Theory]
    [InlineData("EXT4", false)]
    [InlineData("EROFS", false)]
    [InlineData("EXT4", true)]
    [InlineData("EROFS", true)]
    public async Task StageImageTexts_Batch_PublishesBothOrRejectsLateGuard(string format, bool badGuard)
    {
        var token = TestContext.Current.CancellationToken;
        var image = await BuildImageAsync(format);
        await using var run = new PatchRun();
        PatchImageTextEdit[] edits = [new("/build.prop", "first", "utf8", ""),
            new("/other.prop", "second", "utf8", badGuard ? new string('0', 64) : "")];
        if (badGuard)
        {
            await Assert.ThrowsAsync<PatchConflictException>(() => run.StageImageTextsAsync(image, format, edits,
                64 * 1024 * 1024, 1024 * 1024, token));
            Assert.Equal("ro.test=1\n", (await run.ReadImageTextAsync(image, format, "/build.prop", token)).Text);
            await run.CompleteAsync(false, token);
            Assert.Empty(run.Changes);
        }
        else
        {
            Assert.True(await run.StageImageTextsAsync(image, format, edits, 64 * 1024 * 1024, 1024 * 1024, token));
            Assert.False(await run.StageImageTextsAsync(image, format, edits, 64 * 1024 * 1024, 1024 * 1024, token));
            await run.CompleteAsync(true, token);
            Assert.Single(run.Changes);
        }
        await using var after = new PatchRun();
        Assert.Equal(badGuard ? "ro.test=1\n" : "first", (await after.ReadImageTextAsync(image, format, "/build.prop", token)).Text);
        Assert.Equal(badGuard ? "original" : "second", (await after.ReadImageTextAsync(image, format, "/other.prop", token)).Text);
    }

    [Theory]
    [InlineData("EXT4", false)]
    [InlineData("EROFS", false)]
    [InlineData("EXT4", true)]
    [InlineData("EROFS", true)]
    public async Task StageImageAttributes_Change_PreviewsOrPublishesWithOverlay(string format, bool preview)
    {
        var token = TestContext.Current.CancellationToken;
        var image = await BuildImageAsync(format);
        await using var run = new PatchRun(preview);
        var before = await run.ReadImageAttributesAsync(image, format, "/build.prop", token);
        var edit = new UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemAttributeEdit(
            "/build.prop", 1234, 2345, 0x1ED);
        Assert.True(await run.StageImageAttributesAsync(image, format, edit, 64 * 1024 * 1024, 1024 * 1024, token));
        var staged = await run.ReadImageAttributesAsync(image, format, "/build.prop", token);
        Assert.Equal(1234u, staged.Uid);
        Assert.Equal(2345u, staged.Gid);
        Assert.Equal(0x81ED, staged.Mode);
        Assert.False(await run.StageImageAttributesAsync(image, format, edit, 64 * 1024 * 1024, 1024 * 1024, token));
        await using (var physical = new PatchRun())
        {
            Assert.Equal(before, await physical.ReadImageAttributesAsync(image, format, "/build.prop", token));
        }
        await run.CompleteAsync(true, token);
        await using var after = new PatchRun();
        Assert.Equal(preview ? before : staged, await after.ReadImageAttributesAsync(image, format, "/build.prop", token));
        Assert.Equal("ro.test=1\n", (await after.ReadImageTextAsync(image, format, "/build.prop", token)).Text);
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task StageImageAttributes_GuardMismatch_LeavesImageUnstaged(string format)
    {
        var token = TestContext.Current.CancellationToken;
        var image = await BuildImageAsync(format);
        await using var run = new PatchRun();
        var before = await run.ReadImageAttributesAsync(image, format, "/build.prop", token);
        var edit = new UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemAttributeEdit(
            "/build.prop", 1234, 2345, 0x1ED, ExpectedUid: before.Uid ^ 1);
        await Assert.ThrowsAsync<PatchConflictException>(() => run.StageImageAttributesAsync(image, format, edit,
            64 * 1024 * 1024, 1024 * 1024, token));
        await run.CompleteAsync(false, token);
        Assert.Empty(run.Changes);
    }

    [Theory]
    [InlineData("EXT4")]
    [InlineData("EROFS")]
    public async Task ReadImageXattr_StagedImageAndChangedSource_RejectsPublication(string format)
    {
        var token = TestContext.Current.CancellationToken;
        var image = await BuildImageAsync(format);
        var staged = Path.Combine(_root, "staged.img");
        await using var run = new PatchRun();
        await run.StageCopyAsync(image, staged, PatchFileMode.Add, "", token);
        Assert.Null(await run.ReadImageXattrAsync(staged, format, "/build.prop", "user.absent", token));
        using (var writer = File.Open(image, FileMode.Open, FileAccess.ReadWrite))
        {
            writer.Position = writer.Length - 1;
            var last = writer.ReadByte();
            writer.Position--;
            writer.WriteByte((byte)(last ^ 255));
        }
        await Assert.ThrowsAsync<PatchConflictException>(() => run.CompleteAsync(true, token));
        Assert.False(File.Exists(staged));
    }

    [Theory]
    [InlineData("EXT4", false)]
    [InlineData("EROFS", false)]
    [InlineData("EXT4", true)]
    [InlineData("EROFS", true)]
    public async Task StageImageXattr_EmptyValue_PreviewsOrPublishesDistinctFromAbsence(string format, bool preview)
    {
        var token = TestContext.Current.CancellationToken;
        var image = await BuildImageAsync(format);
        await using var run = new PatchRun(preview);
        var edit = new UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemXattrEdit("/build.prop", "user.patch", []);
        Assert.True(await run.StageImageXattrAsync(image, format, edit, 64 * 1024 * 1024, 1024 * 1024, token));
        Assert.Empty(Assert.IsType<byte[]>(await run.ReadImageXattrAsync(image, format, "/build.prop", "user.patch", token)));
        Assert.False(await run.StageImageXattrAsync(image, format, edit, 64 * 1024 * 1024, 1024 * 1024, token));
        await using (var physical = new PatchRun())
        {
            Assert.Null(await physical.ReadImageXattrAsync(image, format, "/build.prop", "user.patch", token));
        }
        await run.CompleteAsync(true, token);
        await using var after = new PatchRun();
        var value = await after.ReadImageXattrAsync(image, format, "/build.prop", "user.patch", token);
        if (preview) { Assert.Null(value); }
        else { Assert.Empty(Assert.IsType<byte[]>(value)); }
    }

    private async Task<string> BuildImageAsync(string format)
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "build.prop"), "ro.test=1\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, "other.prop"), "original", TestContext.Current.CancellationToken);
        var image = Path.Combine(_root, "system.img");
        if (format == "EXT4")
        {
            Ext4Builder.Build(source, image);
        }
        else
        {
            new ErofsBuilder().Build(source, image, new ErofsOptions { Compress = true });
        }
        return image;
    }

    public void Dispose() => Directory.Delete(_root, true);
}