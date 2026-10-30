using System.IO.Compression;
using UotanStudio.Firmware.BootSecurity.Patcher;
using UotanStudio.Firmware.BootSecurity.Patcher.Kernel;
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
            zip.CreateEntry("assets/kpimg");
            zip.CreateEntry("lib/arm64-v8a/libkptools.so");
            zip.CreateEntry("lib/x86_64/libkptools.so");
        }
        stream.Position = 0;
        var result = await BootPatchAnalyzer.AnalyzeAsync("apatch.apk", stream, TestContext.Current.CancellationToken);
        Assert.Equal(BootPatchKind.APatch, result.Kind);
        Assert.Equal(["arm64", "x64"], result.Architectures);
    }

    [Theory]
    [InlineData("assets/kpm/kpimg")]
    [InlineData("assets/kpimg")]
    public async Task Analyzer_RecognizesKernelPatchImageWithoutApatchScripts(string kpimgEntry)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry(kpimgEntry);
            zip.CreateEntry("lib/arm64-v8a/libkptools.so");
        }
        stream.Position = 0;
        var result = await BootPatchAnalyzer.AnalyzeAsync("sukisu.apk", stream, TestContext.Current.CancellationToken);
        Assert.Equal(BootPatchKind.KernelPatch, result.Kind);
        Assert.Equal(["arm64"], result.Architectures);
    }

    [Fact]
    public async Task Analyzer_MagiskMarkersWithKpimg_StayMagisk()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("assets/kpimg");
            zip.CreateEntry("lib/arm64-v8a/libmagiskinit.so");
            zip.CreateEntry("lib/arm64-v8a/libmagisk.so");
        }
        stream.Position = 0;
        var result = await BootPatchAnalyzer.AnalyzeAsync("Magisk-v31.apk", stream, TestContext.Current.CancellationToken);
        Assert.Equal(BootPatchKind.Magisk, result.Kind);
    }

    [Fact]
    public async Task PatchAsync_KernelPatchPackage_InjectsKpimgWithTheSuppliedKey()
    {
        var work = Path.Combine(Path.GetTempPath(), "uotan-kernelpatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var kernel = new byte[0x2400];
            BitConverter.GetBytes(0x2000u).CopyTo(kernel, 0x10);
            "ARM\x64"u8.CopyTo(kernel.AsSpan(0x38));
            "CONFIG_KALLSYMS=y\0"u8.CopyTo(kernel.AsSpan(0x200));
            BitConverter.GetBytes(0x14000000u).CopyTo(kernel, 0);
            var kpimg = new byte[0x1100];
            "KP1158"u8.CopyTo(kpimg);
            var bootPath = Path.Combine(work, "boot.img");
            var packagePath = Path.Combine(work, "sukisu.apk");
            var outputPath = Path.Combine(work, "patched.img");
            File.WriteAllBytes(bootPath, PackBootImage(kernel, UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V3));
            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                using (var entry = zip.CreateEntry("assets/kpm/kpimg").Open())
                    entry.Write(kpimg);
                zip.CreateEntry("lib/arm64-v8a/libkptools.so");
            }

            var result = await new BootPatchService().PatchAsync(packagePath, bootPath, outputPath,
                new BootPatchOptions { SuperPassword = "123" }, TestContext.Current.CancellationToken);

            using var parser = new UotanStudio.Firmware.BootSecurity.BootImage.Parser.BootImageParser();
            parser.ParseFile(outputPath);
            Assert.Equal(KernelPatchInjector.Patch(kernel, kpimg, "123"), parser.Kernel);
            Assert.Contains("inject KernelPatch kpimg into kernel", result.Changes);
            Assert.Equal(BootPatchKind.KernelPatch, result.Detection.Kind);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
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

    [Fact]
    public void KernelVersionExtractor_UtsReleaseMarker_IsUsedWhenNoBannerExists()
    {
        var kernel = "junk\0UTS_RELEASE=\"5.10.1-android12-5.10\"\0trailing"u8.ToArray();

        Assert.Equal("5.10.1-android12-5.10", KernelVersionExtractor.ExtractRelease(kernel));
        Assert.Equal("android12-5.10", KernelVersionExtractor.ExtractKmi(kernel));
    }

    [Fact]
    public void KernelVersionExtractor_BannerWinsOverUtsMarkerRegardlessOfOrder()
    {
        var kernel = "UTS_RELEASE=\"android12-5.10\"\0linux VERSION 6.1.1-android14-11\0"u8.ToArray();

        Assert.Equal("6.1.1-android14-11", KernelVersionExtractor.ExtractRelease(kernel));
    }

    [Fact]
    public void KernelVersionExtractor_MissingBannerAndMarker_ReturnsNull()
    {
        Assert.Null(KernelVersionExtractor.ExtractRelease("no version marker here"u8));
        Assert.Null(KernelVersionExtractor.ExtractKmi("no version marker here"u8));
    }

    [Fact]
    public void MagiskKernelPatcher_Apply_MatchesSequentialPatternReplacement()
    {
        var kernel = BuildPatternKernel();
        foreach (var forceRootfs in new[] { false, true })
        {
            var expected = ReferenceApply(kernel, forceRootfs);
            var changes = new List<string>();

            var actual = MagiskKernelPatcher.Apply(kernel, forceRootfs, changes);

            Assert.Equal(expected.Image, actual);
            Assert.Equal(expected.Changes, changes);
        }
    }

    [Fact]
    public void MagiskKernelPatcher_ApplyDetectingRootfs_MatchesNeedsLegacyRootfsCall()
    {
        var kernel = BuildPatternKernel();
        foreach (var forceRootfs in new[] { false, true })
        {
            var detected = forceRootfs || kernel.AsSpan().IndexOf("skip_initramfs"u8) >= 0;
            var expected = ReferenceApply(kernel, detected);
            var changes = new List<string>();

            var actual = MagiskKernelPatcher.ApplyDetectingRootfs(kernel, forceRootfs, changes);

            Assert.Equal(expected.Image, actual);
            Assert.Equal(expected.Changes, changes);
        }
    }

    [Fact]
    public void SplitEmbeddedKernelDtb_V1CompleteBlob_SplitsAtTheBlobStart()
    {
        var kernel = new byte[0x400];
        kernel.AsSpan().Fill(0x21);
        WriteDtb(kernel, 0x200, 0x48);
        using var parser = new UotanStudio.Firmware.BootSecurity.BootImage.Parser.BootImageParser();
        parser.Parse(PackBootImage(kernel, UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V1));

        BootPatchService.SplitEmbeddedKernelDtb(parser, parser.Kernel, out var body, out var dtb);

        Assert.Equal(kernel[..0x200], body.ToArray());
        Assert.Equal(kernel[0x200..], dtb);
    }

    [Fact]
    public void SplitEmbeddedKernelDtb_V1IncompleteBlobBeforeCompleteOne_SplitsAtTheCompleteBlob()
    {
        var kernel = new byte[0x600];
        kernel.AsSpan().Fill(0x22);
        WriteDtb(kernel, 0x100, 4);
        WriteDtb(kernel, 0x300, 0x180);
        using var parser = new UotanStudio.Firmware.BootSecurity.BootImage.Parser.BootImageParser();
        parser.Parse(PackBootImage(kernel, UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V1));

        BootPatchService.SplitEmbeddedKernelDtb(parser, parser.Kernel, out var body, out var dtb);

        Assert.Equal(kernel[..0x300], body.ToArray());
        Assert.Equal(kernel[0x300..], dtb);
    }

    [Fact]
    public void SplitEmbeddedKernelDtb_V2Image_KeepsTheWholeKernel()
    {
        var kernel = new byte[0x400];
        kernel.AsSpan().Fill(0x23);
        WriteDtb(kernel, 0x200, 0x48);
        using var parser = new UotanStudio.Firmware.BootSecurity.BootImage.Parser.BootImageParser();
        parser.Parse(PackBootImage(kernel, UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V2));

        BootPatchService.SplitEmbeddedKernelDtb(parser, parser.Kernel, out var body, out var dtb);

        Assert.Equal(kernel, body.ToArray());
        Assert.Null(dtb);
    }

    [Fact]
    public async Task DetectBootKernelAsync_ElfKernel_ReportsArchitectureAndKmi()
    {
        var work = Path.Combine(Path.GetTempPath(), "uotan-bootkernel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var kernel = new byte[0x800];
            kernel[0] = 0x7f; kernel[1] = (byte)'E'; kernel[2] = (byte)'L'; kernel[3] = (byte)'F';
            kernel[4] = 2; kernel[5] = 1; kernel[6] = 1;
            BitConverter.GetBytes((ushort)2).CopyTo(kernel, 16);
            BitConverter.GetBytes((ushort)183).CopyTo(kernel, 18);
            "Linux version 5.15.123-android13-5.15-2024 (builder)\0"u8.CopyTo(kernel.AsSpan(0x100));
            var bootPath = Path.Combine(work, "boot.img");
            File.WriteAllBytes(bootPath, PackBootImage(kernel, UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V3));

            var info = await BootPatchAnalyzer.DetectBootKernelAsync(bootPath, TestContext.Current.CancellationToken);

            Assert.Equal("arm64", info.Architecture);
            Assert.Equal("android13-5.15-2024", info.Kmi);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task PatchWithDetectionAsync_PrecomputedDetection_ProducesTheSameImageAsPatchAsync()
    {
        var work = Path.Combine(Path.GetTempPath(), "uotan-bootdetect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var kernel = "\0Linux version 5.15.123-android13-5.15 (builder)\0"u8.ToArray();
            var ramdisk = BuildCpio([("init", "stock-init"u8.ToArray())]);
            var bootPath = Path.Combine(work, "boot.img");
            var packagePath = Path.Combine(work, "kernelsu.apk");
            var first = Path.Combine(work, "first.img");
            var second = Path.Combine(work, "second.img");
            File.WriteAllBytes(bootPath, PackBootImage(kernel, UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V3, ramdisk));
            File.WriteAllBytes(Path.Combine(work, "ksuinit.bin"), "ksu-init"u8.ToArray());
            File.WriteAllBytes(Path.Combine(work, "lkm.bin"), "fake-kernelsu-ko"u8.ToArray());
            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(Path.Combine(work, "ksuinit.bin"), "lib/arm64-v8a/init");
                zip.CreateEntryFromFile(Path.Combine(work, "lkm.bin"), "lib/arm64-v8a/android13-5.15/kernelsu.ko");
            }

            var service = new BootPatchService();
            var detection = await service.DetectAsync(packagePath, TestContext.Current.CancellationToken);
            var direct = await service.PatchAsync(packagePath, bootPath, first, cancellationToken: TestContext.Current.CancellationToken);
            var reused = await service.PatchWithDetectionAsync(packagePath, bootPath, second, null, detection,
                TestContext.Current.CancellationToken);

            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
            Assert.Equal(direct.Changes, reused.Changes);
            Assert.Equal(direct.Detection.Kind, reused.Detection.Kind);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
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
            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(Path.Combine(work, "ksuinit.bin"), "lib/arm64-v8a/init");
                zip.CreateEntryFromFile(Path.Combine(work, "lkm.bin"), "lib/arm64-v8a/android13-5.15/kernelsu.ko");
            }

            var result = await new BootPatchService().PatchAsync(packagePath, bootPath, outputPath, cancellationToken: TestContext.Current.CancellationToken);

            using var parser = new UotanStudio.Firmware.BootSecurity.BootImage.Parser.BootImageParser();
            parser.Parse(File.ReadAllBytes(outputPath));
            var editor = new UotanStudio.Firmware.Archive.Editing.CpioArchiveEditor();
            using var ramdiskStream = new MemoryStream(parser.Ramdisk);
            editor.Load(ramdiskStream);
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
    public async Task PatchAsync_MagiskV24SplitLayout_InstallsTheSplitNativeLibraries()
    {
        var work = Path.Combine(Path.GetTempPath(), "uotan-bootpatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var kernel = "\0Linux version 5.15.123-android13-5.15 (builder)\0"u8.ToArray();
            var stockInit = "stock-init"u8.ToArray();
            var magiskInit = "magisk-init"u8.ToArray();
            var magisk32 = "fake-magisk32"u8.ToArray();
            var magisk64 = "fake-magisk64"u8.ToArray();
            var ramdisk = BuildCpio([("init", stockInit)]);
            var bootPath = Path.Combine(work, "boot.img");
            var packagePath = Path.Combine(work, "Magisk-v24.0.apk");
            var outputPath = Path.Combine(work, "patched.img");
            File.WriteAllBytes(bootPath, PackBootImage(kernel, ramdisk));
            File.WriteAllBytes(Path.Combine(work, "magiskinit.bin"), magiskInit);
            File.WriteAllBytes(Path.Combine(work, "magisk32.bin"), magisk32);
            File.WriteAllBytes(Path.Combine(work, "magisk64.bin"), magisk64);
            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                zip.CreateEntry("assets/boot_patch.sh");
                using (var util = new StreamWriter(zip.CreateEntry("assets/util_functions.sh").Open()))
                    util.WriteLine("MAGISK_VER='24.0:24000'");
                zip.CreateEntryFromFile(Path.Combine(work, "magiskinit.bin"), "lib/arm64-v8a/libmagiskinit.so");
                zip.CreateEntryFromFile(Path.Combine(work, "magisk32.bin"), "lib/arm64-v8a/libmagisk32.so");
                zip.CreateEntryFromFile(Path.Combine(work, "magisk64.bin"), "lib/arm64-v8a/libmagisk64.so");
            }

            var result = await new BootPatchService().PatchAsync(packagePath, bootPath, outputPath,
                cancellationToken: TestContext.Current.CancellationToken);

            using var parser = new UotanStudio.Firmware.BootSecurity.BootImage.Parser.BootImageParser();
            parser.Parse(File.ReadAllBytes(outputPath));
            var editor = new UotanStudio.Firmware.Archive.Editing.CpioArchiveEditor();
            using var ramdiskStream = new MemoryStream(parser.Ramdisk);
            editor.Load(ramdiskStream);
            Assert.Equal(magisk32,
                UotanStudio.Firmware.BootSecurity.BootImage.Components.BootCompressor.TryDecompress(
                    editor.Find("overlay.d/sbin/magisk32.xz")!.Data,
                    UotanStudio.Firmware.BootSecurity.BootImage.Models.CompressionType.Xz));
            Assert.Equal(magisk64,
                UotanStudio.Firmware.BootSecurity.BootImage.Components.BootCompressor.TryDecompress(
                    editor.Find("overlay.d/sbin/magisk64.xz")!.Data,
                    UotanStudio.Firmware.BootSecurity.BootImage.Models.CompressionType.Xz));
            Assert.Equal(magiskInit, editor.Find("init")!.Data);
            Assert.NotNull(editor.Find(".backup/.magisk"));
            Assert.Contains("install overlay.d/sbin/magisk32.xz", result.Changes);
            Assert.Contains("install overlay.d/sbin/magisk64.xz", result.Changes);
            Assert.Equal("24.0:24000", result.Detection.Version);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task PatchAsync_MagiskV23SplitLayout_InstallsTheSplitNativeLibraries()
    {
        var work = Path.Combine(Path.GetTempPath(), "uotan-bootpatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var kernel = "\0Linux version 5.15.123-android13-5.15 (builder)\0"u8.ToArray();
            var stockInit = "stock-init"u8.ToArray();
            var magiskInit = "magisk-init"u8.ToArray();
            var magisk32 = "fake-magisk32"u8.ToArray();
            var magisk64 = "fake-magisk64"u8.ToArray();
            var ramdisk = BuildCpio([("init", stockInit)]);
            var bootPath = Path.Combine(work, "boot.img");
            var packagePath = Path.Combine(work, "Magisk-v23.0.apk");
            var outputPath = Path.Combine(work, "patched.img");
            File.WriteAllBytes(bootPath, PackBootImage(kernel, ramdisk));
            File.WriteAllBytes(Path.Combine(work, "magiskinit.bin"), magiskInit);
            File.WriteAllBytes(Path.Combine(work, "magisk32.bin"), magisk32);
            File.WriteAllBytes(Path.Combine(work, "magisk64.bin"), magisk64);
            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                zip.CreateEntry("assets/boot_patch.sh");
                using (var util = new StreamWriter(zip.CreateEntry("assets/util_functions.sh").Open()))
                    util.WriteLine("MAGISK_VER='23.0:23000'");
                zip.CreateEntryFromFile(Path.Combine(work, "magiskinit.bin"), "lib/arm64-v8a/libmagiskinit.so");
                zip.CreateEntryFromFile(Path.Combine(work, "magisk32.bin"), "lib/arm64-v8a/libmagisk32.so");
                zip.CreateEntryFromFile(Path.Combine(work, "magisk64.bin"), "lib/arm64-v8a/libmagisk64.so");
            }

            var result = await new BootPatchService().PatchAsync(packagePath, bootPath, outputPath,
                cancellationToken: TestContext.Current.CancellationToken);

            using var parser = new UotanStudio.Firmware.BootSecurity.BootImage.Parser.BootImageParser();
            parser.Parse(File.ReadAllBytes(outputPath));
            var editor = new UotanStudio.Firmware.Archive.Editing.CpioArchiveEditor();
            using var ramdiskStream = new MemoryStream(parser.Ramdisk);
            editor.Load(ramdiskStream);
            Assert.Equal(magisk32,
                UotanStudio.Firmware.BootSecurity.BootImage.Components.BootCompressor.TryDecompress(
                    editor.Find("overlay.d/sbin/magisk32.xz")!.Data,
                    UotanStudio.Firmware.BootSecurity.BootImage.Models.CompressionType.Xz));
            Assert.Equal(magisk64,
                UotanStudio.Firmware.BootSecurity.BootImage.Components.BootCompressor.TryDecompress(
                    editor.Find("overlay.d/sbin/magisk64.xz")!.Data,
                    UotanStudio.Firmware.BootSecurity.BootImage.Models.CompressionType.Xz));
            Assert.Contains("install overlay.d/sbin/magisk64.xz", result.Changes);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Analyzer_LegacyMagiskZipLayout_IsRecognizedButReportsWhyItCannotPatch()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("META-INF/com/google/android/update-binary");
            zip.CreateEntry("arm/magiskinit");
            zip.CreateEntry("arm/magiskinit64");
            zip.CreateEntry("common/boot_patch.sh");
            using var util = new StreamWriter(zip.CreateEntry("common/util_functions.sh").Open());
            util.WriteLine("MAGISK_VER='21.4'");
        }
        stream.Position = 0;
        var result = await BootPatchAnalyzer.AnalyzeAsync("Magisk-v21.4.zip", stream, TestContext.Current.CancellationToken);

        Assert.Equal(BootPatchKind.Magisk, result.Kind);
        Assert.Equal("21.4", result.Version);
        Assert.False(result.IsSupported);
        Assert.NotNull(result.UnsupportedReason);
        Assert.Contains("v22.0", result.UnsupportedReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyzer_MagiskWithoutInstallablePayload_ReportsWhyItCannotPatch()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var util = new StreamWriter(zip.CreateEntry("assets/util_functions.sh").Open()))
                util.WriteLine("MAGISK_VER='30.0:30000'");
            zip.CreateEntry("lib/arm64-v8a/libmagiskpolicy.so");
            zip.CreateEntry("lib/arm64-v8a/libmagiskboot.so");
        }
        stream.Position = 0;
        var result = await BootPatchAnalyzer.AnalyzeAsync("Magisk-v30.0.apk", stream, TestContext.Current.CancellationToken);

        Assert.Equal(BootPatchKind.Magisk, result.Kind);
        Assert.False(result.IsSupported);
        Assert.NotNull(result.UnsupportedReason);
        Assert.Contains("no installable runtime payload", result.UnsupportedReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyzer_SukiSuManagerOnlyBundle_ReportsWhyItCannotPatch()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("lib/arm64-v8a/libzako.so");
            zip.CreateEntry("lib/arm64-v8a/libzakoboot.so");
            zip.CreateEntry("assets/ksu_susfs_1.5.9");
        }
        stream.Position = 0;
        var result = await BootPatchAnalyzer.AnalyzeAsync("SukiSU.apk", stream, TestContext.Current.CancellationToken);

        Assert.Equal(BootPatchKind.Unknown, result.Kind);
        Assert.False(result.IsSupported);
        Assert.NotNull(result.UnsupportedReason);
        Assert.Contains("manager-only", result.UnsupportedReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatchAsync_KernelPatchPackageOnNonArm64Kernel_IsRejected()
    {
        var work = Path.Combine(Path.GetTempPath(), "uotan-bootpatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var kernel = new byte[0x1000];
            kernel.AsSpan().Fill(0x11);
            var bootPath = Path.Combine(work, "boot.img");
            var packagePath = Path.Combine(work, "APatch.apk");
            var outputPath = Path.Combine(work, "patched.img");
            File.WriteAllBytes(bootPath, PackBootImage(kernel, BuildCpio(("init", "stock-init"u8.ToArray()))));
            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                using (var kpimg = zip.CreateEntry("assets/kpimg").Open())
                    kpimg.Write(new byte[0x1100]);
                zip.CreateEntry("lib/arm64-v8a/libkptools.so");
            }

            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => new BootPatchService().PatchAsync(
                packagePath, bootPath, outputPath,
                new BootPatchOptions { SuperPassword = "abc12345" },
                TestContext.Current.CancellationToken));

            Assert.Contains("arm64 boot kernels only", failure.Message, StringComparison.Ordinal);
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
            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
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

    [Theory]
    [InlineData(BootPatchKind.Magisk, BootPatchMode.Auto, false, true)]
    [InlineData(BootPatchKind.KernelSu, BootPatchMode.Lkm, true, true)]
    [InlineData(BootPatchKind.KernelSu, BootPatchMode.Kpm, true, false)]
    [InlineData(BootPatchKind.KernelPatch, BootPatchMode.Kpm, true, false)]
    [InlineData(BootPatchKind.APatch, BootPatchMode.Auto, true, false)]
    [InlineData(BootPatchKind.GkiKernel, BootPatchMode.Gki, true, false)]
    public void Compatibility_Requirements_FollowKindAndMode(BootPatchKind kind, BootPatchMode mode,
        bool needsKernel, bool needsRamdisk)
    {
        Assert.Equal(needsKernel, BootPatchCompatibility.RequiresKernel(kind, mode));
        Assert.Equal(needsRamdisk, BootPatchCompatibility.RequiresRamdisk(kind, mode));
    }

    [Fact]
    public void Compatibility_MissingRamdisk_BlocksMagiskAndKernelSuLkm()
    {
        var boot = new BootImageInspection("arm64", "android13-5.15", HasKernel: true, HasRamdisk: false);

        Assert.Equal(BootPatchCompatibilityProblem.RamdiskRequired, BootPatchCompatibility.Check(
            Detection(BootPatchKind.Magisk, ["arm64"]), BootPatchMode.Auto, boot));
        Assert.Equal(BootPatchCompatibilityProblem.RamdiskRequired, BootPatchCompatibility.Check(
            Detection(BootPatchKind.KernelSu, ["arm64"], ["android13-5.15"]), BootPatchMode.Lkm, boot));
        Assert.Equal(BootPatchCompatibilityProblem.None, BootPatchCompatibility.Check(
            Detection(BootPatchKind.KernelSu, ["arm64"], ["android13-5.15"], kpimg: true),
            BootPatchMode.Kpm, boot));
    }

    [Theory]
    [InlineData(BootPatchKind.KernelSu, BootPatchMode.Lkm)]
    [InlineData(BootPatchKind.KernelPatch, BootPatchMode.Kpm)]
    [InlineData(BootPatchKind.APatch, BootPatchMode.Auto)]
    [InlineData(BootPatchKind.GkiKernel, BootPatchMode.Gki)]
    public void Compatibility_MissingKernel_BlocksKernelBasedModes(BootPatchKind kind, BootPatchMode mode)
    {
        var boot = new BootImageInspection(null, null, HasKernel: false, HasRamdisk: true);

        Assert.Equal(BootPatchCompatibilityProblem.KernelRequired, BootPatchCompatibility.Check(
            Detection(kind, ["arm64"]), mode, boot));
    }

    [Fact]
    public void Compatibility_ArchitectureUnsupportedByPackage_IsReported()
    {
        var boot = new BootImageInspection("x64", "android13-5.15", HasKernel: true, HasRamdisk: true);

        Assert.Equal(BootPatchCompatibilityProblem.ArchitectureMismatch, BootPatchCompatibility.Check(
            Detection(BootPatchKind.Magisk, ["arm64", "arm32"]), BootPatchMode.Auto, boot));
    }

    [Fact]
    public void Compatibility_ExplicitArchitectureDifferentFromBoot_IsReported()
    {
        var boot = new BootImageInspection("arm64", "android13-5.15", HasKernel: true, HasRamdisk: true);

        Assert.Equal(BootPatchCompatibilityProblem.SelectedArchitectureMismatch, BootPatchCompatibility.Check(
            Detection(BootPatchKind.Magisk, ["arm64", "x64"]), BootPatchMode.Auto, boot, selectedArchitecture: "x64"));
    }

    [Fact]
    public void Compatibility_KernelSuLkmWithoutBootKmiOrSelection_IsReported()
    {
        var boot = new BootImageInspection("arm64", null, HasKernel: true, HasRamdisk: true);

        Assert.Equal(BootPatchCompatibilityProblem.KmiUnknown, BootPatchCompatibility.Check(
            Detection(BootPatchKind.KernelSu, ["arm64"], ["android13-5.15"]), BootPatchMode.Lkm, boot));
        Assert.Equal(BootPatchCompatibilityProblem.None, BootPatchCompatibility.Check(
            Detection(BootPatchKind.KernelSu, ["arm64"], ["android13-5.15"]), BootPatchMode.Lkm, boot,
            selectedKmi: "android13-5.15"));
    }

    [Fact]
    public void Compatibility_KernelSuLkmWithoutMatchingModule_IsReported()
    {
        var boot = new BootImageInspection("arm64", "android14-6.1", HasKernel: true, HasRamdisk: true);

        Assert.Equal(BootPatchCompatibilityProblem.KernelSuKmiMismatch, BootPatchCompatibility.Check(
            Detection(BootPatchKind.KernelSu, ["arm64"], ["android13-5.15"]), BootPatchMode.Lkm, boot));
    }

    [Fact]
    public void Compatibility_GkiKernelKmiMismatch_IsReported()
    {
        var boot = new BootImageInspection("arm64", "android13-5.15", HasKernel: true, HasRamdisk: true);

        Assert.Equal(BootPatchCompatibilityProblem.GkiKmiMismatch, BootPatchCompatibility.Check(
            Detection(BootPatchKind.GkiKernel, ["arm64"], ["android14-6.1"]), BootPatchMode.Gki, boot));
        Assert.Equal(BootPatchCompatibilityProblem.None, BootPatchCompatibility.Check(
            Detection(BootPatchKind.GkiKernel, ["arm64"], ["android13-5.15"]), BootPatchMode.Gki, boot));
    }

    [Fact]
    public void Compatibility_GkiSelectionDifferentFromBootKmi_IsReported()
    {
        var boot = new BootImageInspection("arm64", "android13-5.15", HasKernel: true, HasRamdisk: true);

        Assert.Equal(BootPatchCompatibilityProblem.GkiKmiMismatch, BootPatchCompatibility.Check(
            Detection(BootPatchKind.GkiKernel, ["arm64"], ["android13-5.15", "android14-6.1"]), BootPatchMode.Gki,
            boot, selectedKmi: "android14-6.1"));
        Assert.Equal(BootPatchCompatibilityProblem.None, BootPatchCompatibility.Check(
            Detection(BootPatchKind.GkiKernel, ["arm64"], ["android13-5.15", "android14-6.1"]), BootPatchMode.Gki,
            boot, selectedKmi: "android13-5.15"));
    }

    [Theory]
    [InlineData(BootPatchKind.APatch, BootPatchMode.Auto, "abc", false)]
    [InlineData(BootPatchKind.APatch, BootPatchMode.Auto, "patch123", true)]
    [InlineData(BootPatchKind.KernelPatch, BootPatchMode.Kpm, "", false)]
    [InlineData(BootPatchKind.KernelPatch, BootPatchMode.Kpm, "abc", true)]
    [InlineData(BootPatchKind.KernelSu, BootPatchMode.Lkm, null, true)]
    [InlineData(BootPatchKind.Magisk, BootPatchMode.Auto, null, true)]
    public void Compatibility_SuperKeyRequirement_FollowsFamilyAndMode(BootPatchKind kind, BootPatchMode mode,
        string? superKey, bool acceptable)
    {
        Assert.Equal(acceptable, BootPatchCompatibility.IsSuperKeyAcceptable(kind, mode, superKey));
    }

    [Theory]
    [InlineData(1, true, BootPatchMode.Lkm)]
    [InlineData(0, true, BootPatchMode.Kpm)]
    [InlineData(0, false, BootPatchMode.Lkm)]
    public void ResolvePatchMode_KernelSuPrefersTheVerifiedLkmPayload(int kmiCount, bool kpimg, BootPatchMode expected)
    {
        var kmis = Enumerable.Range(0, kmiCount).Select(index => $"android1{index}-5.10").ToArray();
        var detection = Detection(BootPatchKind.KernelSu, ["arm64"], kmis, kpimg);

        Assert.Equal(expected, BootPatchService.ResolvePatchMode(detection, BootPatchMode.Auto));
    }

    [Fact]
    public void ResolvePatchMode_KernelPatchImageOnlyPackage_TakesKpm()
    {
        var detection = Detection(BootPatchKind.KernelPatch, ["arm64"], kpimg: true);

        Assert.Equal(BootPatchMode.Kpm, BootPatchService.ResolvePatchMode(detection, BootPatchMode.Auto));
    }

    [Fact]
    public async Task Analyzer_KernelSuRawModuleNamesWithoutVerifiedAssets_ReportNoKmi()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("lib/arm64-v8a/libksud.so").Open();
            entry.Write("android13-5.15_kernelsu.ko"u8);
        }

        stream.Position = 0;
        var result = await BootPatchAnalyzer.AnalyzeAsync("kernelsu.apk", stream, TestContext.Current.CancellationToken);

        Assert.Equal(BootPatchKind.KernelSu, result.Kind);
        Assert.Empty(result.Kmis!);
    }

    [Fact]
    public async Task Analyzer_GkiKernelImage_ReportsItsKmiAndArchitecture()
    {
        var kernel = new byte[0x2000];
        "ARM\x64"u8.CopyTo(kernel.AsSpan(0x38));
        "\0Linux version 5.15.123-android13-5.15 (builder)\0"u8.CopyTo(kernel.AsSpan(0x200));
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("Image").Open();
            entry.Write(kernel);
        }

        stream.Position = 0;
        var result = await BootPatchAnalyzer.AnalyzeAsync("gki.zip", stream, TestContext.Current.CancellationToken);

        Assert.Equal(BootPatchKind.GkiKernel, result.Kind);
        Assert.Equal(["arm64"], result.Architectures);
        Assert.Equal(["android13-5.15"], result.Kmis!);
    }

    [Fact]
    public async Task InspectBootAsync_KernelAndRamdisk_ReportsIdentityAndComponents()
    {
        var work = Path.Combine(Path.GetTempPath(), "uotan-bootinspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var kernel = new byte[0x2000];
            "ARM\x64"u8.CopyTo(kernel.AsSpan(0x38));
            "\0Linux version 5.15.123-android13-5.15 (builder)\0"u8.CopyTo(kernel.AsSpan(0x200));
            var bootPath = Path.Combine(work, "boot.img");
            File.WriteAllBytes(bootPath, PackBootImage(kernel,
                UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V3,
                BuildCpio(("init", "stock-init"u8.ToArray()))));

            var inspection = await BootPatchAnalyzer.InspectBootAsync(bootPath, TestContext.Current.CancellationToken);

            Assert.Equal("arm64", inspection.Architecture);
            Assert.Equal("android13-5.15", inspection.Kmi);
            Assert.True(inspection.HasKernel);
            Assert.True(inspection.HasRamdisk);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task InspectBootAsync_EmptyKernel_ReportsMissingKernel()
    {
        var work = Path.Combine(Path.GetTempPath(), "uotan-bootinspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var bootPath = Path.Combine(work, "init_boot.img");
            File.WriteAllBytes(bootPath, PackBootImage([],
                UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V3,
                BuildCpio(("init", "stock-init"u8.ToArray()))));

            var inspection = await BootPatchAnalyzer.InspectBootAsync(bootPath, TestContext.Current.CancellationToken);

            Assert.Null(inspection.Architecture);
            Assert.Null(inspection.Kmi);
            Assert.False(inspection.HasKernel);
            Assert.True(inspection.HasRamdisk);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task PatchAsync_ApatchPackage_LeavesTheRamdiskBytesUntouched()
    {
        var work = Path.Combine(Path.GetTempPath(), "uotan-apatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var kernel = new byte[0x2400];
            BitConverter.GetBytes(0x2000u).CopyTo(kernel, 0x10);
            "ARM\x64"u8.CopyTo(kernel.AsSpan(0x38));
            "CONFIG_KALLSYMS=y\0"u8.CopyTo(kernel.AsSpan(0x200));
            BitConverter.GetBytes(0x14000000u).CopyTo(kernel, 0);
            var kpimg = new byte[0x1100];
            "KP1158"u8.CopyTo(kpimg);
            var ramdisk = BuildCpio(("init", "stock-init"u8.ToArray()));
            var bootPath = Path.Combine(work, "boot.img");
            var packagePath = Path.Combine(work, "APatch.apk");
            var outputPath = Path.Combine(work, "patched.img");
            File.WriteAllBytes(bootPath, PackBootImage(kernel,
                UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V3, ramdisk));
            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                using (var script = new StreamWriter(zip.CreateEntry("assets/boot_patch.sh").Open()))
                    script.WriteLine("kpimg");
                using (var entry = zip.CreateEntry("assets/kpimg").Open())
                    entry.Write(kpimg);
                zip.CreateEntry("lib/arm64-v8a/libkptools.so");
            }

            var result = await new BootPatchService().PatchAsync(packagePath, bootPath, outputPath,
                new BootPatchOptions { SuperPassword = "abc12345" }, TestContext.Current.CancellationToken);

            using var parser = new UotanStudio.Firmware.BootSecurity.BootImage.Parser.BootImageParser();
            parser.ParseFile(outputPath);
            Assert.Equal(BootPatchKind.APatch, result.Detection.Kind);
            Assert.Equal(KernelPatchInjector.Patch(kernel, kpimg, "abc12345"), parser.Kernel);
            Assert.Equal(ramdisk, parser.Ramdisk);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static BootPatchDetection Detection(BootPatchKind kind, string[] architectures,
        string[]? kmis = null, bool kpimg = false)
        => new(kind, architectures, Kmis: kmis, HasKernelPatchImage: kpimg);

    private static byte[] BuildCpio(params (string Name, byte[] Data)[] entries)
    {
        var archive = new UotanStudio.Firmware.Archive.CpioArchive();
        using var stream = new MemoryStream();
        archive.SaveAll(stream, [.. entries.Select(e => new UotanStudio.Firmware.Archive.Models.ArchiveEntry
        {
            Name = e.Name,
            Data = e.Data,
            Metadata = new UotanStudio.Firmware.Archive.Models.ArchiveEntryMetadata
            {
                FileType = UotanStudio.Firmware.Archive.Models.CpioFileType.Regular,
                UnixPermissions = 0x81ED,
                LinkCount = 1
            }
        })], UotanStudio.Firmware.Archive.Models.ArchiveFormat.NewAscii);
        return stream.ToArray();
    }

    private static readonly (string Find, string Replace, string Name)[] ReferencePatches =
    [
        ("49010054011440B93FA00F71E9000054010840B93FA00F7189000054001840B91FA00F7188010054",
            "A1020054011440B93FA00F7140020054010840B93FA00F71E0010054001840B91FA00F7181010054", "remove Samsung RKP"),
        ("821B8012", "E2FF8F12", "remove Samsung defex"),
        ("006044B91F040071802F005460DE41F9", "006044B91F00006B802F005460DE41F9", "remove Samsung defex A8 variant"),
        ("603A46B91F0400710030005460C642F9", "603A46B91F00006B0030005460C642F9", "remove Samsung defex N9 variant"),
        ("70726F63615F636F6E66696700", "70726F63615F6D616769736B00", "disable Samsung PROCA")
    ];

    private const string ReferenceForceFind = "736B69705F696E697472616D667300";
    private const string ReferenceForceReplace = "77616E745F696E697472616D667300";

    private static byte[] BuildPatternKernel()
    {
        var kernel = new byte[0x1000];
        kernel.AsSpan().Fill(0x11);
        PlacePattern(kernel, 0x100, ReferencePatches[0].Find);
        PlacePattern(kernel, 0x200, "821B8012");
        PlacePattern(kernel, 0x300, "821B8012");
        PlacePattern(kernel, 0x400, ReferencePatches[2].Find);
        PlacePattern(kernel, 0x500, ReferencePatches[3].Find);
        PlacePattern(kernel, 0x600, ReferencePatches[4].Find);
        PlacePattern(kernel, 0x700, ReferenceForceFind);
        return kernel;
    }

    private static void PlacePattern(byte[] kernel, int offset, string hex)
        => Convert.FromHexString(hex).CopyTo(kernel, offset);

    private static void WriteDtb(byte[] kernel, int offset, int total)
    {
        ReadOnlySpan<byte> magic = [0xD0, 0x0D, 0xFE, 0xED];
        magic.CopyTo(kernel.AsSpan(offset));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(kernel.AsSpan(offset + 4), (uint)total);
    }

    private static (byte[] Image, List<string> Changes) ReferenceApply(byte[] kernel, bool forceRootfs)
    {
        var image = (byte[])kernel.Clone();
        var changes = new List<string>();
        foreach (var (find, replace, name) in ReferencePatches)
        {
            if (ReferencePatch(image, find, replace))
                changes.Add($"Magisk kernel: {name}");
        }
        if (forceRootfs && ReferencePatch(image, ReferenceForceFind, ReferenceForceReplace))
            changes.Add("Magisk kernel: force legacy rootfs initialization");
        return (image, changes);
    }

    private static bool ReferencePatch(byte[] image, string find, string replace)
    {
        var pattern = Convert.FromHexString(find);
        var replacement = Convert.FromHexString(replace);
        if (replacement.Length > pattern.Length) return false;
        var offset = image.AsSpan().IndexOf(pattern);
        if (offset < 0) return false;
        replacement.CopyTo(image.AsSpan(offset));
        if (replacement.Length < pattern.Length)
            image.AsSpan(offset + replacement.Length, pattern.Length - replacement.Length).Clear();
        return true;
    }

    private static byte[] PackBootImage(byte[] kernel, UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion version,
        byte[]? ramdisk = null)
    {
        var packer = new UotanStudio.Firmware.BootSecurity.BootImage.BootImagePacker
        {
            Header = new UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageHeader
            {
                HeaderVersion = version,
                OsVersion = 0x01030000,
                CommandLine = "androidboot.slot_suffix=_a",
            },
            Kernel = kernel,
            Ramdisk = ramdisk ?? [],
        };
        using var ms = new MemoryStream();
        packer.PackToStream(ms);
        return ms.ToArray();
    }

    private static byte[] PackBootImage(byte[] kernel, byte[] ramdisk)
    {
        var packer = new UotanStudio.Firmware.BootSecurity.BootImage.BootImagePacker
        {
            Header = new UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageHeader
            {
                HeaderVersion = UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V3,
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