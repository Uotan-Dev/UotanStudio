using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Erofs;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// EROFS METABOX 与 multi-device 单元测试。
/// <para>EROFS METABOX and multi-device unit tests.</para>
/// </summary>
public class ErofsMetaboxTests
{
    [Fact]
    public void Builder_TailPacking_SmallFileInlined()
    {
        using var temp = new TempDirectory();
        // 写盘不带 UTF-8 BOM（TempDirectory.Write 用带 BOM 的 Encoding.UTF8，会把 12 字节内容
        // 扩成 15 字节），保证源文件字节数与断言意图一致。Write without a UTF-8 BOM (Write would
        // use the BOM-emitting Encoding.UTF8 and inflate the 12-byte content to 15 bytes), so the
        // source bytes match the intended 12-byte assertions.
        temp.WriteBytes("small.txt", Encoding.UTF8.GetBytes("tiny content"));

        var img = Path.Combine(temp.Path, "out.img");
        var options = new ErofsOptions
        {
            BlockSize = 4096,
            Compress = false,
            TailPacking = true,
        };
        new ErofsBuilder().Build(temp.Path, img, options);

        using var parser = new ErofsParser(img);
        var entries = parser.EnumerateFiles();
        var small = Assert.Single(entries, e => e.Path == "small.txt");
        Assert.Equal("tiny content".Length, small.Size);
        Assert.Equal("tiny content", Encoding.UTF8.GetString(parser.ReadFileBytes("small.txt")));
    }

    [Fact]
    public void Builder_TailPacking_Disabled_AllFilesFlat()
    {
        using var temp = new TempDirectory();
        temp.Write("small.txt", "tiny");
        temp.Write("large.txt", string.Concat(Enumerable.Repeat("ABCDEFGHIJ", 5000)));

        var img = Path.Combine(temp.Path, "out.img");
        var options = new ErofsOptions
        {
            BlockSize = 4096,
            Compress = false,
            TailPacking = false,
        };
        new ErofsBuilder().Build(temp.Path, img, options);

        using var parser = new ErofsParser(img);
        var expected = File.ReadAllBytes(Path.Combine(temp.Path, "large.txt"));
        Assert.Equal(expected, parser.ReadFileBytes("large.txt"));
    }

    [Fact]
    public void Builder_MultiAlgorithm_Lzma_Compresses()
    {
        using var temp = new TempDirectory();
        temp.Write("data.txt", string.Concat(Enumerable.Repeat("The quick brown fox. ", 5000)));

        var img = Path.Combine(temp.Path, "out.img");
        var options = new ErofsOptions
        {
            BlockSize = 4096,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lzma,
            CompressionLevel = 1,
        };
        new ErofsBuilder().Build(temp.Path, img, options);

        using var parser = new ErofsParser(img);
        var expected = File.ReadAllBytes(Path.Combine(temp.Path, "data.txt"));
        Assert.Equal(expected, parser.ReadFileBytes("data.txt"));
    }

    [Fact]
    public void Builder_MultiAlgorithm_Deflate_Compresses()
    {
        using var temp = new TempDirectory();
        temp.Write("data.txt", string.Concat(Enumerable.Repeat("Hello EROFS! ", 3000)));

        var img = Path.Combine(temp.Path, "out.img");
        var options = new ErofsOptions
        {
            BlockSize = 4096,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Deflate,
            CompressionLevel = 1,
        };
        new ErofsBuilder().Build(temp.Path, img, options);

        using var parser = new ErofsParser(img);
        var expected = File.ReadAllBytes(Path.Combine(temp.Path, "data.txt"));
        Assert.Equal(expected, parser.ReadFileBytes("data.txt"));
    }

    [Fact]
    public void Builder_MultiAlgorithm_Zstd_Compresses()
    {
        using var temp = new TempDirectory();
        temp.Write("data.txt", string.Concat(Enumerable.Repeat("ZSTD compression test. ", 3000)));

        var img = Path.Combine(temp.Path, "out.img");
        var options = new ErofsOptions
        {
            BlockSize = 4096,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Zstd,
            CompressionLevel = 1,
        };
        new ErofsBuilder().Build(temp.Path, img, options);

        using var parser = new ErofsParser(img);
        var expected = File.ReadAllBytes(Path.Combine(temp.Path, "data.txt"));
        Assert.Equal(expected, parser.ReadFileBytes("data.txt"));
    }

    [Fact]
    public void Builder_LargeDirectory_ManyFiles()
    {
        using var temp = new TempDirectory();
        for (var i = 0; i < 200; i++)
        {
            // 不带 UTF-8 BOM 写盘（TempDirectory.Write 用带 BOM 的 Encoding.UTF8，会把每个文件
            // 多写 3 字节 BOM 前缀，导致回读断言多出 U+FEFF）。Write without a UTF-8 BOM so the
            // source files are exactly the plain ASCII content the read-back assertions expect.
            temp.WriteBytes($"file_{i:D4}.txt", Encoding.UTF8.GetBytes($"content of file {i}"));
        }

        var img = Path.Combine(temp.Path, "out.img");
        new ErofsBuilder().Build(temp.Path, img, new ErofsOptions { Compress = false });

        using var parser = new ErofsParser(img);
        var entries = parser.EnumerateFiles().ToList();
        Assert.Equal(200, entries.Count);

        // 验证几个随机文件的内容。
        for (var i = 0; i < 200; i += 50)
        {
            var expected = $"content of file {i}";
            var actual = Encoding.UTF8.GetString(parser.ReadFileBytes($"file_{i:D4}.txt"));
            Assert.Equal(expected, actual);
        }
    }

    private const ulong NidMetaboxFlag = 1UL << 63;

    /// <summary>
    /// 构造一个含目录、子目录、整块与非块对齐（余数）文件的样本树。
    /// <para>Builds a sample tree with directories, a sub-directory, whole-block and
    /// non-block-aligned (remainder) files.</para>
    /// </summary>
    private static void WriteSampleTree(TempDirectory temp)
    {
        temp.Write("hello.txt", "hello metabox\n");
        temp.WriteBytes("blob.bin", Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());          // 整块。Whole block.
        temp.WriteBytes("remainder.bin", Enumerable.Range(0, 4101).Select(i => (byte)(i * 7 + 3)).ToArray());  // 余数。Non-block remainder.
        temp.WriteBytes("tail.bin", Enumerable.Range(0, 900).Select(i => (byte)(i * 13)).ToArray());           // 小余数。Small remainder.
        temp.Write("sub/inner.txt", "nested content inside sub directory\n");
        temp.WriteBytes("sub/payload.bin", Enumerable.Range(0, 8192).Select(i => (byte)(i >> 8)).ToArray());   // 双整块。Two whole blocks.
    }

    /// <summary>
    /// 校验镜像内全部文件与源目录树逐字节一致（含目录层次）。
    /// <para>Asserts every file inside the image byte-matches the source directory tree (including
    /// nested directories).</para>
    /// </summary>
    private static void AssertAllFilesMatchSource(string imagePath, string sourceDirectory)
    {
        using var parser = new ErofsParser(imagePath);
        var entries = parser.EnumerateFiles().ToList();
        Assert.NotEmpty(entries);
        var failures = new List<string>();
        foreach (var entry in entries)
        {
            var sourceBytes = File.ReadAllBytes(Path.Combine(sourceDirectory, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            var readBack = parser.ReadFileBytes(entry.Path);
            if (!sourceBytes.AsSpan().SequenceEqual(readBack))
            {
                failures.Add($"{entry.Path}: source {sourceBytes.Length} bytes != read-back {readBack.Length} bytes");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Metabox_Default_False()
    {
        var options = new ErofsOptions();
        Assert.False(options.Metabox);
        Assert.True(options.TailPacking); // 默认 flat/tailpacking 布局不受影响。Default layout is unaffected.
    }

    [Fact]
    public void Metabox_Enabled_ReadBackFiles()
    {
        using var temp = new TempDirectory();
        WriteSampleTree(temp);
        var img = Path.Combine(temp.Path, "out.img");

        var options = new ErofsOptions { Compress = false, Metabox = true };
        options.Validate();
        // METABOX 强制关闭 TailPacking（容器记录须为纯 32 字节 compact inode）。
        Assert.False(options.TailPacking);
        new ErofsBuilder().Build(temp.Path, img, options);

        using var parser = new ErofsParser(img);
        // METABOX 特性位 + 扩展超级块槽位 + metabox inode nid 均已写入。
        // The METABOX incompat bit, the extended superblock slot and the metabox inode nid are set.
        Assert.True(parser.Superblock.HasMetabox);
        Assert.True(parser.Superblock.SbExtSlots >= 1);
        Assert.NotEqual(0UL, parser.Superblock.MetaBoxNid);

        // 普通文件全部为容器驻留（dirent nid 带 bit63），目录自身仍是经典 nid。
        // Every regular file is container-resident (dirent nid carries bit 63); directories stay classic.
        var entries = parser.EnumerateFiles().ToList();
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.True((e.Nid & NidMetaboxFlag) != 0,
            $"expected metabox nid for {e.Path}, got 0x{e.Nid:X16}"));
        AssertAllFilesMatchSource(img, temp.Path);
    }

    [Fact]
    public void Metabox_Disabled_Flat_ReadBack()
    {
        using var temp = new TempDirectory();
        WriteSampleTree(temp);
        var img = Path.Combine(temp.Path, "out.img");

        new ErofsBuilder().Build(temp.Path, img, new ErofsOptions { Compress = false, Metabox = false });

        using var parser = new ErofsParser(img);
        Assert.False(parser.Superblock.HasMetabox);
        Assert.Equal(0UL, parser.Superblock.MetaBoxNid);
        Assert.All(parser.EnumerateFiles(), e => Assert.True((e.Nid & NidMetaboxFlag) == 0,
            $"expected classic nid for {e.Path}, got 0x{e.Nid:X16}"));
        AssertAllFilesMatchSource(img, temp.Path);
    }

    // ---- WSL cross-validation（fsck.erofs / dump.erofs；不可用时 Skip）。WSL cross-validation;
    // skipped when unavailable. ----

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

    private static bool IsErofsToolAvailable(string tool)
    {
        var (output, _) = RunWsl($"command -v {tool}", 15000);
        var line = output.Trim();
        return line.Length > 0 && !line.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// WSL 交叉验证：
    /// <list type="bullet">
    /// <item>Metabox=false（flat 回归）镜像通过 fsck.erofs，dump.erofs 可列目录且文件内容一致；</item>
    /// <item>Metabox=true 镜像被 erofs-utils 1.8.6 拒绝（其不识 METABOX incompat 位 0x100，
    /// 报 "unidentified incompatible feature 100"），自回环读回内容仍一致。</item>
    /// </list>
    /// <para>WSL cross-validation: a Metabox=false (flat regression) image passes fsck.erofs and is
    /// listed with matching contents by dump.erofs; a Metabox=true image is rejected by erofs-utils
    /// 1.8.6 (it does not know the METABOX incompat bit 0x100 and reports "unidentified incompatible
    /// feature 100"), while the self round-trip still reads all contents back correctly.</para>
    /// </summary>
    [Fact]
    public void Metabox_WslFsckDump_FlatPasses_MetaboxDocumentedRejection()
    {
        if (!IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!IsErofsToolAvailable("fsck.erofs") || !IsErofsToolAvailable("dump.erofs")) { Assert.Skip("required erofs tools missing"); }

        var root = Path.Combine(Path.GetTempPath(), $"uotan-erofs-mbx-{Path.GetRandomFileName()}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "hello metabox fsck\n");
            File.WriteAllBytes(Path.Combine(root, "remainder.bin"), Enumerable.Range(0, 4500).Select(i => (byte)(i * 3)).ToArray());
            Directory.CreateDirectory(Path.Combine(root, "dir"));
            File.WriteAllText(Path.Combine(root, "dir", "nested.txt"), "nested content");

            // 1) flat（Metabox=false）回归：fsck.erofs 通过 + dump.erofs 内容一致。
            var flatImage = Path.Combine(Path.GetTempPath(), $"wsl-erofs-mbx-flat-{Path.GetRandomFileName()}.img");
            try
            {
                new ErofsBuilder().Build(root, flatImage, new ErofsOptions { Compress = false, Metabox = false });
                var wslFlat = ToWslPath(flatImage);

                var (fsckOut, fsckExit) = RunWsl($"fsck.erofs {wslFlat}");
                Assert.True(fsckExit == 0, $"fsck.erofs failed on the flat image (exit={fsckExit}):\n{fsckOut}");

                var (lsOut, lsExit) = RunWsl($"dump.erofs --ls --path=/ {wslFlat}");
                Assert.True(lsExit == 0, $"dump.erofs --ls failed (exit={lsExit}):\n{lsOut}");
                Assert.Contains("hello.txt", lsOut, StringComparison.Ordinal);
                Assert.Contains("dir", lsOut, StringComparison.Ordinal);

                var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "remainder.bin")))).ToLowerInvariant();
                var (catOut, catExit) = RunWsl($"dump.erofs --cat --path=/remainder.bin {wslFlat} | sha256sum");
                Assert.True(catExit == 0, $"dump.erofs --cat failed (exit={catExit}):\n{catOut}");
                Assert.StartsWith(expected, catOut.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { File.Delete(flatImage); } catch { }
            }

            // 2) METABOX 镜像：erofs-utils 1.8.6 不识 METABOX 位（0x100），fsck 明确拒绝并提示
            //    升级；内容回环（本解析器）不受影响。
            var metaboxImage = Path.Combine(Path.GetTempPath(), $"wsl-erofs-mbx-mbox-{Path.GetRandomFileName()}.img");
            try
            {
                new ErofsBuilder().Build(root, metaboxImage, new ErofsOptions { Compress = false, Metabox = true });
                var wslMetabox = ToWslPath(metaboxImage);

                var (fsckOut, fsckExit) = RunWsl($"fsck.erofs {wslMetabox}");
                Assert.Equal(1, fsckExit);
                Assert.Contains("unidentified incompatible feature", fsckOut, StringComparison.Ordinal);

                // 自回环读回：内容必须一致（flat 路径已由 fsck 验证，metabox 路径靠回环）。
                // Self round-trip: contents must match (the flat path is fsck-verified; the metabox
                // path relies on the round-trip).
                AssertAllFilesMatchSource(metaboxImage, root);
            }
            finally
            {
                try { File.Delete(metaboxImage); } catch { }
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}