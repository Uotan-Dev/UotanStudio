using System.Diagnostics;
using System.Text;
using UotanStudio.Firmware.Erofs;
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
    public void Builder_ExtraDeviceCount1_WritesDeviceTable()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "hello.txt", "hello erofs\n");
        WriteText(temp, "dir/nested.txt", "nested content");
        var payload = string.Concat(Enumerable.Repeat("0123456789abcdef", 400));
        WriteText(temp, "dir/big.txt", payload);

        var builder = new ErofsBuilder();
        var imagePath = System.IO.Path.Combine(temp.Path, "multi.erofs");
        builder.Build(
            temp.Path,
            imagePath,
            new ErofsOptions { Compress = false, ExtraDeviceCount = 1 });

        using var parser = new ErofsParser(imagePath);
        var sb = parser.Superblock;

        // s_extra_devices == 1, s_devt_slotoff > 0（块 1）。
        Assert.Equal((ushort)1, sb.ExtraDevices);
        Assert.True(sb.DevtSlotoff > 0, $"s_devt_slotoff should be > 0, got {sb.DevtSlotoff}");

        // 设备表起始字节偏移 = s_devt_slotoff * blockSize（与解析侧一致）。
        var blockSize = parser.BlockSize;
        var devtOffset = (long)sb.DevtSlotoff * blockSize;
        var imageBytes = System.IO.File.ReadAllBytes(imagePath);

        // 设备表含 ExtraDeviceCount + 1 = 2 个槽位。
        // 槽 0（主设备）：blocks == 镜像总块数，mapped_blkaddr == 0。
        var slot0 = ErofsDeviceSlot.FromBytes(
            imageBytes.AsSpan((int)devtOffset, ErofsDeviceSlot.Size));
        Assert.Equal((uint)parser.BlockCount, slot0.Blocks);
        Assert.Equal(0u, slot0.MappedBlkAddr);

        // 槽 1（额外设备占位）：blocks == 0，mapped_blkaddr == 0。
        var slot1 = ErofsDeviceSlot.FromBytes(
            imageBytes.AsSpan((int)devtOffset + ErofsDeviceSlot.Size, ErofsDeviceSlot.Size));
        Assert.Equal(0u, slot1.Blocks);
        Assert.Equal(0u, slot1.MappedBlkAddr);
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
            new ErofsOptions { Compress = false, ExtraDeviceCount = 1 });

        var wslPath = ToWslPath(imagePath);
        var (output, exitCode) = RunWsl($"fsck.erofs {wslPath}");

        // fsck.erofs 退出码：0=OK，1=需要修复，2=错误。
        // fsck.erofs exit codes: 0=OK, 1=needs repair, 2=error.
        Assert.True(exitCode <= 1, $"fsck.erofs failed (exit={exitCode}):\n{output}");
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
        var (output, _) = RunWsl($"command -v {tool}", 15000);
        var line = output.Trim();
        return line.Length > 0 && !line.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }
}
