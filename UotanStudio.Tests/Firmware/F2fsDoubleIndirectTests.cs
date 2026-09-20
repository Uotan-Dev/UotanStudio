using System.Buffers.Binary;
using System.Diagnostics;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks the Linux 2/2/1 node boundaries using a sparse source file. <para>使用稀疏源文件检查 Linux 2/2/1 节点边界。</para></summary>
public class F2fsDoubleIndirectTests
{
    [Fact]
    public void BuilderWritesBothIndirectRootsAndTheDoubleIndirectRoot()
    {
        using var source = new TempDirectory();
        using var target = new TempDirectory();
        const uint indirectCapacity = 1018 * 1018;
        uint[] logicalBlocks = [922, 923, 1940, 1941, 2958, 2959, 2959 + indirectCapacity - 1, 2959 + indirectCapacity, 2959 + 2 * indirectCapacity - 1, 2959 + 2 * indirectCapacity];
        var input = Path.Combine(source.Path, "sparse.bin");
        var imagePath = Path.Combine(target.Path, "f2fs.img");
        using (var file = File.Create(input))
        {
            file.SetLength(((long)logicalBlocks[^1] + 1) * 4096);
            var block = new byte[4096];
            foreach (var logical in logicalBlocks)
            {
                block.AsSpan().Fill((byte)(logical % 251 + 1));
                BinaryPrimitives.WriteUInt32LittleEndian(block, logical);
                file.Position = (long)logical * 4096;
                file.Write(block);
            }
        }
        F2fsBuilder.Build(source.Path, imagePath, new F2fsOptions { EnableSparse = true });
        using var parser = new F2fsParser(imagePath);
        var entry = Assert.Single(parser.ListFiles());
        using var image = File.OpenRead(imagePath);
        var bytes = new byte[4096];
        image.Position = 1024;
        image.ReadExactly(bytes.AsSpan(0, F2fsSuperblock.SuperblockSize));
        var sb = F2fsSuperblock.FromBytes(bytes);
        image.Position = (long)sb.NatBlkaddr * 4096 + entry.Inode * 9 + 5;
        image.ReadExactly(bytes.AsSpan(0, 4));
        var inodeBlock = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        image.Position = (long)inodeBlock * 4096;
        image.ReadExactly(bytes);
        var inode = F2fsInode.FromBytes(bytes);
        Assert.All(inode.Nid, nid => Assert.NotEqual(0u, nid));
        foreach (var logical in logicalBlocks)
        {
            var physical = parser.MapBlock(inode, logical);
            Assert.NotNull(physical);
            image.Position = physical.Value * 4096;
            image.ReadExactly(bytes);
            Assert.Equal(logical, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
            Assert.All(bytes.Skip(4), value => Assert.Equal((byte)(logical % 251 + 1), value));
        }
        Assert.Null(parser.MapBlock(inode, logicalBlocks[^1] - 5));
        image.Dispose();
        parser.Dispose();
        // Native fsck runs the real kernel tool; keep it opt-in so the default suite stays bounded.
        // 原生 fsck 会运行真实内核工具；保持显式启用，确保默认测试集有界。
        var native = WslTestHelpers.IsInteropEnabled() ? CheckNative(imagePath) : null;
        if (native is not null) Assert.True(native.Value.ExitCode == 0 && !native.Value.Output.Contains("[ASSERT]") && !native.Value.Output.Contains("[Fail]"), native.Value.Output);
    }

    private static (int ExitCode, string Output)? CheckNative(string image)
    {
        var path = "/mnt/" + char.ToLowerInvariant(image[0]) + image[2..].Replace('\\', '/');
        try
        {
            var start = new ProcessStartInfo("wsl", $"-- fsck.f2fs --dry-run {path}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 127 ? null : (process.ExitCode, output + error);
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}