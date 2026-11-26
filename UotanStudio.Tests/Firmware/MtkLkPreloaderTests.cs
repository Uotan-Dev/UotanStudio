using System.Text;
using UotanStudio.Firmware.VendorFirmware.MediaTek.Lk;
using UotanStudio.Firmware.VendorFirmware.MediaTek.Preloader;
using UotanStudio.Services.Ui.Workspace.Documents;
using UotanStudio.Services.Workspace.Documents;
using UotanStudio.UiAbstractions;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class MtkLkPreloaderTests
{
    [Fact]
    public async Task LkReader_TwoEntriesWithCertificate_ReportsChainAndBindsCertificate()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "lk.img");
        try
        {
            var image = MtkTestImageBuilder.BuildLkImage(
                ("lk", Convert.FromHexString("2DE9F04FADF5AC5D0000"), 0x4D000000, 0, false),
                ("cert1", new byte[32], 0, 2, true));
            File.WriteAllBytes(path, image);

            var parsed = await LkImageReader.ReadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(2, parsed.Entries.Count);
            Assert.Equal("lk", parsed.Entries[0].Name);
            Assert.Equal(LkImageEntryKind.Application, parsed.Entries[0].Kind);
            Assert.Equal(512, parsed.Entries[0].DataOffset);
            Assert.True(parsed.Entries[0].IsExtended);
            Assert.Equal("cert1", parsed.Entries[1].Name);
            Assert.Equal(LkImageEntryKind.Certificate, parsed.Entries[1].Kind);
            Assert.True(parsed.Entries[1].IsLast);
            Assert.Equal("cert1", parsed.Entries[0].CertificateName);
            Assert.True(parsed.Entries[0].HasCertificate);
            Assert.NotNull(parsed.Find("lk"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LkReader_TruncatedPayload_Throws()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "lak.img");
        try
        {
            var image = MtkTestImageBuilder.BuildLkImage(
                ("lk", Convert.FromHexString("0011"), 0, 0, true));
            var truncated = image.AsSpan(0, 512 + 1).ToArray();
            File.WriteAllBytes(path, truncated);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                LkImageReader.ReadAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LkPatchService_FastbootNeedle_WritesNewFileAndKeepsSource()
    {
        var directory = NewDirectory();
        var source = Path.Combine(directory, "lk.img");
        var output = Path.Combine(directory, "lk.patched.img");
        try
        {
            var image = MtkTestImageBuilder.BuildLkImage(
                ("lk", Convert.FromHexString("2DE9F04FADF5AC5D0000"), 0x4D000000, 0, true));
            File.WriteAllBytes(source, image);
            var original = File.ReadAllBytes(source);
            var parsed = await LkImageReader.ReadAsync(source, TestContext.Current.CancellationToken);

            var result = await LkPatchService.PatchAsync(
                parsed, output, new LkPatchOptions { CreateBackup = false }, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(output));
            Assert.Null(result.BackupPath);
            Assert.Contains(result.Changes, change => change.Offset >= 0 && change.EntryName == "lk");
            Assert.Equal(original, File.ReadAllBytes(source));

            var patched = File.ReadAllBytes(output);
            var payload = patched.AsSpan(512, 10).ToArray();
            Assert.Equal("00207047", Convert.ToHexString(payload[..4]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LkPatchService_OutputEqualsSource_Throws()
    {
        var directory = NewDirectory();
        var source = Path.Combine(directory, "lk.img");
        try
        {
            File.WriteAllBytes(source, MtkTestImageBuilder.BuildLkImage(
                ("lk", Convert.FromHexString("2DE9F04FADF5AC5D"), 0, 0, true)));
            var parsed = await LkImageReader.ReadAsync(source, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<ArgumentException>(() => LkPatchService.PatchAsync(
                parsed, source, new LkPatchOptions(), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PreloaderReader_EmmcBootWithFlagBlock_ReportsLockAndOffset()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "boot1.bin");
        try
        {
            File.WriteAllBytes(path, MtkTestImageBuilder.BuildPreloader(Encoding.ASCII.GetBytes("ABCD"), 0x22));

            var parsed = await MtkPreloaderReader.ReadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(MtkPreloaderStorage.EmmcBoot, parsed.Storage);
            Assert.Equal(0x800, parsed.CodeOffset);
            Assert.NotNull(parsed.FlagBlock);
            Assert.True(parsed.FlagBlock!.IsLocked);
            Assert.True(parsed.CanPatch);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PreloaderReader_RawMagic_IsNotPatchable()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "raw.bin");
        try
        {
            var region = new byte[0x2000];
            "MMM"u8.CopyTo(region);
            region[3] = 0x01;
            File.WriteAllBytes(path, region);

            var parsed = await MtkPreloaderReader.ReadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(MtkPreloaderStorage.Raw, parsed.Storage);
            Assert.False(parsed.CanPatch);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PreloaderPatchService_LockedRegion_WritesUnlockedCopy()
    {
        var directory = NewDirectory();
        var source = Path.Combine(directory, "boot1.bin");
        var output = Path.Combine(directory, "boot1.unlocked.bin");
        try
        {
            var region = MtkTestImageBuilder.BuildPreloader(Encoding.ASCII.GetBytes("CODE"), 0x22, size: 0x20000);
            File.WriteAllBytes(source, region);
            var parsed = await MtkPreloaderReader.ReadAsync(source, TestContext.Current.CancellationToken);

            var result = await MtkPreloaderPatchService.PatchAsync(
                parsed, output, new MtkPreloaderPatchOptions { CreateBackup = false }, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(output));
            Assert.NotEmpty(result.Notes);
            var patched = File.ReadAllBytes(output);
            Assert.Equal(0x20, patched[0x20D]);
            Assert.Equal(0x00, patched[0x104C]);
            Assert.Equal("AND_ROMINFO_v", Encoding.ASCII.GetString(patched, 0x1000, 13));
            Assert.Equal(region, File.ReadAllBytes(source));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Routing_LkMagic_ResolvesToLkImageMode()
    {
        var header = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, 0x58881688);

        Assert.Equal(EditorOpenMode.LkImage, DocumentOpenRegistry.ResolveMode("lk.img", header, null));
        Assert.Equal(EditorOpenMode.LkImage, DocumentFormatPolicy.ResolveMode("lk.img", header));
    }

    [Fact]
    public void Routing_PreloaderMagic_ResolvesToPreloaderMode()
    {
        var header = new byte[16];
        "EMMC_BOOT"u8.CopyTo(header);

        Assert.Equal(EditorOpenMode.MtkPreloader, DocumentOpenRegistry.ResolveMode("boot1.bin", header, null));
    }

    [Fact]
    public void Routing_GenericImageWithoutMagic_DoesNotClaimLkOrPreloader()
    {
        var header = new byte[16];

        Assert.NotEqual(EditorOpenMode.LkImage, DocumentOpenRegistry.ResolveMode("random.img", header, null));
        Assert.NotEqual(EditorOpenMode.MtkPreloader, DocumentOpenRegistry.ResolveMode("random.img", header, null));
    }


    [Fact]
    public async Task LkPatchService_NeedleBeyondWindow_StillFoundOnce()
    {
        var directory = NewDirectory();
        var source = Path.Combine(directory, "big.img");
        var output = Path.Combine(directory, "big.patched.img");
        try
        {
            var payload = new byte[5L * 1024 * 1024];
            Convert.FromHexString("2DE9F04FADF5AC5D").CopyTo(payload.AsSpan(payload.Length - 4096));
            File.WriteAllBytes(source, MtkTestImageBuilder.BuildLkImage(("lk", payload, 0, 0, true)));
            var parsed = await LkImageReader.ReadAsync(source, TestContext.Current.CancellationToken);

            var result = await LkPatchService.PatchAsync(
                parsed, output, new LkPatchOptions { CreateBackup = false, Categories = ["fastboot"] },
                TestContext.Current.CancellationToken);

            var change = Assert.Single(result.Changes, c => c.Offset >= 0);
            Assert.Equal(512 + payload.Length - 4096, change.Offset);
            var patched = File.ReadAllBytes(output);
            Assert.Equal("00207047", Convert.ToHexString(patched.AsSpan((int)change.Offset, 4)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LkReader_PayloadNotAligned_AdvancesToNextAlignedHeader()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "aligned.img");
        try
        {
            File.WriteAllBytes(path, MtkTestImageBuilder.BuildLkImage(
                ("lk", new byte[100], 0x4D000000, 0, false),
                ("cert1", new byte[20], 0, 2, false),
                ("cert2", new byte[30], 0, 2, true)));

            var parsed = await LkImageReader.ReadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(3, parsed.Entries.Count);
            Assert.Equal("lk", parsed.Entries[0].Name);
            Assert.Equal("cert1", parsed.Entries[1].Name);
            Assert.Equal("cert2", parsed.Entries[2].Name);
            Assert.Equal(0, parsed.Entries[1].HeaderOffset % 16);
            Assert.Equal(0, parsed.Entries[2].HeaderOffset % 16);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-mtk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}