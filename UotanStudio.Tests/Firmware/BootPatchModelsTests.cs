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
        Assert.Equal(0x3100, patched.Length);
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
}