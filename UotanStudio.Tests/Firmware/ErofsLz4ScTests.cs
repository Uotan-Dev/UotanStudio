using System.Buffers.Binary;
using System.Diagnostics;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the EROFS LZ4SC read path: a semi-compressed pcluster stores its first logical cluster raw
/// and the remaining logical clusters as one LZ4 stream, indexed by a PLAIN head lcluster followed by
/// NONHEAD lclusters carrying the CBLKCNT block count. The synthetic corpus rewrites a valid builder
/// image into that layout so the decoder contract is exercised without a writer-side implementation.
/// <para>守护 EROFS LZ4SC 读取路径：半压缩 pcluster 的首个逻辑簇以明文存放，其余逻辑簇合成一条 LZ4 流，
/// 索引由 PLAIN head lcluster 加携带 CBLKCNT 块数的 NONHEAD lcluster 组成。合成语料把合法的打包器镜像
/// 改写为该布局，从而在没有写侧实现的情况下验证解码契约。</para>
/// </summary>
public sealed class ErofsLz4ScTests
{
    private const int BlockSize = 4096;
    private const int FileSize = 128 * 1024;
    private const int LclusterSize = 4096;
    private const byte LclusterTypePlain = 0;
    private const byte LclusterTypeNonHead = 2;

    [Fact]
    public void ReadFile_SemiClusteredLz4Pclusters_DecodesRawHeadAndLz4Tail()
    {
        var payload = Payload();
        var image = RewriteAsSemiClusters(BuildBaseImage(payload, ErofsCompressionAlgorithm.Lz4), ErofsAlgorithm.Lz4, bigPcluster2: true);
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void EnumerateFiles_SemiClusteredLz4Pclusters_KeepsInodeAndSizeIdentity()
    {
        var payload = Payload();
        var image = RewriteAsSemiClusters(BuildBaseImage(payload, ErofsCompressionAlgorithm.Lz4), ErofsAlgorithm.Lz4, bigPcluster2: true);
        using var parser = new ErofsParser(image);
        var entry = Assert.Single(parser.EnumerateFiles());
        Assert.Equal("data.bin", entry.Path);
        Assert.Equal(payload.LongLength, entry.Size);
    }

    [Fact]
    public void DumpIndexClusters_SemiClusteredLz4Pclusters_AlternatesPlainHeadAndNonHead()
    {
        var payload = Payload();
        var image = RewriteAsSemiClusters(BuildBaseImage(payload, ErofsCompressionAlgorithm.Lz4), ErofsAlgorithm.Lz4, bigPcluster2: true);
        using var parser = new ErofsParser(image);
        var rows = parser.DumpIndexClusters("data.bin");
        Assert.Equal(FileSize / LclusterSize, rows.Count);
        for (var lcn = 0; lcn < rows.Count; lcn++)
        {
            Assert.Equal(lcn % 2 == 0 ? LclusterTypePlain : LclusterTypeNonHead, rows[lcn].Type);
        }
        Assert.All(rows.Where(static row => row.Type == LclusterTypeNonHead), row => Assert.Equal(1u, row.Delta0));
    }

    [Fact]
    public void ReadFile_SemiClusteredPclusterMissingBigPclusterAdvise_ReportsMissingEncodedTail()
    {
        var payload = Payload();
        var image = RewriteAsSemiClusters(BuildBaseImage(payload, ErofsCompressionAlgorithm.Lz4), ErofsAlgorithm.Lz4, bigPcluster2: false);
        using var parser = new ErofsParser(image);
        var ex = Assert.Throws<FileSystemCorruptedException>(() => parser.ReadFileBytes("data.bin"));
        Assert.Contains("no encoded tail", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFile_SemiClusteredPclusterWithCorruptedTail_ThrowsInvalidData()
    {
        var payload = Payload();
        var image = RewriteAsSemiClusters(BuildBaseImage(payload, ErofsCompressionAlgorithm.Lz4), ErofsAlgorithm.Lz4,
            bigPcluster2: true, corruptStream: true);
        using var parser = new ErofsParser(image);
        Assert.Throws<FileSystemCorruptedException>(() => parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public async Task BuildImage_NativeMkfsLz4scWhenAvailable_MatchesExtractorAndManagedParser()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1")
        {
            Assert.Skip("Set UOTAN_NATIVE_TOOLS=1 on Windows with Alpine WSL erofs-utils to run the LZ4SC cross-check.");
        }
        var probe = await RunWsl("which", "mkfs.erofs");
        if (probe.ExitCode != 0)
        {
            Assert.Skip("Alpine erofs-utils are unavailable (UOTAN_NATIVE_TOOLS interop).");
        }

        using var temp = new TempDirectory();
        var sourceDirectory = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(sourceDirectory);
        var payload = Payload();
        await File.WriteAllBytesAsync(Path.Combine(sourceDirectory, "data.bin"), payload, TestContext.Current.CancellationToken);
        var imagePath = Path.Combine(temp.Path, "lz4sc.img");
        var mkfs = await RunWsl("mkfs.erofs", "-zlz4sc", WslPath(imagePath), WslPath(sourceDirectory));
        if (mkfs.ExitCode != 0)
        {
            Assert.Skip("Alpine mkfs.erofs does not accept the -zlz4sc semi-cluster compressor (UOTAN_NATIVE_TOOLS interop).");
        }
        using var parser = new ErofsParser(imagePath);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
        var extractedDirectory = Path.Combine(temp.Path, "extracted");
        Directory.CreateDirectory(extractedDirectory);
        var extract = await RunWsl("extract.erofs", "-i", WslPath(imagePath), "-x", "-o", WslPath(extractedDirectory), "-f");
        if (extract.ExitCode != 0)
        {
            Assert.Skip("Alpine extract.erofs cannot read LZ4SC images (UOTAN_NATIVE_TOOLS interop).");
        }
        var extractedFile = Assert.Single(Directory.GetFiles(extractedDirectory, "data.bin", SearchOption.AllDirectories));
        Assert.Equal(payload, await File.ReadAllBytesAsync(extractedFile, TestContext.Current.CancellationToken));
    }

    private static byte[] Payload()
    {
        var bytes = new byte[FileSize];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)('a' + i % 251);
        }
        return bytes;
    }

    private static byte[] BuildBaseImage(byte[] payload, ErofsCompressionAlgorithm algorithm)
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        return new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = algorithm,
            UseCompactedIndexes = false,
            TailPacking = false,
            CompressedTailPacking = false,
            Fragments = false,
            Dedup = false,
            SuperblockChecksum = false,
        });
    }

    private static byte[] RewriteAsSemiClusters(byte[] baseImage, ErofsAlgorithm algorithm, bool bigPcluster2, bool corruptStream = false)
    {
        ulong nid;
        long size;
        byte[] payload;
        using (var probe = new ErofsParser(baseImage))
        {
            var entry = Assert.Single(probe.EnumerateFiles());
            nid = entry.Nid;
            size = entry.Size;
            payload = probe.ReadFileBytes(entry.Path);
        }

        Assert.True(size % (2L * LclusterSize) == 0);
        var blockBits = baseImage[1024 + 12];
        var blockSize = 1 << (int)blockBits;
        Assert.Equal(BlockSize, blockSize);
        var metaBlkaddr = BinaryPrimitives.ReadUInt32LittleEndian(baseImage.AsSpan(1024 + 40, 4));
        var inodeLoc = (long)metaBlkaddr * blockSize + (long)nid * ErofsSuperblock.CompactInodeSize;
        var inode = ErofsInode.FromBytes(baseImage.AsSpan((int)inodeLoc, ErofsInode.ExtendedSize));
        Assert.True(inode.IsCompressed);
        Assert.Equal(ErofsInode.DataLayoutCompressed, inode.DataLayout);
        var headerOffset = (inodeLoc + inode.InlineDataOffset + 7) & ~7;
        Assert.Equal((byte)algorithm, baseImage[(int)headerOffset + 6]);
        var clusterBits = baseImage[(int)headerOffset + 7] & ZErofsMapHeader.LclusterBitsMask;
        var lclusterSize = 1 << (int)(blockBits + clusterBits);
        Assert.Equal(LclusterSize, lclusterSize);
        var totalIdx = (int)(size / lclusterSize);
        var indexBase = headerOffset + ZErofsMapHeader.Size + 8;

        var dataBase = (long)(baseImage.Length + blockSize - 1) / blockSize * blockSize;
        var image = baseImage.ToArray();
        var cursor = dataBase;
        for (var pair = 0; pair < totalIdx / 2; pair++)
        {
            var headFileOffset = (long)pair * 2 * lclusterSize;
            var tailFileOffset = headFileOffset + lclusterSize;
            var stream = new byte[ErofsCompression.MaximumOutputSize(algorithm, lclusterSize)];
            var streamLength = ErofsCompression.AlgorithmCompress(algorithm, payload, (int)tailFileOffset, lclusterSize,
                stream, 0, stream.Length, 1);
            Assert.InRange(streamLength, 1, lclusterSize - 1);
            var blocks = 1 + (streamLength + blockSize - 1) / blockSize;
            var required = cursor + (long)blocks * blockSize;
            if (image.Length < required)
            {
                Array.Resize(ref image, (int)required);
            }
            payload.AsSpan((int)headFileOffset, lclusterSize).CopyTo(image.AsSpan((int)cursor, lclusterSize));
            var streamStart = (int)(cursor + lclusterSize);
            var streamRoom = (int)((long)blocks * blockSize - lclusterSize);
            if (corruptStream)
            {
                image.AsSpan(streamStart, streamRoom).Fill(0xFF);
            }
            else
            {
                image.AsSpan(streamStart, streamRoom).Clear();
                stream.AsSpan(0, streamLength).CopyTo(image.AsSpan(streamStart + streamRoom - streamLength, streamLength));
            }

            var headEntry = indexBase + (long)pair * 2 * ZErofsLclusterIndex.Size;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)headEntry), LclusterTypePlain);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)headEntry + 2), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)headEntry + 4), (uint)(cursor / blockSize));
            var tailEntry = headEntry + ZErofsLclusterIndex.Size;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)tailEntry), LclusterTypeNonHead);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)tailEntry + 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)tailEntry + 4),
                (ushort)(ZErofsLclusterIndex.LiD0CblkCnt | (uint)blocks));
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)tailEntry + 6), 1);
            cursor = required;
        }

        if (image.Length < cursor)
        {
            Array.Resize(ref image, (int)cursor);
        }

        var advise = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)headerOffset + 4, 2));
        advise = bigPcluster2
            ? (ushort)(advise | ZErofsMapHeader.AdviseBigPcluster2)
            : (ushort)(advise & ~ZErofsMapHeader.AdviseBigPcluster2);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)headerOffset + 4, 2), advise);

        var incompat = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 80, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(1024 + 80, 4), incompat | ErofsSuperblock.IncompatBigPcluster);
        var algs = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(1024 + 84, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(1024 + 84, 2), (ushort)(algs | (1 << (int)algorithm)));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(1024 + 36, 4), (uint)(image.Length / blockSize));
        return image;
    }

    private static string WslPath(string path)
        => "/mnt/" + char.ToLowerInvariant(path[0]) + "/" + path[3..].Replace('\\', '/');

    private static async Task<(int ExitCode, string Output)> RunWsl(params string[] arguments)
    {
        var start = new ProcessStartInfo("wsl.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-d", "Alpine", "--" }.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }
}