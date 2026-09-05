using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Erofs;
using UotanStudio.Firmware.Ext4;
using UotanStudio.Firmware.F2fs;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// 临时探针：构建高风险场景镜像并在 Alpine WSL2 中跑 fsck，输出全部落盘到 .fsck-probe/。
/// <para>Temporary probe: builds risky-scenario images, runs fsck in Alpine WSL2, dumps all
/// output into .fsck-probe/.</para>
/// </summary>
public class WslFsckProbe
{
    private static readonly string LogDir =
        Path.Combine("D:", "Source Code", "UotanStudio", ".fsck-probe");

    private static void Log(string name, string content)
    {
        Directory.CreateDirectory(LogDir);
        File.WriteAllText(Path.Combine(LogDir, name + ".log"), content);
    }

    private static void LogBytes(string name, byte[] bytes)
    {
        Directory.CreateDirectory(LogDir);
        File.WriteAllBytes(Path.Combine(LogDir, name + ".bin"), bytes);
    }

    private static (string Output, int ExitCode) Wsl(string command, int timeoutMs = 120000)
        => WslTestHelpers.RunWsl(command, timeoutMs);

    private static (bool Available, string Output) TryWsl(string command)
    {
        try
        {
            var (output, exit) = Wsl(command, 60000);
            return (exit == 0, output);
        }
        catch
        {
            return (false, "wsl invocation failed");
        }
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    // ---------------------------------------------------------------- EXT4

    [Fact]
    public void Probe_Ext4()
    {
        var ok = TryWsl("e2fsck -V 2>&1 | head -2");
        if (!ok.Available) { Log("ext4-env", "e2fsck unavailable:\n" + ok.Output); return; }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            // 场景 1：多块组（>128MB 数据 → ≥2 组），含备份超级块路径。
            var multi = Path.Combine(root, "multi");
            Directory.CreateDirectory(multi);
            for (var i = 0; i < 10; i++)
            {
                File.WriteAllBytes(Path.Combine(multi, $"big{i:00}.bin"), RandomBytes(24 * 1024 * 1024, 100 + i));
            }

            File.WriteAllText(Path.Combine(multi, "small.txt"), "multi group probe");
            Directory.CreateDirectory(Path.Combine(multi, "dir"));
            File.WriteAllText(Path.Combine(multi, "dir", "n.txt"), "nested");

            var img1 = BuildExt4(multi, "multi", new Ext4Options());
            RunFsck("ext4-multi-group", "e2fsck -fn", img1, keepImage: true);
            RunFsck("ext4-multi-group-dump", "debugfs -R 'ls -l /' ", img1);

            // 场景 2：htree 大目录（2000 项 + 深层嵌套）。
            var bigDir = Path.Combine(root, "htree");
            var d1 = Path.Combine(bigDir, "many");
            Directory.CreateDirectory(d1);
            for (var i = 0; i < 2000; i++)
            {
                File.WriteAllText(Path.Combine(d1, $"file_{i:D5}.dat"), $"content {i}\n");
            }

            var deep = Path.Combine(bigDir, "deep");
            for (var i = 0; i < 25; i++)
            {
                deep = Path.Combine(deep, $"level{i:D2}");
            }

            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(deep, "bottom.txt"), "deep");
            Directory.CreateDirectory(Path.Combine(bigDir, "many2"));
            for (var i = 0; i < 300; i++)
            {
                File.WriteAllText(Path.Combine(bigDir, "many2", $"x{i:D4}"), "y");
            }

            var img2 = BuildExt4(bigDir, "htree", new Ext4Options());
            RunFsck("ext4-htree", "e2fsck -fn", img2);
            RunFsck("ext4-htree-ls", "debugfs -R 'ls -l /many' ", img2);

            // 场景 3：无日志 + 无 metadata_csum。
            var img3 = BuildExt4(bigDir, "htree-nocsum", new Ext4Options { IncludeJournal = false, EnableMetadataCsum = false });
            RunFsck("ext4-nocsum", "e2fsck -fn", img3);

            // 场景 4：参考镜像（mke2fs -t ext4 -b 4096）。
            var refDir = Path.Combine(root, "refext4");
            Directory.CreateDirectory(refDir);
            File.WriteAllText(Path.Combine(refDir, "a.txt"), "reference");
            var img4 = Path.Combine(Path.GetTempPath(), "probe-ext4-ref.img");
            var wslRef = WslTestHelpers.ToWslPath(img4);
            var (mkOut, mkExit) = Wsl($"mke2fs -q -t ext4 -b 4096 -F {wslRef} 32M 2>&1 && echo MKFS_OK");
            Log("ext4-ref-mkfs", $"exit={mkExit}\n{mkOut}");
            if (mkExit == 0)
            {
                RunFsck("ext4-ref", "e2fsck -fn", img4);
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    // ---------------------------------------------------------------- EROFS

    [Fact]
    public void Probe_Erofs()
    {
        var ok = TryWsl("fsck.erofs -V 2>&1 | head -2");
        if (!ok.Available) { Log("erofs-env", "fsck.erofs unavailable:\n" + ok.Output); return; }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "hello.txt"), "hello erofs\n");
            File.WriteAllBytes(Path.Combine(src, "comp.bin"), Encoding.ASCII.GetBytes(new string('a', 70000) + "tail"));
            File.WriteAllBytes(Path.Combine(src, "rand.bin"), RandomBytes(50000, 7));
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "s.txt"), "sub");

            var cases = new (string Name, ErofsOptions Options)[]
            {
                ("default", new ErofsOptions()),
                ("nocompress", new ErofsOptions { Compress = false }),
                ("fragdedup", new ErofsOptions { Fragments = true, Dedup = true }),
                ("compact", new ErofsOptions { UseCompactedIndexes = true }),
            };
            foreach (var (name, options) in cases)
            {
                var img = BuildErofs(src, name, options);
                RunFsck("erofs-" + name, "fsck.erofs", img);
            }

            // 大目录（3000 项 → 多块目录）。
            var bigDir = Path.Combine(root, "bigdir");
            Directory.CreateDirectory(bigDir);
            for (var i = 0; i < 3000; i++)
            {
                File.WriteAllText(Path.Combine(bigDir, $"entry_{i:D5}"), $"v{i}");
            }

            var imgBig = BuildErofs(bigDir, "bigdir", new ErofsOptions());
            RunFsck("erofs-bigdir", "fsck.erofs", imgBig);

            // 上游参考镜像 + 超级块校验和算法对照。
            var refImg = Path.Combine(Path.GetTempPath(), "probe-erofs-ref.img");
            var wslRef = WslTestHelpers.ToWslPath(refImg);
            var (mkOut, mkExit) = Wsl($"rm -f {wslRef} && mkfs.erofs -z lz4 {wslRef} {WslTestHelpers.ToWslPath(src)} 2>&1 && echo MKFS_OK");
            Log("erofs-ref-mkfs", $"exit={mkExit}\n{mkOut}");
            if (mkExit == 0 && File.Exists(refImg))
            {
                RunFsck("erofs-ref", "fsck.erofs", refImg, keepImage: true);

                var sb = new byte[1024];
                using (var fs = File.OpenRead(refImg))
                {
                    var read = 0;
                    while (read < sb.Length)
                    {
                        var n = fs.Read(sb, read, sb.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }

                var stored = sb[4..8];
                var sbZeroed = (byte[])sb.Clone();
                Array.Clear(sbZeroed, 4, 4);
                var ieee = System.IO.Hashing.Crc32.Hash(sbZeroed);
                var crc32c = ComputeCrc32C(sbZeroed);
                // 额外覆盖候选：全 1024 字节（字段清零）、[8..128)、[8..1024)。
                var sb1024Zeroed = (byte[])sb.Clone();
                Array.Clear(sb1024Zeroed, 4, 4);
                var ieee1024 = System.IO.Hashing.Crc32.Hash(sb1024Zeroed);
                var crc32c1024 = ComputeCrc32C(sb1024Zeroed);
                var ieee8_128 = System.IO.Hashing.Crc32.Hash(sb.AsSpan(8, 120).ToArray());
                var crc32c8_128 = ComputeCrc32C(sb.AsSpan(8, 120));
                Log(
                    "erofs-ref-checksum",
                    $"stored={Convert.ToHexString(stored)}\n" +
                    $"ieee128={Convert.ToHexString(ieee)} crc32c128={Convert.ToHexString(crc32c)}\n" +
                    $"ieee1024={Convert.ToHexString(ieee1024)} crc32c1024={Convert.ToHexString(crc32c1024)}\n" +
                    $"ieee8_128={Convert.ToHexString(ieee8_128)} crc32c8_128={Convert.ToHexString(crc32c8_128)}");
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    // ---------------------------------------------------------------- F2FS

    [Fact]
    public void Probe_F2fs()
    {
        var ok = TryWsl("fsck.f2fs -V 2>&1 | head -2");
        if (!ok.Available) { Log("f2fs-env", "fsck.f2fs unavailable:\n" + ok.Output); return; }

        var root = WslTestHelpers.CreateTempDirectory();
        try
        {
            // 场景 1：16MB 随机文件（跨过 923+2×1018 直接区进入 indirect）+ 常规树。
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            File.WriteAllBytes(Path.Combine(src, "large.bin"), RandomBytes(16 * 1024 * 1024, 11));
            File.WriteAllText(Path.Combine(src, "hello.txt"), "hello f2fs\n");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "inner.txt"), "inner");

            var img1 = BuildF2fs(src, "large", new F2fsOptions());
            RunFsck("f2fs-large", "fsck.f2fs", img1);

            // 场景 2：压缩（可压 + 不可压混合）。
            var comp = Path.Combine(root, "comp");
            Directory.CreateDirectory(comp);
            File.WriteAllBytes(Path.Combine(comp, "compressible.bin"), Encoding.ASCII.GetBytes(new string('z', 200000)));
            File.WriteAllBytes(Path.Combine(comp, "incompressible.bin"), RandomBytes(200000, 5));
            File.WriteAllText(Path.Combine(comp, "tiny.txt"), "tiny");
            var img2 = BuildF2fs(comp, "comp", new F2fsOptions { EnableCompression = true });
            RunFsck("f2fs-compress", "fsck.f2fs", img2);

            // 场景 3：多块目录（1000 项）。
            var many = Path.Combine(root, "many");
            Directory.CreateDirectory(many);
            for (var i = 0; i < 1000; i++)
            {
                File.WriteAllText(Path.Combine(many, $"item_{i:D4}.txt"), $"payload {i}");
            }

            var img3 = BuildF2fs(many, "many", new F2fsOptions());
            RunFsck("f2fs-manydir", "fsck.f2fs", img3);

            // 场景 4：稀疏关闭 + 深目录。
            var deep = Path.Combine(root, "deep");
            var d = deep;
            for (var i = 0; i < 20; i++)
            {
                d = Path.Combine(d, $"L{i:D2}");
            }

            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "leaf.bin"), "leaf");
            var img4 = BuildF2fs(deep, "deep", new F2fsOptions { EnableSparse = false });
            RunFsck("f2fs-deep", "fsck.f2fs", img4);

            // 场景 5：上游参考镜像（32MB = 65536 扇区）。
            var refImg = Path.Combine(Path.GetTempPath(), "probe-f2fs-ref.img");
            var wslRef = WslTestHelpers.ToWslPath(refImg);
            var (mkOut, mkExit) = Wsl($"rm -f {wslRef} && mkfs.f2fs -f {wslRef} 65536 2>&1; echo EXIT=$?");
            Log("f2fs-ref-mkfs", $"exit={mkExit}\n{mkOut}");
            if (File.Exists(refImg))
            {
                RunFsck("f2fs-ref", "fsck.f2fs", refImg, keepImage: true);
            }
        }
        finally
        {
            WslTestHelpers.DeleteRecursive(root);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string BuildExt4(string src, string name, Ext4Options options)
    {
        var path = Path.Combine(Path.GetTempPath(), $"probe-ext4-{name}.img");
        Ext4Builder.Build(src, path, options);
        return path;
    }

    private static string BuildErofs(string src, string name, ErofsOptions options)
    {
        var path = Path.Combine(Path.GetTempPath(), $"probe-erofs-{name}.img");
        new ErofsBuilder().Build(src, path, options);
        return path;
    }

    private static string BuildF2fs(string src, string name, F2fsOptions options)
    {
        var path = Path.Combine(Path.GetTempPath(), $"probe-f2fs-{name}.img");
        F2fsBuilder.Build(src, path, options);
        return path;
    }

    private static void RunFsck(string name, string tool, string imagePath, bool keepImage = false)
    {
        var wslPath = WslTestHelpers.ToWslPath(imagePath);
        var (output, exitCode) = Wsl($"{tool} {wslPath} 2>&1", 300000);
        Log(name, $"exit={exitCode}\n{output}");
        if (!keepImage)
        {
            try { File.Delete(imagePath); } catch { }
        }
    }

    private static byte[] ComputeCrc32C(byte[] data) => ComputeCrc32C(data.AsSpan());

    private static byte[] ComputeCrc32C(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[4];
        Crc32C.Hash(data, hash);
        return hash.ToArray();
    }

    /// <summary>CRC32C（Castagnoli，poly 0x1EDC6F41 反射 0x82F63B78），crc32c_le 约定（init ~0，无终 XOR）。</summary>
    private static class Crc32C
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            const uint poly = 0x82F63B78u;
            for (uint i = 0; i < 256; i++)
            {
                var crc = i;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
                }

                table[i] = crc;
            }

            return table;
        }

        public static void Hash(ReadOnlySpan<byte> data, Span<byte> destination)
        {
            uint crc = 0xFFFF_FFFF;
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(destination, crc);
        }
    }
}
