using System.Buffers.Binary;
using System.Diagnostics;
using UotanStudio.Firmware.Ext4;
using UotanStudio.Firmware.Ext4.Core;
using UotanStudio.Firmware.Ext4.Journal;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// EXT4 Journal/JBD2 解析与重放单元测试。
/// <para>EXT4 Journal/JBD2 parsing and replay unit tests.</para>
/// </summary>
public class Ext4JournalTests
{
    [Fact]
    public void JournalSuperblock_FromBytes_ValidV2()
    {
        var data = new byte[JournalSuperblock.MinimumSize];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), JournalSuperblock.Magic);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), 4); // v2
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 1); // sequence
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x0C, 4), 4096); // blocksize
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x10, 4), 100); // maxlen
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x14, 4), 1); // first
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x18, 4), 1); // sequence
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x1C, 4), 0); // start (empty)

        var jsb = JournalSuperblock.FromBytes(data);
        Assert.Equal(JournalSuperblock.Magic, jsb.HeaderMagic);
        Assert.Equal(4u, jsb.HeaderBlockType);
        Assert.Equal(4096u, jsb.BlockSize);
        Assert.Equal(100u, jsb.MaxLen);
        Assert.True(jsb.IsEmpty);
    }

    [Fact]
    public void JournalSuperblock_RejectsInvalidMagic()
    {
        var data = new byte[JournalSuperblock.MinimumSize];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 0xDEADBEEF);
        Assert.Throws<InvalidDataException>(() => JournalSuperblock.FromBytes(data));
    }

    [Fact]
    public void JournalSuperblock_RejectsTruncatedData()
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), JournalSuperblock.Magic);
        Assert.Throws<InvalidDataException>(() => JournalSuperblock.FromBytes(data));
    }

    [Fact]
    public void JournalOverlay_Empty_HasNoOverlay()
    {
        var overlay = JournalOverlay.Empty;
        Assert.False(overlay.HasOverlay);
        Assert.Equal(0, overlay.Count);
        Assert.False(overlay.TryGetBlock(0, out _));
    }

    [Fact]
    public void JournalOverlay_WithData_ReturnsBlocks()
    {
        var blockData = new byte[4096];
        blockData[0] = 0xAB;
        var overlay = new JournalOverlay(new Dictionary<long, byte[]> { [42] = blockData });

        Assert.True(overlay.HasOverlay);
        Assert.Equal(1, overlay.Count);
        Assert.True(overlay.TryGetBlock(42, out var data));
        Assert.Equal(0xAB, data![0]);
        Assert.False(overlay.TryGetBlock(99, out _));
    }

    [Fact]
    public void TransactionReplayer_EmptyTransactions_ReturnsEmptyOverlay()
    {
        var overlay = TransactionReplayer.Replay([]);
        Assert.False(overlay.HasOverlay);
    }

    [Fact]
    public void TransactionReplayer_AppliesTransactionsInOrder()
    {
        var block1 = new byte[4096];
        block1[0] = 0x01;
        var block2 = new byte[4096];
        block2[0] = 0x02;

        var txns = new List<Jbd2Parser.CommittedTransaction>
        {
            new(1, new Dictionary<long, byte[]> { [10] = block1 }, new HashSet<long>()),
            new(2, new Dictionary<long, byte[]> { [10] = block2 }, new HashSet<long>()),
        };

        var overlay = TransactionReplayer.Replay(txns);
        Assert.True(overlay.TryGetBlock(10, out var data));
        Assert.Equal(0x02, data![0]); // 后写的覆盖先写的。
    }

    [Fact]
    public void TransactionReplayer_RevokedBlocks_AreRemoved()
    {
        var block1 = new byte[4096];
        block1[0] = 0x01;

        var txns = new List<Jbd2Parser.CommittedTransaction>
        {
            new(1, new Dictionary<long, byte[]> { [10] = block1 }, new HashSet<long>()),
            new(2, new Dictionary<long, byte[]>(), new HashSet<long> { 10 }),
        };

        var overlay = TransactionReplayer.Replay(txns);
        Assert.False(overlay.TryGetBlock(10, out _));
    }

    [Fact]
    public void BuildAndParse_HtreeRoundTrip()
    {
        var entries = new List<(string Name, uint Inode, byte FileType)>();
        for (var i = 0; i < 100; i++)
        {
            entries.Add(($"file_{i:D4}.txt", (uint)(i + 10), Ext4DirectoryEntry.TypeRegular));
        }

        var seed = new uint[] { 0x12345678, 0x9ABCDEF0, 0x11223344, 0x55667788 };
        var plan = Ext4Htree.BuildHtree(entries, 4096, seed);

        // 验证 dx_root 可以被解析回来。
        var parsedEntries = Ext4Htree.ParseDxEntries(plan.RootBlock, 4096);
        Assert.NotEmpty(parsedEntries);

        // 验证哈希值非递减（允许相等的哈希值）。
        for (var i = 1; i < parsedEntries.Count; i++)
        {
            Assert.True(parsedEntries[i].Hash >= parsedEntries[i - 1].Hash,
                $"dx_entry hash not monotonically non-decreasing at index {i}.");
        }

        // 验证叶块数量正确。
        Assert.NotEmpty(plan.LeafBlocks);
        Assert.Equal(entries.Count > 0, plan.LeafBlocks.Count > 0);
    }

    [Fact]
    public void HalfMd4Hash_Deterministic()
    {
        var seed = new uint[] { 1, 2, 3, 4 };
        var hash1 = Ext4Htree.HalfMd4Hash("hello.txt", seed);
        var hash2 = Ext4Htree.HalfMd4Hash("hello.txt", seed);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void HalfMd4Hash_DifferentNamesDifferentHashes()
    {
        var seed = new uint[] { 1, 2, 3, 4 };
        var hash1 = Ext4Htree.HalfMd4Hash("aaa.txt", seed);
        var hash2 = Ext4Htree.HalfMd4Hash("zzz.txt", seed);
        Assert.NotEqual(hash1, hash2);
    }

    // ---- Journal write side (ToBytes / CreateEmpty). 写端往返与校验和测试。 ----

    /// <summary>固定的 16 字节 UUID，保证测试确定性。<para>Fixed 16-byte UUID for deterministic tests.</para></summary>
    private static readonly byte[] FixedUuid = [0x53, 0xAA, 0x0F, 0x4D, 0x2C, 0x7A, 0x11, 0xEF, 0x8A, 0xB5, 0x00, 0x0C, 0x29, 0xC0, 0xA1, 0xB2];

    [Fact]
    public void JournalSuperblock_ToBytes_RoundTrip()
    {
        var jsb = JournalSuperblock.CreateEmpty(4096, 1024, FixedUuid);

        var bytes = jsb.ToBytes();
        Assert.Equal(JournalSuperblock.SerializedSize, bytes.Length);

        var parsed = JournalSuperblock.FromBytes(bytes);
        Assert.Equal(JournalSuperblock.Magic, parsed.HeaderMagic);
        Assert.Equal(JournalSuperblock.BlockTypeSuperblockV2, parsed.HeaderBlockType);
        Assert.Equal(4096u, parsed.BlockSize);
        Assert.Equal(1024u, parsed.MaxLen);
        Assert.Equal(1u, parsed.Sequence);
        Assert.Equal(1u, parsed.First);
        Assert.Equal(0u, parsed.Start);
        Assert.True(parsed.IsEmpty);
        Assert.Equal(JournalSuperblock.ChecksumTypeCrc32c, parsed.ChecksumType);
        Assert.True(parsed.HasCsumV3);
        Assert.Equal(FixedUuid, parsed.Uuid);
    }

    [Fact]
    public void JournalSuperblock_Checksum_Validates()
    {
        var jsb = JournalSuperblock.CreateEmpty(4096, 1024, FixedUuid);
        var bytes = jsb.ToBytes();

        var parsed = JournalSuperblock.FromBytes(bytes);
        Assert.True(parsed.HasCsumV3);

        // 按同一公式重算：crc32c(~0) 覆盖 [0x00..0x204)，结果大端存于 0x204。
        // Recompute with the same formula: crc32c(~0) over [0x00..0x204), stored big-endian at 0x204.
        var expected = RawCrc32c(bytes.AsSpan(0, 0x204), 0xFFFF_FFFF);
        Assert.Equal(expected, parsed.Checksum);
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0x204, 4)));

        // 篡改任一字段后读回校验和不再匹配。
        // Tampering with any field makes the read-back checksum no longer match.
        var corrupted = (byte[])bytes.Clone();
        corrupted[0x30] ^= 0xFF;
        var corruptParsed = JournalSuperblock.FromBytes(corrupted);
        Assert.NotEqual(RawCrc32c(corrupted.AsSpan(0, 0x204), 0xFFFF_FFFF), corruptParsed.Checksum);
    }

    [Fact]
    public void Builder_IncludeJournal_Default()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "hello ext4\n");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "sub", "inner.txt"), "inner\n");

            var image = Ext4Builder.Build(root);

            // s_feature_compat 含 HAS_JOURNAL(0x4)，s_journal_inum 指向 inode 8。
            var compat = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 0x5C, 4));
            Assert.True((compat & 0x4) != 0, "EXT4_FEATURE_COMPAT_HAS_JOURNAL must be set by default.");
            var journalInum = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 0xE0, 4));
            Assert.Equal(8u, journalInum);

            // Ext4Parser 仍能正常列出文件。
            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
            var paths = parser.ListFiles().Select(static e => e.Path).OrderBy(static p => p, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "hello.txt", "sub", "sub/inner.txt" }, paths);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public void Builder_NoJournal_WhenDisabled()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "alpha");

            var image = Ext4Builder.Build(root, new Ext4Options { IncludeJournal = false });

            var compat = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 0x5C, 4));
            Assert.True((compat & 0x4) == 0, "HAS_JOURNAL must be clear when IncludeJournal=false.");
            var journalInum = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 0xE0, 4));
            Assert.Equal(0u, journalInum);

            using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
            var paths = parser.ListFiles().Select(static e => e.Path).ToArray();
            Assert.Equal(new[] { "a.txt" }, paths);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    /// <summary>
    /// WSL + e2fsprogs 交叉验证（WSL 不可用时 Skip）：dumpe2fs 显示 has_journal / journal inode 8 /
    /// 日志 start 0；debugfs logdump 报告干净空日志；e2fsck 不报任何 journal 相关错误。
    /// 说明：Builder 仍存在若干与 journal 无关的既有问题（inline data 标志位、目录/位图校验和等），
    /// 故 e2fsck 整体退出码非 0；本测试只要求 journal 相关检查通过。
    /// <para>WSL + e2fsprogs cross-validation (skipped when WSL is unavailable): dumpe2fs shows
    /// has_journal / journal inode 8 / journal start 0; debugfs logdump reports a clean empty journal;
    /// e2fsck reports no journal-related errors. The builder still carries pre-existing non-journal
    /// issues (inline-data flag, directory/bitmap checksums), so the overall e2fsck exit code is not 0;
    /// this test only asserts the journal-related checks pass.</para>
    /// </summary>
    [Fact]
    public void Builder_WithJournal_WslSeesCleanJournal()
    {
        if (!IsWslAvailable()) { Assert.Skip("WSL not available"); }
        if (!IsToolAvailable("e2fsck") || !IsToolAvailable("debugfs") || !IsToolAvailable("dumpe2fs")) { Assert.Skip("required e2fsprogs tools missing"); }

        var root = CreateTempDirectory();
        var imagePath = Path.Combine(Path.GetTempPath(), $"uotan-ext4-jrn-{Path.GetRandomFileName()}.img");
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "hello ext4\n");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "sub", "inner.txt"), "inner\n");

            // 关闭 metadata_csum 以便 debugfs 能打开镜像（现有 block 位图校验和缺陷与 journal 无关）。
            // metadata_csum is disabled so debugfs can open the image (the pre-existing block-bitmap
            // checksum defect is unrelated to the journal).
            Ext4Builder.Build(root, imagePath, new Ext4Options { EnableMetadataCsum = false });
            var wslPath = ToWslPath(imagePath);

            var (dumpOut, dumpExit) = RunWsl($"dumpe2fs -h {wslPath}");
            Assert.True(dumpExit == 0, $"dumpe2fs failed:\n{dumpOut}");
            var normalized = System.Text.RegularExpressions.Regex.Replace(dumpOut, @"\s+", " ");
            Assert.Contains("has_journal", normalized, StringComparison.Ordinal);
            Assert.Contains("Journal inode: 8", normalized, StringComparison.Ordinal);
            Assert.Contains("Journal start: 0", normalized, StringComparison.Ordinal);

            // debugfs logdump：与 mke2fs 干净空日志输出一致。
            var (logOut, logExit) = RunWsl($"debugfs -R 'logdump' {wslPath}");
            Assert.True(logExit == 0, $"debugfs logdump failed:\n{logOut}");
            Assert.Contains("Journal starts at block 0, transaction 1", logOut, StringComparison.Ordinal);

            // e2fsck：镜像仍报既有非 journal 问题（exit 4），但绝不能出现 journal 相关错误。
            var (fsckOut, fsckExit) = RunWsl($"e2fsck -fn {wslPath}");
            Assert.False(fsckExit == 8, $"e2fsck could not process the image (operational error):\n{fsckOut}");
            Assert.DoesNotContain("journal", fsckOut, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(imagePath); } catch { }
            DeleteRecursive(root);
        }
    }

    // ---- Helpers（与 WslCrossValidationTests 相同的探测方式）。 ----

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
        var (output, _) = RunWsl($"command -v {tool}", 15000);
        var line = output.Trim();
        return line.Length > 0 && !line.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToWslPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        if (full.Length >= 2 && full[1] == ':')
        {
            var drive = char.ToLowerInvariant(full[0]);
            var rest = full[2..].Replace('\\', '/');
            return $"/mnt/{drive}{rest}";
        }

        return full.Replace('\\', '/');
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-ext4-jrn-" + Path.GetRandomFileName());
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

    /// <summary>
    /// 以给定寄存器 seed 计算 CRC32c（crc32c_le 约定，无最终异或），用于独立验证 ToBytes 的校验和。
    /// <para>Computes CRC32c from a given register seed (crc32c_le convention, no final XOR) to
    /// independently verify the ToBytes checksum.</para>
    /// </summary>
    private static uint RawCrc32c(ReadOnlySpan<byte> data, uint seed)
    {
        uint crc = seed;
        foreach (var b in data)
        {
            crc = Crc32cTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static readonly uint[] Crc32cTable = InitCrc32cTable();

    private static uint[] InitCrc32cTable()
    {
        const uint poly = 0x82F63B78; // bit-reversed 0x1EDC6F41
        var table = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (uint)i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }
}