using System.Diagnostics;
using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// EROFS fragments（packed inode 尾部碎片聚合）与整文件去重的打包 → 解析回环测试。
/// 覆盖：Fragments=true 时小文件汇入 packed inode、大文件余数仍正确读回；Dedup=true 时
/// 相同文件共享块区（数据区更小）；两者同开时的读回一致；WSL 可用时用 fsck.erofs /
/// dump.erofs 交叉验证。
/// <para>EROFS fragments (tail fragments merged into a packed inode) and whole-file dedupe
/// build → parse round-trip tests: sub-block files merged into the packed inode under
/// Fragments=true (larger files still read back), identical files sharing block ranges under
/// Dedup=true (smaller data area), both enabled together, and fsck.erofs / dump.erofs
/// cross-validation when WSL is available.</para>
/// </summary>
public sealed class ErofsFragmentTests
{
    private const int BlockSize = 4096;

    private static void WriteBytes(TempDirectory temp, string relativePath, byte[] content)
    {
        var full = Path.Combine(temp.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    private static byte[] DeterministicBytes(int size, int seed)
    {
        var rng = new Random(seed);
        var bytes = new byte[size];
        rng.NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// 构建镜像到系统临时目录（而非源目录内），避免后续构建把先前镜像当作输入文件打包进去。
    /// <para>Builds the image into the OS temp directory (not inside the source tree) so later
    /// builds never pack an earlier image in as an input file.</para>
    /// </summary>
    private static string BuildImage(TempDirectory source, ErofsOptions options)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-erofs-frag-{Guid.NewGuid():N}.img");
        new ErofsBuilder().Build(source.Path, path, options);
        return path;
    }

    private static void AssertAllFilesRoundTrip(string imagePath, TempDirectory source)
    {
        using var parser = new ErofsParser(imagePath);
        var entries = parser.EnumerateFiles().Where(e => !e.IsSymlink).ToList();
        Assert.NotEmpty(entries);
        var failures = new List<string>();
        foreach (var entry in entries)
        {
            var sourceBytes = File.ReadAllBytes(Path.Combine(source.Path, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            var readBack = parser.ReadFileBytes(entry.Path);
            if (!sourceBytes.AsSpan().SequenceEqual(readBack))
            {
                var a = Convert.ToHexString(SHA256.HashData(sourceBytes));
                var b = Convert.ToHexString(SHA256.HashData(readBack));
                failures.Add($"{entry.Path}: source sha256 {a} != read-back {b} (sizes {sourceBytes.Length}/{readBack.Length})");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Builder_FragmentsDefault_False()
    {
        var options = new ErofsOptions();
        Assert.False(options.Fragments);
        Assert.False(options.Dedup);
        Assert.True(options.TailPacking);
    }

    [Fact]
    public void Builder_FragmentsEnabled_ReadBackFiles()
    {
        using var temp = new TempDirectory();
        // 多个不足一块的小文件（碎片汇入 packed inode）。
        // Several sub-block files (merged into the packed inode).
        WriteBytes(temp, "tiny-a.bin", DeterministicBytes(97, 1));
        WriteBytes(temp, "tiny-b.bin", DeterministicBytes(1234, 2));
        WriteBytes(temp, "tiny-c.bin", DeterministicBytes(3950, 3));
        // 非块对齐的较大文件（余数向上对齐，读回一致）。
        // Larger non-block-aligned file (remainder rounded up, must read back intact).
        WriteBytes(temp, "large-tail.bin", DeterministicBytes(100_000, 4));
        // 恰好整块的文件。Exactly block-sized file.
        WriteBytes(temp, "exact.bin", DeterministicBytes(BlockSize, 5));
        // 嵌套目录中的小文件。Sub-block file inside a nested directory.
        WriteBytes(temp, "dir/tiny-d.bin", DeterministicBytes(512, 6));

        var imagePath = BuildImage(temp, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = false,
            Fragments = true,
        });

        try
        {
            AssertAllFilesRoundTrip(imagePath, temp);

            using var parser = new ErofsParser(imagePath);
            var paths = parser.EnumerateFiles().Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Assert.Equal(
                new[] { "dir/tiny-d.bin", "exact.bin", "large-tail.bin", "tiny-a.bin", "tiny-b.bin", "tiny-c.bin" },
                paths);
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    [Fact]
    public void Builder_Dedup_SharedBlocks()
    {
        using var temp = new TempDirectory();
        // 两份完全相同的随机（不可压缩）大文件 + 一个独立文件。两个镜像只差 Dedup 开关。
        // Two byte-identical incompressible files plus one distinct file; only Dedup differs.
        var shared = DeterministicBytes(200_000, 7);
        WriteBytes(temp, "dup-a.bin", shared);
        WriteBytes(temp, "dup-b.bin", shared);
        WriteBytes(temp, "other.bin", DeterministicBytes(60_000, 8));

        var plain = BuildImage(temp, new ErofsOptions { BlockSize = BlockSize, Compress = false, Dedup = false });
        var dedup = BuildImage(temp, new ErofsOptions { BlockSize = BlockSize, Compress = false, Dedup = true });
        try
        {
            AssertAllFilesRoundTrip(plain, temp);
            AssertAllFilesRoundTrip(dedup, temp);

            // 整文件级去重：数据区少存一份相同整块内容。
            // Whole-file dedupe: the data area holds one fewer copy of the identical whole blocks.
            var plainBytes = new FileInfo(plain).Length;
            var dedupBytes = new FileInfo(dedup).Length;
            Assert.True(dedupBytes < plainBytes, $"expected dedup image smaller: dedup={dedupBytes} plain={plainBytes}");
        }
        finally
        {
            TryDelete(plain);
            TryDelete(dedup);
        }
    }

    [Fact]
    public void Builder_FragmentsAndDedup_ReadBack()
    {
        using var temp = new TempDirectory();
        // 相同小文件（碎片去重共享 packed 偏移）+ 相同大文件（整块去重共享数据块）。
        // Identical sub-block files (share one packed offset) plus identical larger files
        // (share one flat whole-block range).
        WriteBytes(temp, "s1.bin", DeterministicBytes(700, 11));
        WriteBytes(temp, "s2.bin", DeterministicBytes(700, 11));
        WriteBytes(temp, "s3.bin", DeterministicBytes(900, 12));
        WriteBytes(temp, "big1.bin", DeterministicBytes(150_000, 13));
        WriteBytes(temp, "big2.bin", DeterministicBytes(150_000, 13));
        WriteBytes(temp, "tail.bin", DeterministicBytes(3000, 14));

        var noDedup = BuildImage(temp, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = false,
            Fragments = true,
            Dedup = false,
        });
        var dedup = BuildImage(temp, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = false,
            Fragments = true,
            Dedup = true,
        });
        try
        {
            AssertAllFilesRoundTrip(noDedup, temp);
            AssertAllFilesRoundTrip(dedup, temp);

            var noDedupBytes = new FileInfo(noDedup).Length;
            var dedupBytes = new FileInfo(dedup).Length;
            Assert.True(dedupBytes < noDedupBytes, $"expected dedup image smaller: dedup={dedupBytes} no-dedup={noDedupBytes}");
        }
        finally
        {
            TryDelete(noDedup);
            TryDelete(dedup);
        }
    }

    [Fact]
    public void Builder_Fragments_WithDefaultCompression_ReadBack()
    {
        using var temp = new TempDirectory();
        // 压缩开启（默认）时：可压缩小文件走压缩路径，不可压缩小文件仍汇入 packed inode，
        // 二者必须都能读回。Compression is on (the default): compressible files take the
        // compressed path while incompressible sub-block files are still packed; both must read
        // back intact.
        WriteBytes(temp, "compressible.txt", System.Text.Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 200))));
        WriteBytes(temp, "incompressible.bin", DeterministicBytes(2500, 31));
        WriteBytes(temp, "large.bin", DeterministicBytes(80_000, 32));

        var imagePath = BuildImage(temp, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            CompressionLevel = 1,
            Fragments = true,
        });

        try
        {
            AssertAllFilesRoundTrip(imagePath, temp);
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    // ---- WSL cross-validation (fsck.erofs / dump.erofs); skipped when unavailable. ----

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

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

    private static (string Output, int ExitCode) RunWsl(string command, int timeoutMs = 60000)
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

    private static bool IsToolAvailable(string tool)
    {
        var (output, exitCode) = RunWsl($"command -v {tool}", 15000);
        var line = output.Trim();
        return exitCode == 0 && line.Length > 0 && !line.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToWslPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        if (full.Length >= 2 && full[1] == ':')
        {
            var drive = char.ToLowerInvariant(full[0]);
            return $"/mnt/{drive}{full[2..].Replace('\\', '/')}";
        }

        return full.Replace('\\', '/');
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-erofs-frag-" + Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRecursive(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    [Fact]
    public void FragmentsAndDedup_FsckErofs_PassesOnBuiltImage()
    {
        if (!IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!IsToolAvailable("fsck.erofs") || !IsToolAvailable("dump.erofs")) { Assert.Skip("required erofs tools missing"); }

        var root = CreateTempDirectory();
        var imagePath = Path.Combine(Path.GetTempPath(), $"wsl-erofs-frag-{Path.GetRandomFileName()}.img");
        try
        {
            for (var i = 1; i <= 8; i++)
            {
                File.WriteAllBytes(Path.Combine(root, $"small{i}.bin"), DeterministicBytes(i * 317 % 3700 + 20, i));
            }

            File.WriteAllBytes(Path.Combine(root, "dup1.bin"), DeterministicBytes(60_000, 21));
            File.WriteAllBytes(Path.Combine(root, "dup2.bin"), DeterministicBytes(60_000, 21));
            File.WriteAllBytes(Path.Combine(root, "remainder.bin"), DeterministicBytes(100_000, 22));

            new ErofsBuilder().Build(root, imagePath, new ErofsOptions
            {
                BlockSize = BlockSize,
                Compress = false,
                Fragments = true,
                Dedup = true,
            });

            var wslImage = ToWslPath(imagePath);

            // fsck.erofs：只读结构检查（exit 0 = OK）。
            var (fsckOut, fsckExit) = RunWsl($"fsck.erofs {wslImage}");
            Assert.True(fsckExit == 0, $"fsck.erofs failed (exit={fsckExit}):\n{fsckOut}");

            // dump.erofs：列目录并逐文件以 sha256 核对内容。
            var (lsOut, lsExit) = RunWsl($"dump.erofs --ls --path=/ {wslImage}");
            Assert.True(lsExit == 0, $"dump.erofs --ls failed (exit={lsExit}):\n{lsOut}");
            foreach (var file in Directory.EnumerateFiles(root))
            {
                var name = Path.GetFileName(file);
                Assert.Contains(name, lsOut, StringComparison.Ordinal);
                var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
                var (catOut, catExit) = RunWsl($"dump.erofs --cat --path=/{name} {wslImage} | sha256sum");
                Assert.True(catExit == 0, $"dump.erofs --cat failed for '{name}' (exit={catExit}):\n{catOut}");
                Assert.StartsWith(expected, catOut.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            TryDelete(imagePath);
            DeleteRecursive(root);
        }
    }
}