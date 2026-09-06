using BsDiff;
using UotanStudio.Firmware.Dat;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// DAT incremental OTA tests: verify the v4 incremental command path (stash/move/diff) of
/// <see cref="DatReader"/>, including stash-referencing moves, multi-pair diff range sets, v3 diff
/// compatibility, and the missing-input error cases. Patches are produced with the project's BsDiff
/// generator (<see cref="BinaryPatch"/>).
/// <para>DAT 增量 OTA 测试：校验 <see cref="DatReader"/> 的 v4 增量命令路径（stash/move/diff），包括引用暂存的
/// move、diff 双范围集、v3 diff 兼容以及缺少输入的错误情形。补丁用项目自带的 BsDiff 生成器
/// （<see cref="BinaryPatch"/>）生成。</para>
/// </summary>
public class DatIncrementalTests
{
    private const int BlockSize = DatReader.BlockSize;

    [Fact]
    public void Parse_MoveAndDiffCommands_PopulateSourceRanges()
    {
        using var reader = new StringReader("""
            4
            8
            0
            0
            move 2,0,1,2,3 2,4,5,6,7
            diff 1,1,2 1,3,4
            """);
        var list = TransferListParser.Parse(reader);

        var move = list.Commands[0];
        Assert.Equal(TransferListCommandKind.Move, move.Kind);
        Assert.Equal(new[] { new BlockRange(0, 1), new BlockRange(2, 3) }, move.SourceRanges);
        Assert.Equal(new[] { new BlockRange(4, 5), new BlockRange(6, 7) }, move.Ranges);

        var diff = list.Commands[1];
        Assert.Equal(TransferListCommandKind.Diff, diff.Kind);
        Assert.Equal(new[] { new BlockRange(1, 2) }, diff.SourceRanges);
        Assert.Equal(new[] { new BlockRange(3, 4) }, diff.Ranges);
    }

    [Fact]
    public void Parse_V3DiffSingleRangeSet_SourceEqualsTarget()
    {
        using var reader = new StringReader("3\n4\n0\n0\ndiff 1,2,4\n");
        var list = TransferListParser.Parse(reader);

        var diff = list.Commands[0];
        Assert.Equal(TransferListCommandKind.Diff, diff.Kind);
        Assert.Equal(new[] { new BlockRange(2, 4) }, diff.SourceRanges);
        Assert.Equal(new[] { new BlockRange(2, 4) }, diff.Ranges);
    }

    [Fact]
    public void Unpack_IncrementalList_AllCommands_IsByteExact()
    {
        const int blockCount = 8;
        var sourceImage = BuildRandomImage(blockCount, 42);
        var newBlockA = BuildPatternBlock(0x5A);
        var newBlockB = BuildPatternBlock(0xC3);
        var patchedBlock = BuildPatternBlock(0x9E);

        var targetImage = new byte[blockCount * BlockSize];
        WriteBlock(targetImage, 0, newBlockA);
        WriteBlock(targetImage, 1, GetBlock(sourceImage, 2));
        WriteBlock(targetImage, 3, patchedBlock);
        WriteBlock(targetImage, 4, GetBlock(sourceImage, 0));
        WriteBlock(targetImage, 5, newBlockB);
        // 块 2、6、7 保持为零（free/zero/erase）。
        // Blocks 2, 6, 7 stay zero (free/zero/erase).

        using var temp = new TempDirectory();
        var listPath = temp.Write("incremental.transfer.list", """
            4
            8
            1
            1
            new 1,0,1
            move 1,2,3 1,1,2
            diff 1,1,2 1,3,4
            stash 1,0,1 1,0,1
            move 1,0,1 1,4,5
            new 1,5,6
            zero 1,6,7
            erase 1,7,8
            free 1,2,3
            """);
        var newDataPath = temp.WriteBytes("incremental.new.dat", Concat(newBlockA, newBlockB));
        var patchDataPath = temp.WriteBytes("incremental.patch.dat", CreateBsdiffPatch(GetBlock(sourceImage, 1), patchedBlock));
        var sourcePath = temp.WriteBytes("incremental.source.img", sourceImage);
        var outputPath = Path.Combine(temp.Path, "incremental.img");

        DatReader.Unpack(listPath, newDataPath, patchDataPath, sourcePath, outputPath);

        Assert.Equal(targetImage, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public void Unpack_MoveAfterStash_ReferencesStashedSource()
    {
        var sourceImage = BuildRandomImage(4, 7);
        using var temp = new TempDirectory();

        // stash 以「源范围集字符串」作为暂存 id；后续 move 的源 token 与之相同即引用该暂存。
        // The stash id is the source range-set string; the following move's source token matches it.
        var listPath = temp.Write("stash-move.transfer.list", """
            4
            2
            1
            1
            stash 1,0,1 1,0,1
            move 1,0,1 1,1,2
            """);
        var newDataPath = temp.WriteBytes("stash-move.new.dat", []);
        var sourcePath = temp.WriteBytes("stash-move.source.img", sourceImage);
        var outputPath = Path.Combine(temp.Path, "stash-move.img");

        DatReader.Unpack(listPath, newDataPath, null, sourcePath, outputPath);

        var expected = new byte[2 * BlockSize];
        WriteBlock(expected, 1, GetBlock(sourceImage, 0));
        Assert.Equal(expected, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public void Unpack_Diff_DoubleRangeSet_AppliesPatchPerPair()
    {
        const int blockCount = 8;
        var sourceImage = BuildRandomImage(blockCount, 11);
        var patchedA = BuildPatternBlock(0x11);
        var patchedB = BuildPatternBlock(0x22);

        var targetImage = new byte[blockCount * BlockSize];
        WriteBlock(targetImage, 4, patchedA);
        WriteBlock(targetImage, 6, patchedB);

        // 每对 (src, dst) 区间在补丁流中按序对应一个补丁条目。
        // Each (src, dst) range pair maps to one patch entry, in order.
        var patch = Concat(
            CreateBsdiffPatch(GetBlock(sourceImage, 0), patchedA),
            CreateBsdiffPatch(GetBlock(sourceImage, 2), patchedB));

        using var temp = new TempDirectory();
        var listPath = temp.Write("diff-pairs.transfer.list", """
            4
            8
            0
            0
            diff 2,0,1,2,3 2,4,5,6,7
            """);
        var newDataPath = temp.WriteBytes("diff-pairs.new.dat", []);
        var patchDataPath = temp.WriteBytes("diff-pairs.patch.dat", patch);
        var sourcePath = temp.WriteBytes("diff-pairs.source.img", sourceImage);
        var outputPath = Path.Combine(temp.Path, "diff-pairs.img");

        DatReader.Unpack(listPath, newDataPath, patchDataPath, sourcePath, outputPath);

        Assert.Equal(targetImage, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public void Unpack_V3Diff_SingleRangeSet_SourceEqualsTarget()
    {
        const int blockCount = 2;
        var sourceImage = BuildRandomImage(blockCount, 3);
        var patchedBlock = BuildPatternBlock(0x77);

        var targetImage = new byte[blockCount * BlockSize];
        WriteBlock(targetImage, 1, patchedBlock);

        using var temp = new TempDirectory();
        var listPath = temp.Write("v3.transfer.list", """
            3
            2
            0
            0
            diff 1,1,2
            """);
        var newDataPath = temp.WriteBytes("v3.new.dat", []);
        var patchDataPath = temp.WriteBytes("v3.patch.dat", CreateBsdiffPatch(GetBlock(sourceImage, 1), patchedBlock));
        var sourcePath = temp.WriteBytes("v3.source.img", sourceImage);
        var outputPath = Path.Combine(temp.Path, "v3.img");

        DatReader.Unpack(listPath, newDataPath, patchDataPath, sourcePath, outputPath);

        Assert.Equal(targetImage, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public void Unpack_DiffCommand_WithoutPatchData_Throws()
    {
        using var temp = new TempDirectory();
        var listPath = temp.Write("x.transfer.list", "4\n3\n0\n0\ndiff 1,0,1 1,2,3\n");
        var newDataPath = temp.WriteBytes("x.new.dat", []);
        var sourcePath = temp.WriteBytes("x.source.img", new byte[4 * BlockSize]);

        Assert.Throws<ArgumentException>(() => DatReader.Unpack(listPath, newDataPath, null, sourcePath, Path.Combine(temp.Path, "out.img")));
    }

    [Fact]
    public void Unpack_MoveCommand_WithMissingSourceFile_ThrowsFileNotFound()
    {
        using var temp = new TempDirectory();
        var listPath = temp.Write("x.transfer.list", "4\n3\n0\n0\nmove 1,0,1 1,2,3\n");
        var newDataPath = temp.WriteBytes("x.new.dat", []);
        var missingSource = Path.Combine(temp.Path, "missing.img");

        Assert.Throws<FileNotFoundException>(() => DatReader.Unpack(listPath, newDataPath, null, missingSource, Path.Combine(temp.Path, "out.img")));
    }

    [Fact]
    public void Unpack_Diff_ImgdiffMagic_ThrowsInvalidData()
    {
        using var temp = new TempDirectory();
        var listPath = temp.Write("x.transfer.list", "4\n3\n0\n0\ndiff 1,0,1 1,2,3\n");
        var newDataPath = temp.WriteBytes("x.new.dat", []);
        var sourcePath = temp.WriteBytes("x.source.img", new byte[4 * BlockSize]);
        var patchDataPath = temp.WriteBytes("x.patch.dat", "IMGDIFF2"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => DatReader.Unpack(listPath, newDataPath, patchDataPath, sourcePath, Path.Combine(temp.Path, "out.img")));
    }

    private static byte[] BuildRandomImage(int blockCount, int seed)
    {
        var random = new Random(seed);
        var data = new byte[blockCount * BlockSize];
        random.NextBytes(data);
        return data;
    }

    private static byte[] BuildPatternBlock(byte seed)
    {
        var data = new byte[BlockSize];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 7 + seed) & 0xFF);
        }

        return data;
    }

    private static byte[] GetBlock(byte[] image, int block)
    {
        var data = new byte[BlockSize];
        Array.Copy(image, block * BlockSize, data, 0, BlockSize);
        return data;
    }

    private static void WriteBlock(byte[] image, int block, byte[] data)
    {
        Array.Copy(data, 0, image, block * BlockSize, BlockSize);
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var result = new byte[arrays.Sum(a => a.Length)];
        var offset = 0;
        foreach (var array in arrays)
        {
            Array.Copy(array, 0, result, offset, array.Length);
            offset += array.Length;
        }

        return result;
    }

    private static byte[] CreateBsdiffPatch(byte[] oldData, byte[] newData)
    {
        using var patchStream = new MemoryStream();
        BinaryPatch.Create(oldData, newData, patchStream);
        return patchStream.ToArray();
    }
}