using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Erofs;
using UotanStudio.Firmware.Ext4;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Verifies the two-pass streaming build model of <see cref="Ext4Builder"/> and
/// <see cref="ErofsBuilder"/>: the streamed output must match the byte[] API byte for byte,
/// large images are written to seekable streams (never a full in-memory image array), and a
/// source file truncated between the layout pass and the data-copy pass fails the build.
/// <para>验证 <see cref="Ext4Builder"/> 与 <see cref="ErofsBuilder"/> 的两遍流式构建模型：
/// 流式输出须与 byte[] API 逐字节一致、大镜像写入可寻址流（不构造完整内存镜像数组），
/// 且布局遍与数据拷贝遍之间被截断的源文件会使构建失败。</para>
/// </summary>
public class BuilderStreamingTests
{
    private const int BlockSize = 4096;

    // ------------------------------------------------------------------ Ext4

    [Fact]
    public void Ext4_StreamingOutput_MatchesByteArrayApi_ByteForByte()
    {
        using var temp = new TempDirectory();
        temp.Write("hello.txt", "hello streaming ext4\n");
        temp.WriteBytes("blob.bin", [0x00, 0x01, 0x02, 0xFE, 0xFF, 0x80, 0x7F]);
        temp.Write("sub/inner.txt", "inner content");
        var big = new byte[200 * 1024];
        for (var i = 0; i < big.Length; i++)
        {
            big[i] = (byte)(i * 31 + 7);
        }

        temp.WriteBytes("large.bin", big);

        var options = new Ext4Options();
        var expected = Ext4Builder.Build(temp.Path, options);
        using var stream = new MemoryStream();
        Ext4Builder.Build(temp.Path, stream, options);

        Assert.Equal(expected, stream.ToArray());
    }

    [Fact]
    public void Ext4_StreamingOutput_PreservesPrefixAndClearsReusedImageRegion()
    {
        using var temp = new TempDirectory();
        temp.Write("a.txt", "alpha");
        var expected = Ext4Builder.Build(temp.Path);
        var prefix = Encoding.ASCII.GetBytes("preserved-prefix");
        using var output = new MemoryStream();
        output.Write(prefix);
        output.Write(Enumerable.Repeat((byte)0xCC, expected.Length * 2).ToArray());
        output.Position = prefix.Length;

        Ext4Builder.Build(temp.Path, output);

        var actual = output.ToArray();
        Assert.Equal(prefix, actual[..prefix.Length]);
        Assert.Equal(expected, actual[prefix.Length..(prefix.Length + expected.Length)]);
        Assert.All(actual[(prefix.Length + expected.Length)..], value => Assert.Equal(0xCC, value));
        Assert.Equal(prefix.Length + expected.Length, output.Position);
        Assert.Equal(prefix.Length + expected.Length * 2, output.Length);
    }

    [Fact]
    public void Ext4_StreamingOutput_MatchesByteArrayApi_WithSparseMultiExtentLeaves()
    {
        using var temp = new TempDirectory();

        // Alternating all-zero / non-zero 4 KiB blocks produce many hole runs, which forces
        // the extent index tree (leaf blocks) to be planned and written. 32 blocks → 16 runs
        // → 16 extents → 4 leaf groups, the maximum the 60-byte inode extent area can index.
        // 交替全零/非零 4KiB 块产生大量空洞 run，强制规划并写出 extent 索引树（叶块）。
        // 32 块 → 16 个 run → 16 个 extent → 4 个叶块组，为 60 字节 inode extent 区的上限。
        var content = new byte[32 * BlockSize];
        for (var i = 0; i < content.Length; i += BlockSize)
        {
            if ((i / BlockSize) % 2 == 0)
            {
                continue; // hole (all zero)
            }

            for (var j = 0; j < BlockSize; j++)
            {
                content[i + j] = (byte)((i + j) % 251);
            }
        }

        temp.WriteBytes("sparse.bin", content);

        var options = new Ext4Options { EnableSparse = true };
        var expected = Ext4Builder.Build(temp.Path, options);
        using var stream = new MemoryStream();
        Ext4Builder.Build(temp.Path, stream, options);

        // The streamed image must equal the byte[] image byte for byte, including the planned
        // extent leaf blocks written for the multi-run sparse file.
        // 流式镜像必须与 byte[] 镜像逐字节一致，包括为多 run 稀疏文件写出的 extent 叶块。
        Assert.Equal(expected, stream.ToArray());
    }

    [Fact]
    public void Ext4_PathApi_MatchesByteArrayApi()
    {
        using var temp = new TempDirectory();
        temp.Write("a.txt", "alpha");
        temp.WriteBytes("b.bin", [1, 2, 3, 4, 5]);

        // Build the byte[] image first: once the path API writes out.img into the source
        // directory, a later byte[] build would pack that stale image into itself.
        // 先构建 byte[] 镜像：一旦 path API 将 out.img 写入源目录，之后的 byte[] 构建
        // 会把这份旧镜像再次打包进自身。
        var expected = Ext4Builder.Build(temp.Path);
        var outputPath = Path.Combine(temp.Path, "out.img");
        Ext4Builder.Build(temp.Path, outputPath);

        // The output image is written inside the source directory and must not be packed
        // into itself, so the two APIs must agree byte for byte.
        // 输出镜像位于源目录内且不得打包进自身，因此两个 API 必须逐字节一致。
        Assert.Equal(expected, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public void Ext4_PathApi_FailurePreservesExistingTarget()
    {
        using var temp = new TempDirectory();
        temp.Write("a.txt", "alpha");
        var outputPath = Path.Combine(temp.Path, "out.img");
        var original = Encoding.ASCII.GetBytes("existing-image");
        File.WriteAllBytes(outputPath, original);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Ext4Builder.Build(temp.Path, outputPath, new Ext4Options { BlockSize = 123 }));

        Assert.Equal(original, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public void Ext4_LargeImage_StreamedToFile_HasExpectedHeaderBlocksAndLength()
    {
        using var temp = new TempDirectory();
        const long size = 32L * 1024 * 1024;
        WritePseudoRandomFile(Path.Combine(temp.Path, "blob.bin"), size, seed: 1234);
        var outputPath = Path.Combine(temp.Path, "out.img");

        Ext4Builder.Build(temp.Path, outputPath);

        using var image = File.OpenRead(outputPath);
        var superblock = new byte[1024];
        image.Position = 1024;
        image.ReadExactly(superblock);

        // Key superblock bytes: s_magic 0xEF53, s_log_block_size=2, s_desc_size=32.
        Assert.Equal(0xEF53, ReadU16(superblock, 0x38));
        Assert.Equal(2u, ReadU32(superblock, 0x18));
        Assert.Equal(32, ReadU16(superblock, 0xFE));

        // Image length must be exactly blocks × block size (SetLength pre-allocation).
        var blocks = (long)ReadU32(superblock, 0x04);
        Assert.Equal(blocks * BlockSize, image.Length);
        Assert.True(blocks > size / BlockSize, "Streamed image is too small for the source payload.");

        // Group descriptor table starts in the block right after the superblock block
        // ((s_first_data_block + 1) × block_size = block 1 for 4 KiB blocks): block bitmap block 2,
        // inode bitmap block 3, inode table block 4.
        // 组描述符表起始于超级块所在块之后的块（(s_first_data_block + 1) × 块大小，4 KiB 块为块 1）：
        // 块位图块 2、inode 位图块 3、inode 表块 4。
        var descriptor = new byte[32];
        image.Position = 1L * BlockSize;
        image.ReadExactly(descriptor);
        Assert.Equal(2u, ReadU32(descriptor, 0));
        Assert.Equal(3u, ReadU32(descriptor, 4));
        Assert.Equal(4u, ReadU32(descriptor, 8));

        // The streamed image is a valid ext4 filesystem: read the 32 MiB payload back.
        using var parser = new Ext4Parser(outputPath);
        using var back = new MemoryStream();
        parser.ReadFile("blob.bin", back);
        back.Position = 0;
        Assert.True(MatchesPseudoRandom(back, size, 1234), "Streamed image data does not match the source payload.");
    }

    [Fact]
    public void Ext4_TruncatedSourceFile_Throws()
    {
        using var temp = new TempDirectory();
        const long size = 32L * 1024 * 1024;
        var sourcePath = Path.Combine(temp.Path, "payload.bin");
        WritePseudoRandomFile(sourcePath, size, seed: 42);
        var outputPath = Path.Combine(Path.GetTempPath(), $"uotan-build-{Guid.NewGuid():N}.img");
        try
        {
            // The wrapper blocks inside the second pass's SetLength — i.e. deterministically after
            // the layout pass recorded the original 32 MiB size and before any source file is opened.
            // 包装流在第二遍的 SetLength 处阻塞——即确定性地发生在布局遍记录原始 32MiB 大小之后、
            // 任何源文件被打开之前。
            using var rawOutput = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var output = new SetLengthSignalStream(rawOutput);
            using var buildTask = Task.Run(() => Ext4Builder.Build(temp.Path, output), TestContext.Current.CancellationToken);

            try
            {
                Assert.True(
                    output.PreSized.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken),
                    "Timed out waiting for the builder to begin the streaming write pass.");

                // The builder is parked in SetLength, so nothing holds the source file open yet:
                // shrink it so the data-copy pass reads fewer bytes than the recorded size.
                // 构建器停在 SetLength 中，此刻尚无句柄占用源文件：将其缩短，
                // 使数据拷贝遍读到的字节数少于已记录大小。
                using (var truncator = new FileStream(sourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    truncator.SetLength(64 * 1024);
                }
            }
            finally
            {
                output.Resume.Set();
            }

            var exception = Record.Exception(() => buildTask.GetAwaiter().GetResult());
            Assert.IsType<EndOfStreamException>(exception);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void Ext4_Build_ToNonSeekableStream_IsSupported()
    {
        using var temp = new TempDirectory();
        temp.Write("a.txt", "alpha");
        var expected = Ext4Builder.Build(temp.Path);

        using var output = new WriteOnlyNonSeekableStream();
        Ext4Builder.Build(temp.Path, output);

        Assert.Equal(expected, output.ToArray());
    }

    // ------------------------------------------------------------------ Erofs

    [Fact]
    public void Erofs_StreamingOutput_MatchesByteArrayApi()
    {
        using var temp = new TempDirectory();
        WriteText(temp, "hello.txt", "hello erofs streaming\n");
        temp.WriteBytes("dir/nested.bin", [0x00, 0x01, 0xFE, 0xFF, 0x80, 0x7F]);
        var big = new byte[96 * 1024];
        for (var i = 0; i < big.Length; i++)
        {
            big[i] = (byte)(i * 17 + 3);
        }

        temp.WriteBytes("dir/big.bin", big);

        // Both flat and compressed layouts must be streamed identically.
        foreach (var compress in new[] { false, true })
        {
            var options = new ErofsOptions { Compress = compress, CompressionLevel = 5 };
            var expected = new ErofsBuilder().Build(temp.Path, options);
            using var stream = new MemoryStream();
            new ErofsBuilder().Build(temp.Path, stream, options);
            var actual = stream.ToArray();

            Assert.Equal(expected.Length, actual.Length);
            // Two separate builds differ only in the superblock build time and UUID.
            Assert.Equal(
                EraseNondeterministicSuperblockFields(expected),
                EraseNondeterministicSuperblockFields(actual));
        }
    }

    [Fact]
    public void Erofs_LargeImage_StreamedToFile_HasExpectedHeaderBlocksAndLength()
    {
        using var temp = new TempDirectory();
        const long size = 32L * 1024 * 1024;
        WritePseudoRandomFile(Path.Combine(temp.Path, "blob.bin"), size, seed: 99);
        var outputPath = Path.Combine(temp.Path, "out.erofs");

        using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            new ErofsBuilder().Build(temp.Path, output, new ErofsOptions { Compress = false });
        }

        using var parser = new ErofsParser(outputPath);
        Assert.Equal(parser.BlockCount * parser.BlockSize, parser.ImageSize);
        Assert.Equal(parser.ImageSize, new FileInfo(outputPath).Length);

        // Superblock key bytes: magic, meta block address = 1, total blocks.
        using (var image = File.OpenRead(outputPath))
        {
            var superblock = new byte[112];
            image.Position = 1024;
            image.ReadExactly(superblock);
            Assert.Equal(0xE0F5E1E2u, BinaryPrimitives.ReadUInt32LittleEndian(superblock));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(40, 4)));
            Assert.Equal((uint)parser.BlockCount, BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(36, 4)));
        }

        var back = parser.ReadFileBytes("blob.bin");
        Assert.True(MatchesPseudoRandom(new MemoryStream(back), size, 99), "Streamed image data does not match the source payload.");
    }

    [Fact]
    public void Erofs_TruncatedSourceFile_Throws()
    {
        using var temp = new TempDirectory();
        const long size = 32L * 1024 * 1024;
        var sourcePath = Path.Combine(temp.Path, "payload.bin");
        WritePseudoRandomFile(sourcePath, size, seed: 7);
        var outputPath = Path.Combine(Path.GetTempPath(), $"uotan-build-{Guid.NewGuid():N}.erofs");
        try
        {
            // Block inside the second pass's SetLength: deterministically after the layout pass
            // recorded the original size and before any source file is opened.
            // 在第二遍的 SetLength 处阻塞：确定性地发生在布局遍记录原始大小之后、源文件被打开之前。
            using var rawOutput = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var output = new SetLengthSignalStream(rawOutput);
            using var buildTask = Task.Run(() =>
                new ErofsBuilder().Build(temp.Path, output, new ErofsOptions { Compress = false }), TestContext.Current.CancellationToken);

            try
            {
                Assert.True(
                    output.PreSized.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken),
                    "Timed out waiting for the builder to begin the streaming write pass.");

                // The builder is parked in SetLength, so nothing holds the source file open yet.
                // 构建器停在 SetLength 中，此刻尚无句柄占用源文件。
                using (var truncator = new FileStream(sourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    truncator.SetLength(64 * 1024);
                }
            }
            finally
            {
                output.Resume.Set();
            }

            var exception = Record.Exception(() => buildTask.GetAwaiter().GetResult());
            Assert.IsType<EndOfStreamException>(exception);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static void WriteText(TempDirectory temp, string relativePath, string content)
    {
        var full = Path.Combine(temp.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }

    private static void WritePseudoRandomFile(string path, long length, int seed)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var random = new Random(seed);
        var chunk = new byte[1 << 20];
        long remaining = length;
        while (remaining > 0)
        {
            var count = (int)Math.Min(chunk.Length, remaining);
            random.NextBytes(chunk.AsSpan(0, count));
            fs.Write(chunk, 0, count);
            remaining -= count;
        }
    }

    private static bool MatchesPseudoRandom(Stream source, long length, int seed)
    {
        var random = new Random(seed);
        var expected = new byte[1 << 20];
        var actual = new byte[1 << 20];
        long remaining = length;
        while (remaining > 0)
        {
            var count = (int)Math.Min(expected.Length, remaining);
            random.NextBytes(expected.AsSpan(0, count));
            var read = 0;
            while (read < count)
            {
                var n = source.Read(actual, read, count - read);
                if (n <= 0)
                {
                    return false;
                }

                read += n;
            }

            if (!expected.AsSpan(0, count).SequenceEqual(actual.AsSpan(0, count)))
            {
                return false;
            }

            remaining -= count;
        }

        return true;
    }

    private static byte[] EraseNondeterministicSuperblockFields(byte[] image)
    {
        const int superblockOffset = 1024;
        var copy = (byte[])image.Clone();
        // s_build_time (8) + s_build_time_nsec (4) at 1024+24.
        Array.Clear(copy, superblockOffset + 24, 12);
        // s_uuid (16) at 1024+48.
        Array.Clear(copy, superblockOffset + 48, 16);
        return copy;
    }

    private static ushort ReadU16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static uint ReadU32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    /// <summary>
    /// A writable stream that cannot seek nor pre-size — used to verify the streaming builder
    /// rejects non-seekable outputs instead of allocating a full image.
    /// <para>不可寻址、不可预分配的只写流——用于验证流式构建器拒绝不可寻址输出而非分配完整镜像。</para>
    /// </summary>
    private sealed class WriteOnlyNonSeekableStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public byte[] ToArray() => _inner.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }

    /// <summary>
    /// Wraps the builder's output stream and parks the build inside the second pass's
    /// <see cref="Stream.SetLength(long)"/> — i.e. deterministically after the layout pass
    /// recorded every source size and before any source file is opened — giving the test a
    /// race-free moment to truncate a source file between the two passes.
    /// <para>包装构建器的输出流，使构建停在第二遍的 <see cref="Stream.SetLength(long)"/>
    /// 处——即确定性地发生在布局遍记录所有源文件大小之后、任何源文件被打开之前——为测试
    /// 提供在两次遍之间截断源文件的无竞争窗口。</para>
    /// </summary>
    private sealed class SetLengthSignalStream : Stream
    {
        private readonly Stream _inner;

        public SetLengthSignalStream(Stream inner)
        {
            _inner = inner;
        }

        /// <summary>Set once the builder pre-sizes the output (second pass began). 第二遍开始预分配时置位。</summary>
        public ManualResetEventSlim PreSized { get; } = new();

        /// <summary>Set by the test to let the build continue. 由测试置位以放行构建。</summary>
        public ManualResetEventSlim Resume { get; } = new();

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value)
        {
            _inner.SetLength(value);
            PreSized.Set();
            if (!Resume.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Timed out waiting for the test to resume the build.");
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }
}