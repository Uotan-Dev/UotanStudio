using System.IO.Compression;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Archive.Editing;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashRootImageTests
{
    [Fact]
    public async Task PrepareAsync_MagiskPackage_PatchesTemporaryImageWithoutChangingSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-magisk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? output = null;
        try
        {
            var package = Path.Combine(root, "magisk.apk");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "lib/arm64-v8a/libmagiskinit.so", "magisk-init");
                WriteEntry(zip, "lib/arm64-v8a/libmagisk.so", "magisk-runtime");
            }

            var sourceBytes = CreateBootImage([]);
            var source = Path.Combine(root, "boot.img");
            await File.WriteAllBytesAsync(source, sourceBytes, TestContext.Current.CancellationToken);

            output = await FlashRootImage.PrepareAsync(package, source, TestContext.Current.CancellationToken);

            Assert.NotEqual(source, output);
            Assert.Equal(sourceBytes, File.ReadAllBytes(source));
            using var parser = new BootImageParser();
            parser.ParseFile(output);
            var editor = new CpioArchiveEditor();
            using var patchedRamdisk = new MemoryStream(parser.Ramdisk);
            editor.Load(patchedRamdisk);
            Assert.Equal("magisk-init"u8.ToArray(), editor.Find("init")!.Data);
            Assert.NotNull(editor.Find(".backup/.magisk"));
            Assert.NotNull(editor.Find("overlay.d/sbin/magisk.xz"));
        }
        finally
        {
            if (output is not null) File.Delete(output);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_NonMagiskPackage_RejectsWithoutChangingSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-magisk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var package = Path.Combine(root, "ordinary.apk");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
                WriteEntry(zip, "assets/readme.txt", "ordinary");
            var source = Path.Combine(root, "boot.img");
            await File.WriteAllBytesAsync(source, "stock"u8.ToArray(), TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                FlashRootImage.PrepareAsync(package, source, TestContext.Current.CancellationToken));
            Assert.Equal("stock"u8.ToArray(), File.ReadAllBytes(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_KernelSuPackage_PatchesTemporaryBootAndPreservesSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-root-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? output = null;
        try
        {
            var package = Path.Combine(root, "kernelsu.apk");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "lib/arm64-v8a/init", "ksu-init");
                WriteEntry(zip, "lib/arm64-v8a/android13-5.15/kernelsu.ko", "ksu-module");
            }
            var sourceBytes = CreateBootImage("\0Linux version 5.15.123-android13-5.15 (builder)\0"u8.ToArray());
            var source = Path.Combine(root, "boot.img");
            await File.WriteAllBytesAsync(source, sourceBytes, TestContext.Current.CancellationToken);

            output = await FlashRootImage.PrepareAsync(package, source, TestContext.Current.CancellationToken);

            Assert.Equal(sourceBytes, File.ReadAllBytes(source));
            using var parser = new BootImageParser();
            parser.ParseFile(output);
            var editor = new CpioArchiveEditor();
            using var ramdisk = new MemoryStream(parser.Ramdisk);
            editor.Load(ramdisk);
            Assert.Equal("ksu-init"u8.ToArray(), editor.Find("init")!.Data);
            Assert.Equal("stock-init"u8.ToArray(), editor.Find("init.real")!.Data);
            Assert.Equal("ksu-module"u8.ToArray(), editor.Find("kernelsu.ko")!.Data);
        }
        finally
        {
            if (output is not null) File.Delete(output);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_ApatchPackageWithoutSuperKey_RejectsBeforePatching()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-root-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var package = Path.Combine(root, "apatch.apk");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
                WriteEntry(zip, "lib/arm64-v8a/libkptools.so", "apatch");
            var source = Path.Combine(root, "boot.img");
            await File.WriteAllBytesAsync(source, "stock"u8.ToArray(), TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                FlashRootImage.PrepareAsync(package, source, TestContext.Current.CancellationToken));
            Assert.Equal("stock"u8.ToArray(), File.ReadAllBytes(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_KernelSuElfModule_StagesPatchedImage()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-root-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? output = null;
        try
        {
            var module = new byte[64];
            new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' }.CopyTo(module, 0);
            module[4] = 2;
            module[5] = 1;
            BitConverter.GetBytes((ushort)183).CopyTo(module, 18);
            var package = Path.Combine(root, "kernelsu.ko");
            await File.WriteAllBytesAsync(package, module, TestContext.Current.CancellationToken);
            var source = Path.Combine(root, "boot.img");
            var sourceBytes = CreateBootImage([]);
            await File.WriteAllBytesAsync(source, sourceBytes, TestContext.Current.CancellationToken);

            output = await FlashRootImage.PrepareAsync(package, source, TestContext.Current.CancellationToken);

            Assert.Equal(sourceBytes, File.ReadAllBytes(source));
            using var parser = new BootImageParser();
            parser.ParseFile(output);
            var editor = new CpioArchiveEditor();
            using var ramdisk = new MemoryStream(parser.Ramdisk);
            editor.Load(ramdisk);
            Assert.Equal(module, editor.Find("kernelsu.ko")!.Data);
        }
        finally
        {
            if (output is not null) File.Delete(output);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_UnknownArchitectureModule_RejectsBeforePatching()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-root-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var package = Path.Combine(root, "not-a-module.ko");
            await File.WriteAllBytesAsync(package, "not ELF"u8.ToArray(), TestContext.Current.CancellationToken);
            var source = Path.Combine(root, "boot.img");
            await File.WriteAllBytesAsync(source, "stock"u8.ToArray(), TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                FlashRootImage.PrepareAsync(package, source, TestContext.Current.CancellationToken));
            Assert.Equal("stock"u8.ToArray(), File.ReadAllBytes(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateBootImage(byte[] kernel)
    {
        var archive = new CpioArchive();
        using var ramdisk = new MemoryStream();
        archive.SaveAll(ramdisk, [new ArchiveEntry
        {
            Name = "init",
            Data = "stock-init"u8.ToArray(),
            Metadata = new ArchiveEntryMetadata
            {
                FileType = CpioFileType.Regular,
                UnixPermissions = 0x81ED,
                LinkCount = 1
            }
        }], ArchiveFormat.NewAscii);
        var packer = new BootImagePacker
        {
            Header = new BootImageHeader { HeaderVersion = BootImageVersion.V3, HeaderSize = 1580 },
            Kernel = kernel,
            Ramdisk = ramdisk.ToArray()
        };
        using var boot = new MemoryStream();
        packer.PackToStream(boot);
        return boot.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string value)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(value);
    }
}