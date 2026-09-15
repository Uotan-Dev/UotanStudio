using System.IO.Compression;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class BootPatchModelsTests
{
    [Fact]
    public void APatchSuperKey_UsesOfficialLengthAndCompositionRules()
    {
        Assert.False(APatchSuperKey.IsValid("short1"));
        Assert.False(APatchSuperKey.IsValid("onlyletters"));
        Assert.True(APatchSuperKey.IsValid("password1"));
    }

    [Fact]
    public async Task Analyzer_RecognizesOfficialApatchLayoutAndAbis()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("assets/boot_patch.sh");
            zip.CreateEntry("assets/util_functions.sh");
            zip.CreateEntry("lib/arm64-v8a/libkptools.so");
            zip.CreateEntry("lib/x86_64/libkptools.so");
        }
        stream.Position = 0;
        var result = await BootPatchAnalyzer.AnalyzeAsync("apatch.apk", stream, TestContext.Current.CancellationToken);
        Assert.Equal(BootPatchKind.APatch, result.Kind);
        Assert.Equal(["arm64", "x64"], result.Architectures);
    }

    [Fact]
    public async Task Analyzer_RecognizesMagiskNativeLibrariesAndVersion()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var util = new StreamWriter(zip.CreateEntry("assets/util_functions.sh").Open()))
                util.WriteLine("MAGISK_VER='27.0:27000'");
            using (var script = new StreamWriter(zip.CreateEntry("assets/boot_patch.sh").Open()))
                script.WriteLine("KEEPVERITY=false\nKEEPFORCEENCRYPT=false\nPATCHVBMETAFLAG=false\nRECOVERYMODE=false\nLEGACYSAR=false\nPREINITDEVICE=");
            zip.CreateEntry("lib/arm64-v8a/libmagiskinit.so");
            zip.CreateEntry("lib/arm64-v8a/libmagisk.so");
            zip.CreateEntry("lib/x86_64/libmagiskinit.so");
        }
        stream.Position = 0;
        var result = await BootPatchAnalyzer.AnalyzeAsync("Magisk-v27.apk", stream, TestContext.Current.CancellationToken);
        Assert.Equal(BootPatchKind.Magisk, result.Kind);
        Assert.Equal(["arm64", "x64"], result.Architectures);
        Assert.Equal("27.0:27000", result.Version);
        Assert.Contains(BootPatchOption.PatchVbmetaFlag, result.SupportedOptions!);
        Assert.Contains(BootPatchOption.LegacySar, result.SupportedOptions!);
    }

    [Fact]
    public async Task Analyzer_RecognizesKernelSuVariantLibraries()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("lib/arm64-v8a/libsu.so");
            zip.CreateEntry("lib/arm64-v8a/ksud");
            zip.CreateEntry("lib/x86_64/susfs.ko");
        }
        stream.Position = 0;
        var result = await BootPatchAnalyzer.AnalyzeAsync("kernelsu-next.apk", stream, TestContext.Current.CancellationToken);
        Assert.Equal(BootPatchKind.KernelSu, result.Kind);
        Assert.Equal(["arm64", "x64"], result.Architectures);
    }

    [Fact]
    public async Task Analyzer_IgnoresPackagesWithOnlySharedOrGenericMarkers()
    {
        // An ordinary APK bundling the generic libsu.so and a shared
        // util_functions.sh must not be classified as any root patcher.
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("assets/util_functions.sh");
            zip.CreateEntry("lib/arm64-v8a/libsu.so");
            zip.CreateEntry("lib/arm64-v8a/libfoo.so");
        }
        stream.Position = 0;
        var result = await BootPatchAnalyzer.AnalyzeAsync("ordinary.apk", stream, TestContext.Current.CancellationToken);
        Assert.Equal(BootPatchKind.Unknown, result.Kind);
    }

    [Fact]
    public async Task Analyzer_RequiresMagiskNativeComponentsOverSharedScript()
    {
        // Magisk boot scripts and magiskboot are shared with root tooling, so
        // their presence alone no longer classifies a package as Magisk.
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("assets/util_functions.sh");
            zip.CreateEntry("assets/boot_patch.sh");
            zip.CreateEntry("lib/arm64-v8a/magiskboot");
        }
        stream.Position = 0;
        var result = await BootPatchAnalyzer.AnalyzeAsync("should-be-unknown.zip", stream, TestContext.Current.CancellationToken);
        Assert.Equal(BootPatchKind.Unknown, result.Kind);
    }

    [Fact]
    public async Task Analyzer_DoesNotClassifyGenericLibsuAloneAsKernelSu()
    {
        // libsu.so is a general root-Activity library used by many apps; a
        // package carrying only that marker is not a KernelSU patcher.
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("lib/arm64-v8a/libsu.so");
        }
        stream.Position = 0;
        var result = await BootPatchAnalyzer.AnalyzeAsync("root-manager.apk", stream, TestContext.Current.CancellationToken);
        Assert.Equal(BootPatchKind.Unknown, result.Kind);
    }

    [Fact]
    public void ScriptParser_PreservesQuotedArgumentsAndLineNumbers()
    {
        var operations = BootPatchScriptParser.Parse("# comment\ncpio ramdisk.cpio \"add 0750 init magiskinit\"\nhexpatch kernel 00 FF");
        Assert.Equal(2, operations.Count);
        Assert.Equal(2, operations[0].LineNumber);
        Assert.Equal("add 0750 init magiskinit", operations[0].Arguments[1]);
        Assert.Equal("hexpatch", operations[1].Command);
    }

    [Fact]
    public void KernelPatchInjector_PreparesPresetAndArm64EntryBranch()
    {
        var kernel = new byte[0x2400];
        BitConverter.GetBytes(0x2000u).CopyTo(kernel, 0x10);
        "ARM\x64"u8.CopyTo(kernel.AsSpan(0x38));
        "CONFIG_KALLSYMS=y\0"u8.CopyTo(kernel.AsSpan(0x200));
        BitConverter.GetBytes(0x14000000u).CopyTo(kernel, 0);
        var kpimg = new byte[0x1100];
        "KP1158"u8.CopyTo(kpimg);

        var patched = KernelPatchInjector.Patch(kernel, kpimg, "password1");
        // Layout: aligned kernel (0x2000) + aligned kpimg (0x1100) + 0x80 extra-item sentinel.
        Assert.Equal(0x3180, patched.Length);
        Assert.Equal(0x00u, patched[patched.Length - 1]);
        Assert.Equal(0x00u, patched[patched.Length - 0x80]);
        Assert.Equal(0x14000c00u, BitConverter.ToUInt32(patched, 0));
        Assert.Equal(0x2000L, BitConverter.ToInt64(patched, 0x2048));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData("password1"u8), patched.AsSpan(0x2128, 32).ToArray());
    }

    [Fact]
    public void MagiskKernelPatcher_AppliesKnownSamsungAndLegacySarPatterns()
    {
        var bytes = new byte[128];
        var pattern = Convert.FromHexString("821B8012");
        pattern.CopyTo(bytes, 16);
        Convert.FromHexString("736B69705F696E697472616D667300").CopyTo(bytes, 40);
        var changes = new List<string>();

        var patched = MagiskKernelPatcher.Apply(bytes, forceRootfs: true, changes);

        Assert.Equal(Convert.FromHexString("E2FF8F12"), patched.AsSpan(16, 4).ToArray());
        Assert.Equal(Convert.FromHexString("77616E745F696E697472616D667300"), patched.AsSpan(40, 15).ToArray());
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void BootArchitectureDetection_UsesFirmwareMagicForEmbeddedElf()
    {
        var elf = new byte[128];
        elf[0] = 0x7f; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        elf[4] = 2; elf[5] = 1;
        BitConverter.GetBytes((ushort)183).CopyTo(elf, 18);
        Assert.Equal("arm64", BootPatchAnalyzer.DetectKernelArchitecture(elf));
    }

    [Fact]
    public void KernelVersionExtractor_ExtractsReleaseAndKmi()
    {
        var kernel = "\0Linux version 5.15.123-android13-5.15-2024 (builder)\0"u8.ToArray();
        Assert.Equal("5.15.123-android13-5.15-2024", KernelVersionExtractor.ExtractRelease(kernel));
        Assert.Equal("android13-5.15-2024", KernelVersionExtractor.ExtractKmi(kernel));
    }

    [Theory]
    [InlineData("lib/modules/android12-5.10/kernelsu.ko", "android12-5.10")]
    [InlineData("android13_5.15_kernelsu.ko", "android13-5.15")]
    public void KernelSuAssetNames_ExposeKmi(string name, string expected)
    {
        Assert.True(KernelSuEmbeddedExtractor.TryGetKmi(name, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task PatchAsync_KernelSuPackage_InstallsRootInitAndLkm()
    {
        var work = Path.Combine(Path.GetTempPath(), "uotan-bootpatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var kernel = "\0Linux version 5.15.123-android13-5.15 (builder)\0"u8.ToArray();
            var stockInit = "stock-init"u8.ToArray();
            var ksuinit = "ksu-init"u8.ToArray();
            var lkm = "fake-kernelsu-ko"u8.ToArray();
            var ramdisk = BuildCpio([("init", stockInit)]);
            var bootPath = Path.Combine(work, "boot.img");
            var packagePath = Path.Combine(work, "kernelsu.apk");
            var outputPath = Path.Combine(work, "patched.img");
            File.WriteAllBytes(bootPath, PackBootImage(kernel, ramdisk));
            File.WriteAllBytes(Path.Combine(work, "ksuinit.bin"), ksuinit);
            File.WriteAllBytes(Path.Combine(work, "lkm.bin"), lkm);
            using (var zip = System.IO.Compression.ZipFile.Open(packagePath, System.IO.Compression.ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(Path.Combine(work, "ksuinit.bin"), "lib/arm64-v8a/init");
                zip.CreateEntryFromFile(Path.Combine(work, "lkm.bin"), "lib/arm64-v8a/android13-5.15/kernelsu.ko");
            }

            var result = await new BootPatchService().PatchAsync(packagePath, bootPath, outputPath, cancellationToken: TestContext.Current.CancellationToken);

            using var parser = new UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.BootImageParser();
            parser.Parse(File.ReadAllBytes(outputPath));
            var editor = new UotanStudio.Firmware.BoundedContexts.Archive.Cpio.CpioArchiveEditor();
            using var ramdiskStream = new MemoryStream(parser.Ramdisk);
            editor.Load(ramdiskStream);
            // Upstream ksud layout: root init (ksuinit), stock init preserved as
            // init.real, LKM at the cpio root, no Magisk overlay.d entries.
            Assert.Equal(ksuinit, editor.Find("init")!.Data);
            Assert.Equal(stockInit, editor.Find("init.real")!.Data);
            Assert.Equal(lkm, editor.Find("kernelsu.ko")!.Data);
            Assert.DoesNotContain(editor.Entries, e => e.Name.Contains("overlay.d", StringComparison.Ordinal));
            Assert.Contains("install init (ksuinit)", result.Changes);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task PatchAsync_KernelSuOnMagiskPatchedImage_IsRejected()
    {
        var work = Path.Combine(Path.GetTempPath(), "uotan-bootpatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var kernel = "\0Linux version 5.15.123-android13-5.15 (builder)\0"u8.ToArray();
            var ramdisk = BuildCpio([("init", "stock-init"u8.ToArray()), (".backup/.magisk", "KEEPVERITY=true\n"u8.ToArray())]);
            var bootPath = Path.Combine(work, "boot.img");
            var packagePath = Path.Combine(work, "kernelsu.apk");
            var outputPath = Path.Combine(work, "patched.img");
            File.WriteAllBytes(bootPath, PackBootImage(kernel, ramdisk));
            File.WriteAllBytes(Path.Combine(work, "lkm.bin"), "fake-kernelsu-ko"u8.ToArray());
            using (var zip = System.IO.Compression.ZipFile.Open(packagePath, System.IO.Compression.ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(Path.Combine(work, "lkm.bin"), "lib/arm64-v8a/android13-5.15/kernelsu.ko");
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => new BootPatchService().PatchAsync(
                packagePath, bootPath, outputPath, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static byte[] BuildCpio(params (string Name, byte[] Data)[] entries)
    {
        var archive = new UotanStudio.Firmware.BoundedContexts.Archive.Cpio.CpioArchive();
        using var stream = new MemoryStream();
        archive.SaveAll(stream, [.. entries.Select(e => new UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model.ArchiveEntry
        {
            Name = e.Name,
            Data = e.Data,
            Metadata = new UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model.ArchiveEntryMetadata
            {
                FileType = UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model.CpioFileType.Regular,
                UnixPermissions = 0x81ED,
                LinkCount = 1
            }
        })], UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model.ArchiveFormat.NewAscii);
        return stream.ToArray();
    }

    private static byte[] PackBootImage(byte[] kernel, byte[] ramdisk)
    {
        var packer = new UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.BootImagePacker
        {
            Header = new UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.BootImageHeader
            {
                HeaderVersion = UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.BootImageVersion.V3,
                HeaderSize = 1580,
                OsVersion = 0x01030000,
                CommandLine = "androidboot.slot_suffix=_a",
            },
            Kernel = kernel,
            Ramdisk = ramdisk,
        };
        using var ms = new MemoryStream();
        packer.PackToStream(ms);
        return ms.ToArray();
    }
}