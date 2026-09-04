using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Firmware.F2fs;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// F2FS double indirect 读路径回归测试。F2fsParser.MapBlock 的 Nid[3]/Nid[4] double indirect
/// 映射（<see cref="F2fsParser"/> 内 MapBlock / ReadDoubleIndirectAddress）从未与
/// F2fsBuilder.PlanDnodes 的写入槽位语义一起被验证过；这两条用例构造超出 single indirect
/// 覆盖上限的大文件，经 F2fsBuilder 构建后由 F2fsParser 流式读回并做全文件 SHA-256 对比。
/// <para>Regression tests for the F2FS double-indirect read path. F2fsParser.MapBlock's
/// Nid[3]/Nid[4] double-indirect mapping (MapBlock / ReadDoubleIndirectAddress) was never
/// validated against the slot semantics produced by F2fsBuilder.PlanDnodes. Both cases build a
/// file that exceeds the single-indirect addressing range, then stream it back through
/// F2fsParser and compare a full-file SHA-256 against the source.</para>
/// </summary>
public class F2fsDoubleIndirectTests
{
    private const int BlockSize = F2fsNode.NodeSize; // 4096

    // 触发边界推导（必须与 F2fsParser.MapBlock 的分支常量一致）。
    // Boundary derivation (must agree with the branch constants in F2fsParser.MapBlock):
    //   inode i_addr 直接指针   : 0 .. DefAddrsPerInode - 1            (923 blocks)
    //   direct 节点 1 (Nid[0]) : 923 .. 923 + AddrsPerBlock - 1
    //   direct 节点 2 (Nid[1]) : ... + AddrsPerBlock
    //   single indirect (Nid[2]): 2959 .. 2959 + AddrsPerBlock^2 - 1
    // double indirect (Nid[3]) 从逻辑块 DefAddrsPerInode + 2*AddrsPerBlock + AddrsPerBlock^2
    // （= 1,039,283）开始。Single indirect 最多覆盖 1,039,283 个块；再往后即触发 double indirect。
    /// <summary>
    /// 首个需要 double indirect 映射的逻辑块号。
    /// <para>First logical block that requires double-indirect mapping.</para>
    /// </summary>
    private static long FirstDoubleIndirectLogical
    {
        get
        {
            var perIndirect = (long)F2fsNode.AddrsPerBlock * F2fsNode.AddrsPerBlock;
            return F2fsNode.DefAddrsPerInode + 2L * F2fsNode.AddrsPerBlock + perIndirect;
        }
    }

    [Fact]
    public void Parser_ReadLargeFile_DoubleIndirect_Matches()
    {
        var root = CreateTempDirectory();
        var imageRoot = CreateTempDirectory();
        try
        {
            var sourceFile = Path.Combine(root, "huge.bin");
            var imagePath = Path.Combine(imageRoot, "image.img");

            // 确界：single indirect 覆盖逻辑块 0..1,039,282（共 1,039,283 块），
            // double indirect 自逻辑块 1,039,283 起。文件须至少含 (1,039,283 + 1) 个块，
            // 使最后一个块恰好落入 Nid[3] 的第一个槽位，从而触发 double indirect 数据读。
            // Exact bound: single indirect covers logical blocks 0..1,039,282 (1,039,283 blocks);
            // double indirect starts at logical block 1,039,283. The file must contain at least
            // (1,039,283 + 1) blocks so its final block lands in the first Nid[3] slot.
            var firstDoubleIndirectBlock = FirstDoubleIndirectLogical;
            Assert.Equal(
                F2fsNode.DefAddrsPerInode + 2L * F2fsNode.AddrsPerBlock + (long)F2fsNode.AddrsPerBlock * F2fsNode.AddrsPerBlock,
                firstDoubleIndirectBlock);

            var blockCount = firstDoubleIndirectBlock + 1; // 1,039,284
            var fileSize = checked(blockCount * BlockSize);

            // 整文件真实数据（无洞），块内容由「逻辑块号 + 周期字节」构成，各块唯一。
            // Full real data (no holes); every block carries its own logical index so any slot /
            // ordering mismatch shows up as a hash difference.
            WriteFullDataFile(sourceFile, blockCount);
            Assert.Equal(fileSize, new FileInfo(sourceFile).Length);

            F2fsBuilder.Build(root, imagePath, new F2fsOptions { EnableSparse = false });

            using var parser = new F2fsParser(imagePath);
            var entry = parser.ListFiles().Single(static e => e.Path == "huge.bin");
            Assert.Equal(fileSize, entry.Size);

            var expected = Sha256File(sourceFile);
            var actual = ReadFileSha256(parser, "huge.bin");
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));
        }
        finally
        {
            DeleteRecursive(root);
            DeleteRecursive(imageRoot);
        }
    }

    [Fact]
    public void Parser_ReadLargeFile_WithHoles_Matches()
    {
        var root = CreateTempDirectory();
        var imageRoot = CreateTempDirectory();
        try
        {
            var sourceFile = Path.Combine(root, "holes.bin");
            var imagePath = Path.Combine(imageRoot, "image.img");

            // 布局：0..2,958 为真实数据（inode i_addr + 两个 direct 节点），随后是一整段零洞区
            // （从 single indirect 起点 2,959 一直延伸到 double indirect 区域内 2,959 块），
            // 末尾 3 块真实数据放在 double indirect 区域的同一 direct 节点中 —— 该节点前 923
            // 槽位是空洞、后 3 槽位是数据，可校验 Nid[3] 槽位语义与洞补零的联合行为。
            // Layout: blocks 0..2,958 hold real data (inode i_addr + two direct nodes); a long
            // zero-hole region then runs from the single-indirect start (2,959) for 2,959 blocks
            // into the double-indirect region; the final three blocks hold real data inside one
            // Nid[3] direct node whose first 923 slots are holes and last 3 are data, exercising
            // slot semantics plus holes together.
            var headBlocks = (int)(F2fsNode.DefAddrsPerInode + 2L * F2fsNode.AddrsPerBlock); // 2,959
            const int tailBlocks = 3;
            var tailStart = FirstDoubleIndirectLogical + headBlocks; // 1,039,286 (within Nid[3])
            var blockCount = tailStart + tailBlocks;                 // 1,039,289
            var fileSize = checked(blockCount * BlockSize);

            WriteSparseFile(sourceFile, blockCount, headBlocks, tailStart, tailBlocks);
            Assert.Equal(fileSize, new FileInfo(sourceFile).Length);

            F2fsBuilder.Build(root, imagePath, new F2fsOptions { EnableSparse = true });

            using var parser = new F2fsParser(imagePath);
            var entry = parser.ListFiles().Single(static e => e.Path == "holes.bin");
            Assert.Equal(fileSize, entry.Size);

            var expected = Sha256File(sourceFile);
            var actual = ReadFileSha256(parser, "holes.bin");
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));
        }
        finally
        {
            DeleteRecursive(root);
            DeleteRecursive(imageRoot);
        }
    }

    /// <summary>写满 fileSize = blockCount * 4096 的真实数据文件，每块内容唯一。
    /// Writes a fully-real-data file of blockCount blocks with unique per-block content.</summary>
    private static void WriteFullDataFile(string path, long blockCount)
    {
        const int chunkBlocks = 1024; // 4 MiB per chunk
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        var chunk = new byte[chunkBlocks * BlockSize];
        long written = 0;
        while (written < blockCount)
        {
            var count = (int)Math.Min(chunkBlocks, blockCount - written);
            var span = chunk.AsSpan(0, count * BlockSize);
            for (var i = 0; i < count; i++)
            {
                FillDataBlock(span.Slice(i * BlockSize, BlockSize), written + i);
            }

            fs.Write(chunk, 0, count * BlockSize);
            written += count;
        }
    }

    /// <summary>稀疏源文件：只物理写入头/尾数据块，中间零洞区经 SetLength 留空（读回为零）。
    /// Sparse source file: only head/tail data blocks are physically written; the middle zero
    /// region is left as a SetLength gap (reads back as zeros).</summary>
    private static void WriteSparseFile(string path, long blockCount, int headBlocks, long tailStart, int tailBlocks)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        fs.SetLength(checked(blockCount * BlockSize));

        var block = new byte[BlockSize];
        for (var i = 0; i < headBlocks; i++)
        {
            FillDataBlock(block, i);
            fs.Position = (long)i * BlockSize;
            fs.Write(block, 0, BlockSize);
        }

        for (var i = 0; i < tailBlocks; i++)
        {
            FillDataBlock(block, tailStart + i);
            fs.Position = (tailStart + i) * BlockSize;
            fs.Write(block, 0, BlockSize);
        }

        fs.Flush();
    }

    /// <summary>填充一个 4096 字节数据块：前 8 字节为逻辑块号（LE），其余为 1..250 周期字节。
    /// Fills one 4096-byte block: an 8-byte little-endian logical block number followed by a
    /// 1..250 cyclic byte pattern (each block is unique and never all-zero).</summary>
    private static void FillDataBlock(Span<byte> block, long logical)
    {
        BinaryPrimitives.WriteInt64LittleEndian(block, logical);
        block.Slice(8).Fill((byte)(1 + logical % 250));
    }

    private static byte[] Sha256File(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return hash.GetHashAndReset();
    }

    /// <summary>将文件流式读回并只计算 SHA-256（不落盘）。
    /// Streams a file back through F2fsParser and returns its SHA-256 (never written to disk).</summary>
    private static byte[] ReadFileSha256(F2fsParser parser, string filePath)
    {
        using var sink = new Sha256Sink();
        parser.ReadFile(filePath, sink);
        return sink.GetHashAndReset();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-f2fs-" + Path.GetRandomFileName());
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

    /// <summary>只收集写入数据的流，实时喂入 SHA-256，避免把数 GB 读回数据落盘。
    /// A write-only stream that feeds everything written through SHA-256, avoiding multi-GB
    /// read-back files.</summary>
    private sealed class Sha256Sink : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _disposed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => _hash.AppendData(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _hash.AppendData(buffer);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Flush() { }

        public byte[] GetHashAndReset() => _hash.GetHashAndReset();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _hash.Dispose();
                _disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
