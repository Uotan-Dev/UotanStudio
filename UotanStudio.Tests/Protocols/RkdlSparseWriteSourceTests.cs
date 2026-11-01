using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Firmware.BlockImage.Sparse.DataProviders;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Verifies the streaming byte sources the sparse replay writes through: a RAW chunk's provider is read forward only,
/// a FILL chunk repeats its little-endian pattern without materialising the expanded block, and the progress wrapper
/// turns per-chunk counts into one cumulative total.
/// <para>验证稀疏回放所用的流式字节源：RAW 数据块的数据提供器只向前读取，FILL 数据块重复其小端模式而不展开整块，进度包装
/// 把按块计数换算为单一累计总量。</para>
/// </summary>
public class RkdlSparseWriteSourceTests
{
    [Fact]
    public void FromFill_Read_RepeatsTheLittleEndianPattern()
    {
        var bytes = new byte[] { 0x44, 0x33, 0x22, 0x11, 0x44, 0x33, 0x22, 0x11 };
        using var source = SparseWriteSource.FromFill(0x11223344, bytes.Length);

        var buffer = new byte[bytes.Length];

        Assert.Equal(bytes.Length, source.Read(buffer, 0, buffer.Length));
        Assert.Equal(bytes, buffer);
        Assert.Equal(0, source.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void FromFill_ShortRead_ContinuesThePatternAtTheNextOffset()
    {
        using var source = SparseWriteSource.FromFill(0x11223344, 6);
        var first = new byte[3];
        var second = new byte[3];

        Assert.Equal(3, source.Read(first, 0, first.Length));
        Assert.Equal(3, source.Read(second, 0, second.Length));

        Assert.Equal(new byte[] { 0x44, 0x33, 0x22 }, first);
        Assert.Equal(new byte[] { 0x11, 0x44, 0x33 }, second);
    }

    [Fact]
    public void FromProvider_Read_AdvancingOffsets()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6 };
        using var provider = new MemoryDataProvider(payload);
        using var source = SparseWriteSource.FromProvider(provider);

        var buffer = new byte[4];
        Assert.Equal(4, source.Read(buffer, 0, 4));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer);

        var tail = new byte[4];
        Assert.Equal(2, source.Read(tail, 0, 4));
        Assert.Equal(new byte[] { 5, 6, 0, 0 }, tail);

        Assert.Equal(0, source.Read(tail, 0, 4));
    }

    [Fact]
    public void FromProvider_Length_ReportsTheProviderLength()
    {
        using var provider = new MemoryDataProvider(new byte[512]);
        using var source = SparseWriteSource.FromProvider(provider);

        Assert.Equal(512, source.Length);
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public void Position_Set_ThrowsBecauseTheSourceIsSequential()
    {
        using var source = SparseWriteSource.FromFill(0, 4);

        Assert.Throws<NotSupportedException>(() => source.Position = 2);
        Assert.False(source.CanSeek);
        Assert.False(source.CanWrite);
    }

    [Fact]
    public void OffsetProgress_Report_AddsTheBaseToEveryValue()
    {
        var observed = new List<long>();
        var progress = new OffsetProgress(new RecordingProgress(observed), offset: 4096);

        progress.Report(512);
        progress.Report(1024);

        Assert.Equal(new long[] { 4608, 5120 }, observed);
    }

    private sealed class RecordingProgress(List<long> observed) : IProgress<long>
    {
        public void Report(long value) => observed.Add(value);
    }
}