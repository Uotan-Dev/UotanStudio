using System.IO.Compression;
using UotanStudio.Firmware.BootSecurity.Patcher;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class BootPatchModeTests
{
    [Fact]
    public async Task Analyzer_ReportsKernelPatchImageInsideKernelSuPackage()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("assets/kpm/kpimg");
            zip.CreateEntry("lib/arm64-v8a/init");
            zip.CreateEntry("lib/arm64-v8a/android13-5.15/kernelsu.ko");
        }
        stream.Position = 0;

        var result = await BootPatchAnalyzer.AnalyzeAsync("sukisu.apk", stream, TestContext.Current.CancellationToken);

        Assert.Equal(BootPatchKind.KernelSu, result.Kind);
        Assert.True(result.HasKernelPatchImage);
    }

    [Fact]
    public async Task Analyzer_LkmZipWithoutLibDirectories_DetectsTheModuleAndAbi()
    {
        var module = new byte[0x400];
        module[0] = 0x7f; module[1] = (byte)'E'; module[2] = (byte)'L'; module[3] = (byte)'F';
        module[4] = 2; module[5] = 1; module[6] = 1;
        BitConverter.GetBytes((ushort)183).CopyTo(module, 18);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("android12-5.10_kernelsu.ko").Open();
            entry.Write(module);
        }
        stream.Position = 0;

        var result = await BootPatchAnalyzer.AnalyzeAsync("lkm.zip", stream, TestContext.Current.CancellationToken);

        Assert.Equal(BootPatchKind.KernelSu, result.Kind);
        Assert.Equal(["arm64"], result.Architectures);
        Assert.Contains("android12-5.10", result.Kmis!);
        Assert.True(result.IsSupported);
    }

    [Fact]
    public async Task Analyzer_RecognizesRawKernelImageAsGkiKernel()
    {
        var work = CreateWorkDir("uotan-bootanalyzer-");
        try
        {
            var imagePath = Path.Combine(work, "Image");
            File.WriteAllBytes(imagePath, BuildArm64Image(0x400));

            var result = await BootPatchAnalyzer.AnalyzeAsync(imagePath, TestContext.Current.CancellationToken);

            Assert.Equal(BootPatchKind.GkiKernel, result.Kind);
            Assert.Equal(["arm64"], result.Architectures);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task Analyzer_RecognizesAnyKernel3ZipAsGkiKernel()
    {
        var work = CreateWorkDir("uotan-bootanalyzerzip-");
        try
        {
            var imagePath = Path.Combine(work, "Image");
            var packagePath = Path.Combine(work, "anykernel3.zip");
            File.WriteAllBytes(imagePath, BuildArm64Image(0x400));
            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(imagePath, "Image");
            }

            var result = await BootPatchAnalyzer.AnalyzeAsync(packagePath, TestContext.Current.CancellationToken);

            Assert.Equal(BootPatchKind.GkiKernel, result.Kind);
            Assert.Equal(["arm64"], result.Architectures);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task PatchAsync_RawKernelImagePackage_ReplacesTheBootKernelAndKeepsTheRamdisk()
    {
        var work = CreateWorkDir("uotan-bootpatchgki-");
        try
        {
            var kernel = "\0Linux version 5.15.123-android13-5.15 (builder)\0"u8.ToArray();
            var ramdisk = BuildCpio([("init", "stock-init"u8.ToArray())]);
            var replacement = BuildArm64Image(0x400, 0x5A);
            var bootPath = Path.Combine(work, "boot.img");
            var packagePath = Path.Combine(work, "Image");
            var outputPath = Path.Combine(work, "patched.img");
            File.WriteAllBytes(bootPath, PackBootImage(kernel, UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V3, ramdisk));
            File.WriteAllBytes(packagePath, replacement);

            var result = await new BootPatchService().PatchAsync(packagePath, bootPath, outputPath,
                cancellationToken: TestContext.Current.CancellationToken);

            using var parser = new UotanStudio.Firmware.BootSecurity.BootImage.Parser.BootImageParser();
            parser.ParseFile(outputPath);
            Assert.Equal(replacement, parser.Kernel.ToArray());
            Assert.Equal(ramdisk, parser.Ramdisk.ToArray());
            var editor = new UotanStudio.Firmware.Archive.Editing.CpioArchiveEditor();
            using var ramdiskStream = new MemoryStream(parser.Ramdisk);
            editor.Load(ramdiskStream);
            Assert.Equal("stock-init"u8.ToArray(), editor.Find("init")!.Data);
            Assert.Null(editor.Find("kernelsu.ko"));
            Assert.Contains("replace boot kernel with the supplied GKI image", result.Changes);
            Assert.Equal(BootPatchKind.GkiKernel, result.Detection.Kind);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task PatchAsync_AnyKernel3Package_ReplacesTheBootKernel()
    {
        var work = CreateWorkDir("uotan-bootpatchak3-");
        try
        {
            var kernel = "\0Linux version 5.15.123-android13-5.15 (builder)\0"u8.ToArray();
            var ramdisk = BuildCpio([("init", "stock-init"u8.ToArray())]);
            var replacement = BuildArm64Image(0x300, 0x5B);
            var bootPath = Path.Combine(work, "boot.img");
            var packagePath = Path.Combine(work, "anykernel3.zip");
            var outputPath = Path.Combine(work, "patched.img");
            File.WriteAllBytes(bootPath, PackBootImage(kernel, UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V3, ramdisk));
            File.WriteAllBytes(Path.Combine(work, "image.bin"), replacement);
            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(Path.Combine(work, "image.bin"), "Image");
            }

            var result = await new BootPatchService().PatchAsync(packagePath, bootPath, outputPath,
                cancellationToken: TestContext.Current.CancellationToken);

            using var parser = new UotanStudio.Firmware.BootSecurity.BootImage.Parser.BootImageParser();
            parser.ParseFile(outputPath);
            Assert.Equal(replacement, parser.Kernel.ToArray());
            Assert.Equal(BootPatchKind.GkiKernel, result.Detection.Kind);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task PatchAsync_GkiModeOnV1Boot_ReAttachesTheOriginalKernelDtb()
    {
        var work = CreateWorkDir("uotan-bootpatchgkidtb-");
        try
        {
            var kernel = new byte[0x400];
            kernel.AsSpan().Fill(0x21);
            WriteDtb(kernel, 0x200, 0x48);
            var ramdisk = BuildCpio([("init", "stock-init"u8.ToArray())]);
            var replacement = BuildArm64Image(0x300, 0x5A);
            var bootPath = Path.Combine(work, "boot.img");
            var packagePath = Path.Combine(work, "Image");
            var outputPath = Path.Combine(work, "patched.img");
            File.WriteAllBytes(bootPath, PackBootImage(kernel, UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V1, ramdisk));
            File.WriteAllBytes(packagePath, replacement);

            var result = await new BootPatchService().PatchAsync(packagePath, bootPath, outputPath,
                cancellationToken: TestContext.Current.CancellationToken);

            using var parser = new UotanStudio.Firmware.BootSecurity.BootImage.Parser.BootImageParser();
            parser.ParseFile(outputPath);
            Assert.Equal([.. replacement, .. kernel[0x200..]], parser.Kernel.ToArray());
            Assert.Contains("re-attach the original kernel DTB", result.Changes);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task PatchAsync_KernelSuPackageWithKpimg_InjectsKernelPatchAndLeavesTheRamdiskUntouched()
    {
        var work = CreateWorkDir("uotan-bootpatchkpm-");
        try
        {
            var kernel = new byte[0x2400];
            BitConverter.GetBytes(0x2000u).CopyTo(kernel, 0x10);
            "ARM\x64"u8.CopyTo(kernel.AsSpan(0x38));
            "CONFIG_KALLSYMS=y\0"u8.CopyTo(kernel.AsSpan(0x200));
            BitConverter.GetBytes(0x14000000u).CopyTo(kernel, 0);
            var kpimg = new byte[0x1100];
            "KP1158"u8.CopyTo(kpimg);
            var ramdisk = BuildCpio([("init", "stock-init"u8.ToArray())]);
            var bootPath = Path.Combine(work, "boot.img");
            var packagePath = Path.Combine(work, "sukisu.apk");
            var outputPath = Path.Combine(work, "patched.img");
            File.WriteAllBytes(bootPath, PackBootImage(kernel, UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V3, ramdisk));
            File.WriteAllBytes(Path.Combine(work, "kpimg.bin"), kpimg);
            File.WriteAllBytes(Path.Combine(work, "ksuinit.bin"), "ksu-init"u8.ToArray());
            File.WriteAllBytes(Path.Combine(work, "lkm.bin"), "fake-kernelsu-ko"u8.ToArray());
            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(Path.Combine(work, "kpimg.bin"), "assets/kpm/kpimg");
                zip.CreateEntryFromFile(Path.Combine(work, "ksuinit.bin"), "lib/arm64-v8a/init");
                zip.CreateEntryFromFile(Path.Combine(work, "lkm.bin"), "lib/arm64-v8a/android13-5.15/kernelsu.ko");
            }

            var result = await new BootPatchService().PatchAsync(packagePath, bootPath, outputPath,
                new BootPatchOptions { Mode = BootPatchMode.Kpm, SuperPassword = "123" },
                TestContext.Current.CancellationToken);

            using var parser = new UotanStudio.Firmware.BootSecurity.BootImage.Parser.BootImageParser();
            parser.ParseFile(outputPath);
            Assert.Equal(UotanStudio.Firmware.BootSecurity.Patcher.Kernel.KernelPatchInjector.Patch(kernel, kpimg, "123"),
                parser.Kernel.ToArray());
            Assert.Equal(ramdisk, parser.Ramdisk.ToArray());
            var editor = new UotanStudio.Firmware.Archive.Editing.CpioArchiveEditor();
            using var ramdiskStream = new MemoryStream(parser.Ramdisk);
            editor.Load(ramdiskStream);
            Assert.Equal("stock-init"u8.ToArray(), editor.Find("init")!.Data);
            Assert.Null(editor.Find("kernelsu.ko"));
            Assert.Contains("inject KernelPatch kpimg into kernel", result.Changes);
            Assert.Equal(BootPatchKind.KernelSu, result.Detection.Kind);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task PatchAsync_LkmModeOnKernelPatchPackage_IsRejected()
    {
        var work = CreateWorkDir("uotan-bootpatchlkmrej-");
        try
        {
            var bootPath = Path.Combine(work, "boot.img");
            var packagePath = Path.Combine(work, "sukisu.apk");
            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                zip.CreateEntry("assets/kpimg");
                zip.CreateEntry("lib/arm64-v8a/libkptools.so");
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => new BootPatchService().PatchAsync(
                packagePath, bootPath, Path.Combine(work, "patched.img"),
                new BootPatchOptions { Mode = BootPatchMode.Lkm }, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task PatchAsync_GkiModeOnKernelSuPackage_IsRejected()
    {
        var work = CreateWorkDir("uotan-bootpatchgkirej-");
        try
        {
            var kernel = "\0Linux version 5.15.123-android13-5.15 (builder)\0"u8.ToArray();
            var ramdisk = BuildCpio([("init", "stock-init"u8.ToArray())]);
            var bootPath = Path.Combine(work, "boot.img");
            var packagePath = Path.Combine(work, "kernelsu.apk");
            File.WriteAllBytes(bootPath, PackBootImage(kernel, UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V3, ramdisk));
            File.WriteAllBytes(Path.Combine(work, "ksuinit.bin"), "ksu-init"u8.ToArray());
            File.WriteAllBytes(Path.Combine(work, "lkm.bin"), "fake-kernelsu-ko"u8.ToArray());
            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(Path.Combine(work, "ksuinit.bin"), "lib/arm64-v8a/init");
                zip.CreateEntryFromFile(Path.Combine(work, "lkm.bin"), "lib/arm64-v8a/android13-5.15/kernelsu.ko");
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => new BootPatchService().PatchAsync(
                packagePath, bootPath, Path.Combine(work, "patched.img"),
                new BootPatchOptions { Mode = BootPatchMode.Gki }, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task PatchAsync_KernelPackageWithoutKernelEntry_IsRejected()
    {
        var work = CreateWorkDir("uotan-bootpatchnokernel-");
        try
        {
            var kernel = "\0Linux version 5.15.123-android13-5.15 (builder)\0"u8.ToArray();
            var ramdisk = BuildCpio([("init", "stock-init"u8.ToArray())]);
            var bootPath = Path.Combine(work, "boot.img");
            var packagePath = Path.Combine(work, "kernel.zip");
            File.WriteAllBytes(bootPath, PackBootImage(kernel, UotanStudio.Firmware.BootSecurity.BootImage.Models.BootImageVersion.V3, ramdisk));
            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                zip.CreateEntry("README.txt");
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => new BootPatchService().PatchAsync(
                packagePath, bootPath, Path.Combine(work, "patched.img"),
                cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static byte[] BuildArm64Image(int length, byte fill = 0x21)
    {
        var image = new byte[length];
        image.AsSpan().Fill(fill);
        "ARM\x64"u8.CopyTo(image.AsSpan(0x38));
        return image;
    }

    private static string CreateWorkDir(string prefix)
    {
        var work = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

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

    private static void WriteDtb(byte[] kernel, int offset, int total)
    {
        ReadOnlySpan<byte> magic = [0xD0, 0x0D, 0xFE, 0xED];
        magic.CopyTo(kernel.AsSpan(offset));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(kernel.AsSpan(offset + 4), (uint)total);
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
}