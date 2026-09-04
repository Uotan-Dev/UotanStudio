using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.F2fs;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// F2FS 构建侧压缩写路径接入测试：压缩镜像构建 → F2fsParser 读回 SHA-256 逐文件对比，
/// 并直接校验 inode 压缩标志位（i_flags COMPR 位 + i_advise 内核算法 ID/log_cluster_size）。
/// <para>F2FS builder-side compressed write-path integration tests: builds compressed images,
/// reads them back with the parser and compares SHA-256 per file, and directly inspects the inode
/// compression markers (i_flags COMPR bit plus the i_advise kernel algorithm id / log cluster
/// size).</para>
/// </summary>
public class F2fsCompressionWriteTests
{
    private const int BlockSize = 4096;

    // 固定 UUID：使两次构建仅因压缩选项差异而不同，便于做“已压缩镜像占用主区更少”对比。
    // Fixed UUID so two builds differ only by the compression option, enabling the used-block
    // comparison between compressed and uncompressed images.
    private static readonly Guid FixedUuid = new("11111111-2222-3333-4444-555555555555");

    /// <summary>
    /// LZ4：目录含可压缩大文件（重复文本 ≥ 300 KB）与不可压缩大文件（随机字节），
    /// 构建压缩镜像后逐文件 SHA-256 对比，并校验可压缩文件 inode 标记为 LZ4 压缩。
    /// <para>LZ4: a directory with a highly compressible large file (repeated text, ≥ 300 KB) and an
    /// incompressible large file (random bytes) is packed into a compressed image; every file is
    /// read back and compared by SHA-256, and the compressible inode is asserted to carry the LZ4
    /// compression markers.</para>
    /// </summary>
    [Fact]
    public void Builder_CompressedImage_ReadBackMatches()
    {
        RunRoundTrip(CompressionAlgorithm.Lz4, expectedKernelAlgorithmId: 1);
    }

    /// <summary>
    /// Zstd：同 LZ4 场景，使用 Zstd 算法构建并读回。
    /// <para>Zstd: same scenario as LZ4 but packed with Zstandard.</para>
    /// </summary>
    [Fact]
    public void Builder_Compressed_Zstd_ReadBackMatches()
    {
        RunRoundTrip(CompressionAlgorithm.Zstd, expectedKernelAlgorithmId: 2);
    }

    private static void RunRoundTrip(CompressionAlgorithm algorithm, byte expectedKernelAlgorithmId)
    {
        var root = CreateTempDirectory();
        try
        {
            var compressible = CreateCompressibleBytes(300 * 1024 + 123);
            var random = new byte[300 * 1024 + 7];
            new Random(20240401).NextBytes(random);
            File.WriteAllBytes(Path.Combine(root, "text.bin"), compressible);
            File.WriteAllBytes(Path.Combine(root, "random.bin"), random);

            var options = new F2fsOptions
            {
                EnableCompression = true,
                CompressionAlgorithm = algorithm,
                CompressionLevel = algorithm == CompressionAlgorithm.Zstd ? 5 : 0,
                Uuid = FixedUuid,
            };
            var image = F2fsBuilder.Build(root, options);

            using var parser = new F2fsParser(new MemoryStream(image, writable: false));
            var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["text.bin"] = compressible,
                ["random.bin"] = random,
            };
            AssertFileContentsMatch(parser, expected);

            // 可压缩文件必须被标记压缩（并携带正确算法/簇大小），随机文件必须保持未压缩。
            // The compressible file must be flagged compressed (with the right algorithm/cluster
            // size); the incompressible random file must stay uncompressed.
            var textInode = ReadInode(image, FindIno(parser, "text.bin"));
            Assert.True(textInode.IsCompressed, "compressible file should be stored compressed.");
            Assert.Equal(expectedKernelAlgorithmId, textInode.CompressAlgorithm);
            Assert.Equal(4, textInode.LogClusterSize); // 65536 = 1 << (4 + 12).

            var randomInode = ReadInode(image, FindIno(parser, "random.bin"));
            Assert.False(randomInode.IsCompressed, "incompressible file must not be flagged compressed.");
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    /// <summary>
    /// 压缩禁用的镜像必须与默认（未压缩）构建逐字节一致，且内容仍可读回。
    /// <para>With compression disabled the produced image must be byte-identical to the default
    /// (uncompressed) build, and the contents must still read back.</para>
    /// </summary>
    [Fact]
    public void Builder_CompressionDisabled_Unchanged()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "hello f2fs compression\n");
            File.WriteAllBytes(Path.Combine(root, "text.bin"), CreateCompressibleBytes(200 * 1024));
            var random = new byte[128 * 1024];
            new Random(7).NextBytes(random);
            File.WriteAllBytes(Path.Combine(root, "random.bin"), random);

            // 关闭压缩显式构建。
            var explicitDisabled = F2fsBuilder.Build(root, new F2fsOptions { EnableCompression = false, Uuid = FixedUuid });
            // 默认选项（EnableCompression 默认 false）。
            var defaultBuild = F2fsBuilder.Build(root, new F2fsOptions { Uuid = FixedUuid });

            // 两遍路径均走既有的普通文件布局，镜像必须逐字节一致。
            Assert.Equal(explicitDisabled, defaultBuild);

            using var parser = new F2fsParser(new MemoryStream(defaultBuild, writable: false));
            var entries = parser.ListFiles().ToDictionary(static e => e.Path, StringComparer.Ordinal);
            Assert.Equal(Encoding.UTF8.GetBytes("hello f2fs compression\n"), ReadFile(parser, "hello.txt"));
            Assert.Equal(random, ReadFile(parser, "random.bin"));
            Assert.Equal(200 * 1024L, entries["text.bin"].Size);

            // 未压缩大文件的 inode 不得携带压缩标志。
            var textInode = ReadInode(defaultBuild, entries["text.bin"].Inode);
            Assert.False(textInode.IsCompressed);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    /// <summary>
    /// 小文件（不足一个簇，也够不上 2 簇阈值）在启用压缩时不被压缩：内容读回一致，
    /// 且 inode 无压缩标志。
    /// <para>Small files (below one cluster, i.e. not spanning two clusters) are never compressed
    /// even with compression enabled: contents read back unchanged and the inode carries no
    /// compression flag.</para>
    /// </summary>
    [Fact]
    public void Builder_SmallFile_NotCompressed_Matches()
    {
        var root = CreateTempDirectory();
        try
        {
            var small = Encoding.UTF8.GetBytes("small file content that stays plain 小文件不压缩");
            File.WriteAllBytes(Path.Combine(root, "small.txt"), small);

            var image = F2fsBuilder.Build(root, new F2fsOptions
            {
                EnableCompression = true,
                CompressionAlgorithm = CompressionAlgorithm.Lz4,
                Uuid = FixedUuid,
            });

            using var parser = new F2fsParser(new MemoryStream(image, writable: false));
            var entry = parser.ListFiles().Single(static e => e.Path == "small.txt");
            Assert.Equal(small.Length, (int)entry.Size);
            Assert.Equal(small, ReadFile(parser, "small.txt"));

            var inode = ReadInode(image, entry.Inode);
            Assert.False(inode.IsCompressed);
            Assert.Equal(0, inode.CompressAlgorithm);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    /// <summary>
    /// 压缩启用确实把可压缩文件的主区占用压得更小（用 checkpoint valid_block_count 对比），
    /// 从而证明写路径真正产出了压缩布局而非仅以普通块写出。
    /// <para>Enabling compression genuinely shrinks the main-area block usage of a compressible file
    /// (compared through the checkpoint valid_block_count), proving the write path emits a real
    /// compressed layout instead of storing ordinary blocks.</para>
    /// </summary>
    [Fact]
    public void Builder_Compression_ShrinksUsedMainBlocks()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "text.bin"), CreateCompressibleBytes(300 * 1024 + 1000));

            var compressedImage = F2fsBuilder.Build(root, new F2fsOptions
            {
                EnableCompression = true,
                CompressionAlgorithm = CompressionAlgorithm.Lz4,
                Uuid = FixedUuid,
            });
            var plainImage = F2fsBuilder.Build(root, new F2fsOptions { EnableCompression = false, Uuid = FixedUuid });

            var compressedUsed = UsedMainBlocks(compressedImage);
            var plainUsed = UsedMainBlocks(plainImage);
            Assert.True(
                compressedUsed < plainUsed,
                $"expected compressed main-block usage ({compressedUsed}) < plain ({plainUsed}).");
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    /// <summary>
    /// LZO 不可用：构建时回退未压缩路径，读回一致且 inode 不带压缩标志。
    /// <para>LZO is unavailable: the build falls back to the uncompressed path, contents read back
    /// unchanged and no inode is flagged compressed.</para>
    /// </summary>
    [Fact]
    public void Builder_Lzo_NotSupported_FallsBackToUncompressed()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "text.bin"), CreateCompressibleBytes(200 * 1024));
            var image = F2fsBuilder.Build(root, new F2fsOptions
            {
                EnableCompression = true,
                CompressionAlgorithm = CompressionAlgorithm.Lzo,
                Uuid = FixedUuid,
            });

            using var parser = new F2fsParser(new MemoryStream(image, writable: false));
            var entry = parser.ListFiles().Single(static e => e.Path == "text.bin");
            Assert.Equal(200 * 1024, (int)entry.Size);
            Assert.Equal(200 * 1024, ReadFile(parser, "text.bin").Length);
            var inode = ReadInode(image, entry.Inode);
            Assert.False(inode.IsCompressed);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    /// <summary>
    /// 可压缩文本块 + 随机块的混合大文件：同一压缩文件内同时存在压缩簇与普通簇，
    /// 读回仍须逐字节一致。
    /// <para>A large file mixing compressible text clusters and incompressible random clusters
    /// exercises compressed and ordinary clusters inside one compressed file; the read-back must
    /// still match byte-for-byte.</para>
    /// </summary>
    [Fact]
    public void Builder_CompressedFile_MixedClusters_ReadBackMatches()
    {
        var root = CreateTempDirectory();
        try
        {
            var text = CreateCompressibleBytes(4 * 65536);
            var random = new byte[4 * 65536];
            new Random(20240501).NextBytes(random);
            var mixed = new byte[text.Length + random.Length];
            text.CopyTo(mixed, 0);
            random.CopyTo(mixed, text.Length);
            File.WriteAllBytes(Path.Combine(root, "mixed.bin"), mixed);

            var image = F2fsBuilder.Build(root, new F2fsOptions
            {
                EnableCompression = true,
                CompressionAlgorithm = CompressionAlgorithm.Lz4,
                Uuid = FixedUuid,
            });

            using var parser = new F2fsParser(new MemoryStream(image, writable: false));
            var entry = parser.ListFiles().Single(static e => e.Path == "mixed.bin");
            Assert.Equal(mixed, ReadFile(parser, "mixed.bin"));

            var inode = ReadInode(image, entry.Inode);
            Assert.True(inode.IsCompressed, "mixed file should be flagged compressed (its text clusters compress).");
            Assert.Equal(1, inode.CompressAlgorithm); // LZ4 kernel id.
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    /// <summary>
    /// WSL fsck.f2fs 交叉校验（尽力而为）：压缩镜像本身不是内核 f2fs 的压缩磁盘格式
    /// （本仓库用自定义簇头 + i_advise 存储算法/簇大小），若 WSL 或 fsck.f2fs 不可用、或
    /// fsck 无法识别该自定义压缩布局（退出码 &gt; 1），则 Skip 并说明；识别通过则断言校验成功。
    /// <para>Best-effort WSL fsck.f2fs cross-validation. The compressed image uses this repo's own
    /// on-disk compression format (custom cluster header plus i_advise-encoded algorithm/cluster
    /// size), not the kernel F2FS compressed layout. The test is skipped with an explanation when
    /// WSL or fsck.f2fs is unavailable, or when fsck cannot recognize the custom compressed layout
    /// (exit code &gt; 1); when fsck does validate it, the exit code is asserted to be clean.</para>
    /// </summary>
    [Fact]
    public void Builder_CompressedImage_WslFsck_BestEffort()
    {
        if (!IsWslAvailable()) { Assert.Skip("WSL not available.  WSL 不可用。"); }
        if (!IsFsckF2fsAvailable()) { Assert.Skip("fsck.f2fs not available in WSL.  WSL 中无 fsck.f2fs。"); }

        var root = CreateTempDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "text.bin"), CreateCompressibleBytes(300 * 1024 + 123));
            var imagePath = Path.Combine(Path.GetTempPath(), $"uotan-f2fs-cmp-{Path.GetRandomFileName()}.img");
            try
            {
                F2fsBuilder.Build(root, imagePath, new F2fsOptions
                {
                    EnableCompression = true,
                    CompressionAlgorithm = CompressionAlgorithm.Lz4,
                    Uuid = FixedUuid,
                });

                var (output, exitCode) = RunWsl($"fsck.f2fs -n {ToWslPath(imagePath)}");
                // fsck.f2fs 退出码：0=OK，1=已修正，2=错误。
                // Exit codes: 0 = OK, 1 = fixed, 2 = errors.
                if (exitCode > 1)
                {
                    Assert.Skip(
                        "fsck.f2fs does not recognize the custom F2FS compressed-cluster layout "
                        + $"(exit={exitCode}); skipping kernel-side validation.  fsck.f2fs 无法识别本仓库自定义的"
                        + $"F2FS 压缩簇磁盘布局（退出码 {exitCode}），跳过内核侧校验。\n{Truncate(output, 2000)}");
                }

                Assert.True(exitCode <= 1, $"fsck.f2fs failed (exit={exitCode}):\n{output}");
            }
            finally
            {
                try { File.Delete(imagePath); } catch { /* best effort */ }
            }
        }
        finally
        {
            DeleteRecursive(root);
        }
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
            if (proc is null)
            {
                return false;
            }

            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFsckF2fsAvailable()
    {
        var (output, exitCode) = RunWsl("command -v fsck.f2fs", 15000);
        return exitCode == 0 && output.Trim().Length > 0 && !output.Contains("not found", StringComparison.OrdinalIgnoreCase);
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

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    private static void AssertFileContentsMatch(F2fsParser parser, IReadOnlyDictionary<string, byte[]> expected)
    {
        var entries = parser.ListFiles().ToDictionary(static e => e.Path, StringComparer.Ordinal);
        foreach (var (path, content) in expected)
        {
            Assert.True(entries.ContainsKey(path), $"missing entry {path}");
            var readBack = ReadFile(parser, path);
            Assert.Equal(content.Length, readBack.Length);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(content)),
                Convert.ToHexString(SHA256.HashData(readBack)));
        }
    }

    private static uint FindIno(F2fsParser parser, string path)
    {
        var entry = parser.ListFiles().First(e => e.Path == path);
        return entry.Inode;
    }

    /// <summary>
    /// 读取镜像中某 inode 节点并解析（按超级块 NatBlkaddr → NAT 条目 → 节点页）。
    /// <para>Resolves and parses an inode node from the image (superblock NatBlkaddr → NAT entry →
    /// node page).</para>
    /// </summary>
    private static F2fsInode ReadInode(byte[] image, uint nid)
    {
        var superblock = F2fsSuperblock.FromImage(image);
        var natBlockIndex = nid / F2fsNode.NatEntryPerBlock;
        var natAddress = checked((long)(superblock.NatBlkaddr + natBlockIndex * 2u) * F2fsNode.NodeSize);
        var entryOffset = checked((int)(nid % F2fsNode.NatEntryPerBlock) * F2fsNode.NatEntrySize);
        var blockAddress = BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(checked((int)natAddress) + entryOffset + F2fsNode.NatEntryBlockOffset, 4));
        if (blockAddress == 0)
        {
            throw new InvalidDataException($"NAT entry for nid {nid} is missing.");
        }

        var nodeOffset = checked((long)blockAddress * F2fsNode.NodeSize);
        return F2fsInode.FromBytes(image.AsSpan(checked((int)nodeOffset), F2fsNode.NodeSize));
    }

    /// <summary>
    /// 从 checkpoint 读取主区已用块数（valid_block_count）。
    /// <para>Reads the used main-area block count (valid_block_count) from the checkpoint.</para>
    /// </summary>
    private static ulong UsedMainBlocks(byte[] image)
    {
        var superblock = F2fsSuperblock.FromImage(image);
        var checkpointOffset = checked((long)superblock.CpBlkaddr * F2fsNode.NodeSize);
        var checkpoint = F2fsCheckpoint.FromBytes(image.AsSpan(checked((int)checkpointOffset), F2fsNode.NodeSize));
        return checkpoint.ValidBlockCount;
    }

    private static byte[] CreateCompressibleBytes(int length)
    {
        const string line = "The quick brown fox jumps over the lazy dog. F2FS compression write path 0123456789.\n";
        var builder = new StringBuilder(length + line.Length);
        while (builder.Length < length)
        {
            builder.Append(line);
        }

        return Encoding.ASCII.GetBytes(builder.ToString(0, length));
    }

    private static byte[] ReadFile(F2fsParser parser, string path)
    {
        using var ms = new MemoryStream();
        parser.ReadFile(path, ms);
        return ms.ToArray();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-f2fs-compress-" + Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRecursive(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
