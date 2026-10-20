using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// <see cref="DatWriter"/> progress-throttling tests: the writers coalesce their per-block reports behind a time
/// window, so these pin the guarantees the UI depends on — a terminal report equal to the source length for both the
/// full and the incremental writer, a report count that stays bounded however many blocks are written, and a report
/// stream that never decreases.
/// <para><see cref="DatWriter"/> 进度节流测试：全量与增量写手都会把逐块上报合并到时间窗口之后，因此本组测试锁定界面依赖的三条保证——
/// 两个写手都必须发射与源长度相等的终止报告、报告条数不随块数线性增长、以及上报序列不得递减。</para>
/// </summary>
public class DatWriterProgressThrottleTests
{
    private const int BlockCount = 1000;
    private const int ReportCeiling = 16;

    [Fact]
    public void Write_ManyBlocks_FinalReportEqualsTotal()
    {
        var image = BuildPatternImage(BlockCount);
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var progress = new SyncProgress<(long Processed, long Total)>();

        DatWriter.Write(imagePath, Path.Combine(temp.Path, "output.new.dat"), Path.Combine(temp.Path, "output.transfer.list"),
            new DatWriteOptions(), progress, TestContext.Current.CancellationToken);

        Assert.NotEmpty(progress.Reports);
        Assert.Equal((image.LongLength, image.LongLength), progress.Reports[^1]);
    }

    [Fact]
    public void WriteIncremental_ManyBlocks_FinalReportEqualsTotal()
    {
        var source = BuildPatternImage(BlockCount);
        var target = (byte[])source.Clone();
        var lastBlockOffset = (BlockCount - 1) * DatWriter.BlockSize;
        target.AsSpan(lastBlockOffset, DatWriter.BlockSize).Fill((byte)0x5A);
        using var temp = new TempDirectory();
        var sourcePath = temp.WriteBytes("source.img", source);
        var targetPath = temp.WriteBytes("target.img", target);
        var progress = new SyncProgress<(long Processed, long Total)>();

        DatWriter.WriteIncremental(sourcePath, targetPath, Path.Combine(temp.Path, "output.new.dat"),
            Path.Combine(temp.Path, "output.patch.dat"), Path.Combine(temp.Path, "output.transfer.list"),
            new DatWriteOptions(), progress, TestContext.Current.CancellationToken);

        Assert.NotEmpty(progress.Reports);
        Assert.Equal((target.LongLength, target.LongLength), progress.Reports[^1]);
    }

    [Fact]
    public void Write_ThousandBlocks_ReportCountStaysBelowCeiling()
    {
        var image = BuildPatternImage(BlockCount);
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var progress = new SyncProgress<(long Processed, long Total)>();

        DatWriter.Write(imagePath, Path.Combine(temp.Path, "output.new.dat"), Path.Combine(temp.Path, "output.transfer.list"),
            new DatWriteOptions(), progress, TestContext.Current.CancellationToken);

        Assert.True(progress.Reports.Count <= ReportCeiling,
            $"Throttling leaked {progress.Reports.Count} reports for {BlockCount} blocks; the ceiling is {ReportCeiling}.");
        Assert.True(progress.Reports.Count < BlockCount, "Report count must stay far below the block count.");
        AssertMonotonic(progress.Reports);
        Assert.Equal((image.LongLength, image.LongLength), progress.Reports[^1]);
    }

    [Fact]
    public void Write_ProgressNeverDecreases_AcrossThrottledReports()
    {
        var image = BuildPatternImage(BlockCount * 2);
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var progress = new SyncProgress<(long Processed, long Total)>();

        DatWriter.Write(imagePath, Path.Combine(temp.Path, "output.new.dat"), Path.Combine(temp.Path, "output.transfer.list"),
            new DatWriteOptions(), progress, TestContext.Current.CancellationToken);

        AssertMonotonic(progress.Reports);
        Assert.All(progress.Reports, report => Assert.True(report.Total == image.LongLength, "Total must stay fixed."));
        Assert.Equal(image.LongLength, progress.Reports[^1].Processed);
    }

    private static void AssertMonotonic(IReadOnlyList<(long Processed, long Total)> reports)
    {
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].Processed >= reports[i - 1].Processed, "Progress must not decrease.");
        }
    }

    private static byte[] BuildPatternImage(int blockCount)
    {
        var data = new byte[blockCount * DatWriter.BlockSize];
        for (var block = 0; block < blockCount; block++)
        {
            var offset = block * DatWriter.BlockSize;
            for (var i = 0; i < DatWriter.BlockSize; i++)
            {
                data[offset + i] = (byte)((i * 31 + block) & 0xFF);
            }
        }

        return data;
    }
}