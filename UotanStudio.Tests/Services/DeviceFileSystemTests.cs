using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class DeviceFileSystemTests
{
    [Fact]
    public void EscapeShellArgument_WrapsAndEscapesSingleQuotes()
    {
        var result = DeviceFileSystemService.EscapeShellArgument("/sdcard/my dir/it's a file.txt");
        Assert.Equal("'/sdcard/my dir/it'\\''s a file.txt'", result);
    }

    [Theory]
    [InlineData("/sdcard/Download", "/sdcard")]
    [InlineData("/sdcard", "/")]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    [InlineData("/storage/emulated/0", "/storage/emulated")]
    public void GetParentPath_ReturnsExpectedParent(string input, string expected)
    {
        var parent = DeviceFileSystemService.GetParentPath(input);
        Assert.Equal(expected, parent);
    }

    [Theory]
    [InlineData("/sdcard", "Download", "/sdcard/Download")]
    [InlineData("/sdcard/", "Download", "/sdcard/Download")]
    [InlineData("/", "system", "/system")]
    [InlineData("", "data", "/data")]
    public void CombineRemotePath_CombinesCorrectly(string basePath, string sub, string expected)
    {
        var combined = DeviceFileSystemService.CombineRemotePath(basePath, sub);
        Assert.Equal(expected, combined);
    }

    [Fact]
    public void ParseLsOutput_ParsesDirectoriesFilesAndSymlinks()
    {
        var output = """
            total 24
            drwxrwxr-x  2 root sdcard_rw 4096 2024-01-01 12:00 Download
            -rw-rw-r--  1 root sdcard_rw  123 2024-01-02 13:45 notes with space.txt
            lrwxrwxrwx  1 root root         21 2024-01-01 00:00 sdcard -> /storage/emulated/0
            drwxr-xr-x  2 root root      4096 2024-01-01 12:00 .
            drwxr-xr-x 10 root root      4096 2024-01-01 12:00 ..
            """;

        var entries = DeviceFileSystemService.ParseLsOutput(output, "/storage/emulated/0");

        Assert.Equal(3, entries.Count);

        // Directories come first
        Assert.True(entries[0].IsDirectory);
        Assert.Equal("Download", entries[0].Name);
        Assert.Equal("/storage/emulated/0/Download", entries[0].FullPath);
        Assert.Equal(-1, entries[0].Size);

        // Then files / symlinks
        var notesFile = Assert.Single(entries, e => e.Name == "notes with space.txt");
        Assert.False(notesFile.IsDirectory);
        Assert.Equal(123, notesFile.Size);
        Assert.Equal("/storage/emulated/0/notes with space.txt", notesFile.FullPath);

        var symlink = Assert.Single(entries, e => e.Name == "sdcard");
        Assert.True(symlink.IsSymlink);
        Assert.Equal("/storage/emulated/0", symlink.LinkTarget);
    }
}