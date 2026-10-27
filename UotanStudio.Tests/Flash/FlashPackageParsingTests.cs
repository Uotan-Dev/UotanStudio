using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

public sealed class FlashPackageParsingTests
{
    [Fact]
    public void FormatByteSize_ReturnsExpectedUnit()
    {
        Assert.Equal(string.Empty, FlashViewModel.FormatByteSize(0));
        Assert.Equal("512 B", FlashViewModel.FormatByteSize(512));
        Assert.Equal("2.0 KiB", FlashViewModel.FormatByteSize(2048));
        Assert.Equal("10.0 MiB", FlashViewModel.FormatByteSize(10 * 1024 * 1024));
        Assert.Equal("2.50 GiB", FlashViewModel.FormatByteSize((long)(2.5 * 1024 * 1024 * 1024)));
    }

    [Fact]
    public void ParseScript_BatchScriptWithNonFastbootLines_ParsesOnlyFastbootCommands()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-script-" + Guid.NewGuid().ToString("N"));
        var imagesDir = Path.Combine(tempDir, "images");
        Directory.CreateDirectory(imagesDir);
        try
        {
            var bootImg = Path.Combine(imagesDir, "boot.img");
            File.WriteAllBytes(bootImg, new byte[1024]);
            var superEmptyImg = Path.Combine(imagesDir, "super_empty.img");
            File.WriteAllBytes(superEmptyImg, new byte[512]);

            var scriptPath = Path.Combine(tempDir, "flash_all.bat");
            var lines = new[]
            {
                "@echo off",
                "echo Flashing images...",
                "fastboot %* erase metadata",
                "@fastboot %* flash boot \"%~dp0images\\boot.img\"",
                "fastboot %* wipe-super \"%~dp0images\\super_empty.img\"",
                "fastboot %* set_active a",
                "pause",
                "fastboot %* reboot"
            };

            var rows = FlashViewModel.ParseScript(scriptPath, lines);

            Assert.Equal(5, rows.Count);

            Assert.Equal("erase", rows[0].Command);
            Assert.Equal("metadata", rows[0].Name);

            Assert.Equal("flash", rows[1].Command);
            Assert.Equal("boot", rows[1].Name);
            Assert.Equal("boot.img", rows[1].FileName);
            Assert.Equal(bootImg, rows[1].FilePath);
            Assert.Equal("1.0 KiB", rows[1].Size);

            Assert.Equal("wipe_super", rows[2].Command);
            Assert.Equal("super_empty", rows[2].Name);
            Assert.Equal("super_empty.img", rows[2].FileName);

            Assert.Equal("set_active", rows[3].Command);
            Assert.Equal("a", rows[3].Name);

            Assert.Equal("reboot", rows[4].Command);
            Assert.Equal("reboot", rows[4].Name);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ParseScript_ShellScript_NormalizesDirnameVariables()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-sh-" + Guid.NewGuid().ToString("N"));
        var imagesDir = Path.Combine(tempDir, "images");
        Directory.CreateDirectory(imagesDir);
        try
        {
            var bootImg = Path.Combine(imagesDir, "boot.img");
            File.WriteAllBytes(bootImg, new byte[2048]);

            var scriptPath = Path.Combine(tempDir, "flash_all.sh");
            var lines = new[]
            {
                "#!/bin/bash",
                "echo \"Starting flash\"",
                "fastboot $* flash boot `dirname $0`/images/boot.img",
                "fastboot delete-logical-partition product_a",
                "fastboot create-logical-partition product_a 0"
            };

            var rows = FlashViewModel.ParseScript(scriptPath, lines);

            Assert.Equal(3, rows.Count);
            Assert.Equal("flash", rows[0].Command);
            Assert.Equal("boot", rows[0].Name);
            Assert.Equal(bootImg, rows[0].FilePath);

            Assert.Equal("delete", rows[1].Command);
            Assert.Equal("product_a", rows[1].Name);

            Assert.Equal("create", rows[2].Command);
            Assert.Equal("product_a", rows[2].Name);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ParseManifestText_ValidTextWithHeadersAndCommands_ExtractsRowsAndMetadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-txt-" + Guid.NewGuid().ToString("N"));
        var imagesDir = Path.Combine(tempDir, "images");
        Directory.CreateDirectory(imagesDir);
        try
        {
            var bootImg = Path.Combine(imagesDir, "boot.img");
            File.WriteAllBytes(bootImg, new byte[4096]);

            var txtPath = Path.Combine(tempDir, "flash_fastbootd.txt");
            var lines = new[]
            {
                "# Comments should be ignored",
                "codename: raphael",
                "mode:fastbootd",
                "",
                "system delete",
                "system create",
                "boot",
                "vendor_boot images/vendor_boot.img"
            };

            var rows = FlashViewModel.ParseManifestText(txtPath, lines, out var codeName, out var requiresFastbootd);

            Assert.Equal("raphael", codeName);
            Assert.True(requiresFastbootd);
            Assert.Equal(4, rows.Count);

            Assert.Equal("delete", rows[0].Command);
            Assert.Equal("system", rows[0].Name);

            Assert.Equal("create", rows[1].Command);
            Assert.Equal("system", rows[1].Name);

            Assert.Equal("flash", rows[2].Command);
            Assert.Equal("boot", rows[2].Name);
            Assert.Equal("boot.img", rows[2].FileName);
            Assert.Equal(bootImg, rows[2].FilePath);
            Assert.Equal("4.0 KiB", rows[2].Size);

            Assert.Equal("flash", rows[3].Command);
            Assert.Equal("vendor_boot", rows[3].Name);
            Assert.Equal("vendor_boot.img", rows[3].FileName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ParseDirectory_DirectoryWithImagesSubdir_EnumeratesImagesExcludingScripts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-dir-" + Guid.NewGuid().ToString("N"));
        var imagesDir = Path.Combine(tempDir, "images");
        Directory.CreateDirectory(imagesDir);
        try
        {
            File.WriteAllBytes(Path.Combine(imagesDir, "boot.img"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(imagesDir, "super_empty.img"), new byte[512]);
            File.WriteAllBytes(Path.Combine(imagesDir, "flash.bat"), new byte[100]); // should be skipped

            var rows = FlashViewModel.ParseDirectory(tempDir);

            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.Name == "boot" && r.Command == "flash");
            Assert.Contains(rows, r => r.Name == "super_empty" && r.Command == "wipe_super");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
