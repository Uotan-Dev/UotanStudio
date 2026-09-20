using System.Buffers.Binary;
using System.Diagnostics;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks the kernel system.data layout. <para>检查内核 system.data 布局。</para></summary>
public class Ext4InlineDataTests
{
    [Theory]
    [InlineData("user.test", 1, "test")]
    [InlineData("system.posix_acl_access", 2, "")]
    public void WriterUsesKernelIbodyXattrFields(string fullName, byte expectedIndex, string shortName)
    {
        var raw = new byte[256];
        var value = new byte[] { 1, 2, 3, 4, 5 };
        Ext4Builder.WriteInodeXattrs(raw, 256, new Dictionary<string, byte[]> { [fullName] = value });
        Assert.Equal(0xEA020000u, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(156)));
        var entry = raw.AsSpan(160);
        Assert.Equal((byte)shortName.Length, entry[0]);
        Assert.Equal(expectedIndex, entry[1]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]));
        Assert.Equal(shortName, System.Text.Encoding.UTF8.GetString(entry.Slice(16, shortName.Length)));
        Assert.Equal(value, raw.AsSpan(160 + BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]), 5).ToArray());
    }

    [Fact]
    public void ExtractionFailurePreservesTheExistingFile()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        File.WriteAllBytes(Path.Combine(source.Path, "file.bin"), new byte[8192]);
        using var image = new MemoryStream(Ext4Builder.Build(source.Path));
        using var parser = new Ext4Parser(image);
        parser.ListFiles();
        image.SetLength(4096);
        var existing = Path.Combine(target.Path, "file.bin");
        File.WriteAllText(existing, "existing");
        Assert.ThrowsAny<Exception>(() => parser.ExtractToDirectory(target.Path));
        Assert.Equal("existing", File.ReadAllText(existing));
        Assert.Single(Directory.EnumerateFiles(target.Path));
    }

    [Fact]
    public void ParserReadsNativeInlineFileAndDirectory()
    {
        if (RunWsl("command -v mke2fs").ExitCode != 0) Assert.Skip("WSL mke2fs is unavailable.");
        using var source = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(source.Path, "nested"));
        var expected = Enumerable.Range(0, 100).Select(i => (byte)(i + 1)).ToArray();
        File.WriteAllBytes(Path.Combine(source.Path, "nested", "inline.bin"), expected);
        var image = Path.Combine(Path.GetTempPath(), $"uotan-ext4-inline-{Guid.NewGuid():N}.img");
        try
        {
            using (var imageStream = File.Create(image)) imageStream.SetLength(16 * 1024 * 1024);
            var mkfs = RunWsl($"mke2fs -q -F -t ext4 -O inline_data -d {WslPath(source.Path)} {WslPath(image)}");
            Assert.True(mkfs.ExitCode == 0, mkfs.Output);
            using var parser = new Ext4Parser(image);
            using var output = new MemoryStream();
            parser.ReadFile("nested/inline.bin", output);
            Assert.Equal(expected, output.ToArray());
            var entry = parser.ListFiles().Single(e => e.Path == "nested/inline.bin");
            Assert.NotNull(entry.Xattrs);
            Assert.True(entry.Xattrs.ContainsKey("system.data"));
        }
        finally { File.Delete(image); }
    }

    [Fact]
    public void InlineFileReadsSystemDataValueRatherThanExtraFields()
    {
        var raw = CreateInlineInode();
        var inode = Ext4Inode.FromBytes(raw, 256);
        Assert.Equal(Enumerable.Range(0, 100).Select(x => (byte)x), inode.InlineData);
    }

    [Fact]
    public void InlineFileRejectsMissingOrOutOfRangeSystemData()
    {
        var missing = CreateInlineInode();
        missing[165] = 1;
        Assert.Throws<InvalidDataException>(() => Ext4Inode.FromBytes(missing, 256));
        var truncated = CreateInlineInode();
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(172), 1000);
        Assert.Throws<InvalidDataException>(() => Ext4Inode.FromBytes(truncated, 256));
    }

    private static byte[] CreateInlineInode()
    {
        var raw = new byte[256];
        BinaryPrimitives.WriteUInt16LittleEndian(raw, 0x81A4);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(32), 0x10000000);
        for (var i = 0; i < 60; i++) raw[40 + i] = (byte)i;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(128), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(160), 0xEA020000);
        raw[164] = 4;
        raw[165] = 7;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(166), 52);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(172), 40);
        "data"u8.CopyTo(raw.AsSpan(180));
        for (var i = 0; i < 40; i++) raw[216 + i] = (byte)(60 + i);
        return raw;
    }

    private static string WslPath(string path) => "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');

    private static (int ExitCode, string Output) RunWsl(string command)
    {
        try
        {
            var start = new ProcessStartInfo("wsl", $"-- bash -c \"{command.Replace("\"", "\\\"")}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output + error);
        }
        catch (System.ComponentModel.Win32Exception) { return (-1, "WSL unavailable"); }
    }
}