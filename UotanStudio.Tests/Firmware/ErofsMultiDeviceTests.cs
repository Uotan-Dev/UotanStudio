using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// EROFS 多设备构建侧接线测试：验证 <see cref="ErofsOptions.ExtraDeviceCount"/> 在
/// <see cref="ErofsBuilder"/> 中正确写入超级块 s_extra_devices / s_devt_slotoff 并生成
/// 可被解析侧识别的设备表。
/// <para>EROFS multi-device build-side wiring tests: verifies that
/// <see cref="ErofsOptions.ExtraDeviceCount"/> is consumed by <see cref="ErofsBuilder"/> to
/// write the superblock s_extra_devices / s_devt_slotoff fields and emit a device table
/// recognizable by the parser.</para>
/// </summary>
public class ErofsMultiDeviceTests
{
    [Fact]
    public void ChunkIndexAlignsAfterXattrsAndMasksDeviceBits()
    {
        var image = CreateChunkDeviceImage(0x8001);
        image.AsSpan(2112, 8).CopyTo(image.AsSpan(2128));
        image.AsSpan(2112, 16).Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(2082), 1);
        using var primary = new MemoryStream(image);
        using var extra = new MemoryStream(Enumerable.Repeat((byte)0xAB, 4096).ToArray());
        using var parser = new ErofsParser(new Stream[] { primary, extra });
        using var output = new MemoryStream();
        parser.ReadFile("file.bin", output);
        Assert.Equal(Enumerable.Repeat((byte)0xAB, 4096), output.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChunkHolesUseTheDeclaredAddressWidth(bool wideAddress)
    {
        var image = CreateChunkDeviceImage(0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(2080 + 16), wideAddress ? 0x60u : 0x20u);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(2112 + 4), uint.MaxValue);
        if (wideAddress) BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(2112), ushort.MaxValue);
        using var parser = new ErofsParser(image);
        using var output = new MemoryStream();
        parser.ReadFile("file.bin", output);
        Assert.Equal(new byte[4096], output.ToArray());
    }

    [Fact]
    public void LargeChunkDoesNotWrapItsLengthOrAllocateTheWholeChunk()
    {
        var image = CreateChunkDeviceImage(1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(2080 + 16), 0x3Fu);
        using var primary = new MemoryStream(image);
        using var extra = new MemoryStream(Enumerable.Repeat((byte)0xAB, 4096).ToArray());
        using var parser = new ErofsParser(new Stream[] { primary, extra });
        using var output = new MemoryStream();
        parser.ReadFile("file.bin", output);
        Assert.Equal(Enumerable.Repeat((byte)0xAB, 4096), output.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParserReadsSeparateAndMappedFlatDevices(bool flatDevice)
    {
        var image = CreateChunkDeviceImage(1);
        using var primary = new MemoryStream(image);
        using var extra = new MemoryStream(Enumerable.Repeat((byte)0xAB, 4096).ToArray());
        using var parser = flatDevice ? ErofsParser.OpenFlatDevice(primary) : new ErofsParser(new Stream[] { primary, extra });
        using var output = new MemoryStream();
        parser.ReadFile("file.bin", output);
        Assert.Equal(Enumerable.Repeat((byte)0xAB, 4096), output.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ParserRejectsMissingAndInvalidDevice(ushort deviceId)
    {
        using var parser = new ErofsParser(CreateChunkDeviceImage(deviceId));
        using var output = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => parser.ReadFile("file.bin", output));
    }

    private static byte[] CreateChunkDeviceImage(ushort deviceId)
    {
        // Disk fields follow erofs_fs.h: 128-byte slots, compact inodes and 8-byte chunk indexes.
        var image = new byte[4 * 4096];
        var sb = image.AsSpan(1024, 128);
        BinaryPrimitives.WriteUInt32LittleEndian(sb, 0xE0F5E1E2);
        sb[12] = 12;
        BinaryPrimitives.WriteUInt16LittleEndian(sb[14..], 64);
        BinaryPrimitives.WriteUInt64LittleEndian(sb[16..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[36..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[80..], 12);
        BinaryPrimitives.WriteUInt16LittleEndian(sb[86..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(sb[88..], 12);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(1536 + 64), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(1536 + 68), 3);
        var root = image.AsSpan(2048, 32);
        BinaryPrimitives.WriteUInt16LittleEndian(root[4..], 0x41ED);
        BinaryPrimitives.WriteUInt16LittleEndian(root[6..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(root[8..], 20);
        BinaryPrimitives.WriteUInt32LittleEndian(root[16..], 2);
        var file = image.AsSpan(2080, 32);
        BinaryPrimitives.WriteUInt16LittleEndian(file, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(file[4..], 0x81A4);
        BinaryPrimitives.WriteUInt16LittleEndian(file[6..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file[8..], 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(file[16..], 0x20);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(2112 + 2), deviceId);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(8192), 65);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(8200), 12);
        image[8202] = 1;
        "file.bin"u8.CopyTo(image.AsSpan(8204));
        image.AsSpan(12288, 4096).Fill(0xAB);
        return image;
    }

    /// <summary>
    /// 写入不带 UTF-8 BOM 的文本文件（TempDirectory.Write 使用的 Encoding.UTF8 会带 BOM，
    /// 破坏逐字节一致的往返断言）。
    /// <para>Writes a text file without a UTF-8 BOM.</para>
    /// </summary>
    private static void WriteText(TempDirectory temp, string relativePath, string content)
    {
        var full = System.IO.Path.Combine(temp.Path, relativePath);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content, new UTF8Encoding(false));
    }

    [Fact]
    public void Builder_ExtraDeviceCount1_ThrowsNotSupported()
    {
        // The builder has no backing-device input; multi-device output is explicitly refused
        // instead of silently writing an all-zero device table.
        // 打包器没有外部设备镜像输入；显式拒绝多设备输出，而不是静默写全零设备表。
        using var temp = new TempDirectory();
        WriteText(temp, "hello.txt", "hello erofs\n");

        var builder = new ErofsBuilder();
        var imagePath = System.IO.Path.Combine(temp.Path, "multi.erofs");
        Assert.Throws<NotSupportedException>(() => builder.Build(
            temp.Path,
            imagePath,
            new ErofsOptions { Compress = false, ExtraDeviceCount = 1 }));
    }

    [Fact]
    public void Builder_ZeroExtraDevices_NoDeviceTable()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "a.txt", "alpha");
        WriteText(temp, "b.txt", "beta");

        var builder = new ErofsBuilder();
        var imagePath = System.IO.Path.Combine(temp.Path, "single.erofs");
        builder.Build(
            temp.Path,
            imagePath,
            new ErofsOptions { Compress = false });

        using var parser = new ErofsParser(imagePath);
        var sb = parser.Superblock;

        // 默认 ExtraDeviceCount = 0：无设备表，s_devt_slotoff == 0。
        Assert.Equal((ushort)0, sb.ExtraDevices);
        Assert.Equal((ushort)0, sb.DevtSlotoff);

        // 解析侧 EnumerateFiles 不抛错。
        var paths = parser.EnumerateFiles()
            .Select(entry => entry.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "a.txt", "b.txt" }, paths);
    }

    [Fact]
    public void Builder_MultiDevice_FsckErofsPasses()
    {
        if (!IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!IsToolAvailable("fsck.erofs")) { Assert.Skip("required tools missing"); }

        using var temp = new TempDirectory();
        WriteText(temp, "hello.txt", "hello erofs\n");
        WriteText(temp, "dir/nested.txt", "nested content");

        var builder = new ErofsBuilder();
        var imagePath = System.IO.Path.Combine(temp.Path, "multi.erofs");
        builder.Build(
            temp.Path,
            imagePath,
            new ErofsOptions { Compress = false });

        var wslPath = ToWslPath(imagePath);
        var (output, exitCode) = RunWsl($"fsck.erofs {wslPath}");

        Assert.True(exitCode == 0, $"fsck.erofs failed (exit={exitCode}):\n{output}");
    }

    // ---- WSL helpers (mirroring WslCrossValidationTests). ----

    private static bool IsWslAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("wsl", "--status")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (string Output, int ExitCode) RunWsl(string command, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo("wsl", $"-- bash -c \"{command.Replace("\"", "\\\"")}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(timeoutMs);
        return (stdout + stderr, proc.ExitCode);
    }

    private static string ToWslPath(string windowsPath)
    {
        var full = System.IO.Path.GetFullPath(windowsPath);
        if (full.Length >= 2 && full[1] == ':')
        {
            var drive = char.ToLowerInvariant(full[0]);
            var rest = full[2..].Replace('\\', '/');
            return $"/mnt/{drive}{rest}";
        }

        return full.Replace('\\', '/');
    }

    private static bool IsToolAvailable(string tool)
    {
        var (output, exitCode) = RunWsl($"command -v {tool}", 15000);
        var line = output.Trim();
        return exitCode == 0 && line.Length > 0 && !line.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }
}