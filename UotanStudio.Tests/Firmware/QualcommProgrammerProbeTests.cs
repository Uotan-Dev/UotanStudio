using UotanStudio.Firmware.Executable.Qualcomm;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Cases for the offline shape probe a loader row uses to accept ELF, MBN and multi-ELF programmers.
/// <para>loader 行用来接受 ELF、MBN 与多 ELF programmer 的离线形态探测的用例。</para>
/// </summary>
/// <remarks>
/// The scattered layouts matter most: a newer programmer is a sequence of ELF components separated by padding,
/// whose first component does not have to start at offset zero, so a probe that only looks at offset zero would
/// call it unknown and the row would reject a usable image.
/// <para>零散布局最关键：较新的 programmer 是被填充隔开的一串 ELF 组件，其首个组件不必从偏移零处开始，因此只看偏移零的
/// 探测会把它判为未知，该行就会拒绝一个可用的镜像。</para>
/// </remarks>
public sealed class QualcommProgrammerProbeTests
{
    private const int ElfHeaderSize = 52;

    [Fact]
    public void Detect_SingleElfAtOrigin_ReportsElfWithOneComponent()
    {
        using var stream = new MemoryStream(BuildElf());
        var shape = QualcommProgrammerProbe.Detect(stream, TestContext.Current.CancellationToken);

        Assert.Equal(QualcommProgrammerKind.Elf, shape.Kind);
        Assert.Equal(1, shape.ComponentCount);
    }

    [Fact]
    public void Detect_TwoContiguousElfComponents_ReportsMelfWithTwoComponents()
    {
        using var stream = new MemoryStream([.. BuildElf(), .. BuildElf()]);
        var shape = QualcommProgrammerProbe.Detect(stream, TestContext.Current.CancellationToken);

        Assert.Equal(QualcommProgrammerKind.Melf, shape.Kind);
        Assert.Equal(2, shape.ComponentCount);
    }

    [Fact]
    public void Detect_ScatteredComponentsBehindPrefixAndPadding_ReportsMelf()
    {
        var image = new byte[4096];
        var first = 512;
        var second = 2048;
        BuildElf().CopyTo(image, first);
        BuildElf().CopyTo(image, second);

        using var stream = new MemoryStream(image);
        var shape = QualcommProgrammerProbe.Detect(stream, TestContext.Current.CancellationToken);

        Assert.Equal(QualcommProgrammerKind.Melf, shape.Kind);
        Assert.Equal(2, shape.ComponentCount);
    }

    [Fact]
    public void Detect_SingleComponentBehindPrefix_StillReportsAContainer()
    {
        var image = new byte[1024];
        BuildElf().CopyTo(image, 640);

        using var stream = new MemoryStream(image);
        var shape = QualcommProgrammerProbe.Detect(stream, TestContext.Current.CancellationToken);

        Assert.Equal(QualcommProgrammerKind.Melf, shape.Kind);
        Assert.Equal(1, shape.ComponentCount);
    }

    [Fact]
    public void Detect_StandaloneMbn_ReportsMbnWithoutComponents()
    {
        using var stream = new MemoryStream(BuildMbn());
        var shape = QualcommProgrammerProbe.Detect(stream, TestContext.Current.CancellationToken);

        Assert.Equal(QualcommProgrammerKind.Mbn, shape.Kind);
        Assert.Equal(0, shape.ComponentCount);
    }

    [Fact]
    public void Detect_UnrecognizedBytes_ReportsUnknown()
    {
        using var stream = new MemoryStream("LDR "u8.ToArray());
        var shape = QualcommProgrammerProbe.Detect(stream, TestContext.Current.CancellationToken);

        Assert.Equal(QualcommProgrammerKind.Unknown, shape.Kind);
        Assert.Equal(0, shape.ComponentCount);
    }

    [Fact]
    public void Detect_RestoresPositionOfTheStream()
    {
        using var stream = new MemoryStream([.. new byte[64], .. BuildElf()]);
        stream.Position = 7;

        _ = QualcommProgrammerProbe.Detect(stream, TestContext.Current.CancellationToken);

        Assert.Equal(7, stream.Position);
    }

    [Fact]
    public void Detect_UnseekableStream_IsRefused()
    {
        using var stream = new ForwardOnlyStream(BuildElf());

        Assert.Throws<ArgumentException>(() =>
            QualcommProgrammerProbe.Detect(stream, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Builds the smallest ELF header the reader accepts: 32-bit little endian, no program headers and no section
    /// headers, which is enough because the probe only needs the component boundaries.
    /// <para>构造读取器能接受的最小 ELF 头：32 位小端、无程序头与节头，探针只需要组件边界，这就够了。</para>
    /// </summary>
    private static byte[] BuildElf()
    {
        var header = new byte[ElfHeaderSize];
        "\u007fELF"u8.CopyTo(header);
        header[4] = 1;
        header[5] = 1;
        header[6] = 1;
        header[16] = 2;
        header[18] = 40;
        header[20] = 1;
        header[40] = ElfHeaderSize;
        return header;
    }

    /// <summary>
    /// Builds a minimal version 3 MBN: a 40-byte header whose declared length and hash length both cover the trailing
    /// hash table, which is every bound the reader checks before it reports a layout.
    /// <para>构造最小版本 3 MBN：40 字节头，其声明长度与哈希长度都正好覆盖尾部哈希表，这正是读取器在给出布局前检查的
    /// 全部边界。</para>
    /// </summary>
    private static byte[] BuildMbn()
    {
        const int hashLength = 32;
        var image = new byte[40 + hashLength];
        BitConverter.TryWriteBytes(image.AsSpan(4), 3u);
        BitConverter.TryWriteBytes(image.AsSpan(16), (uint)hashLength);
        BitConverter.TryWriteBytes(image.AsSpan(20), (uint)hashLength);
        return image;
    }

    /// <summary>
    /// A stream that can be read but not seeked, standing in for a pipe a caller might hand the probe by mistake.
    /// <para>可读但不可寻址的流，用于模拟调用方可能误交给探针的管道。</para>
    /// </summary>
    private sealed class ForwardOnlyStream(byte[] payload) : Stream
    {
        private readonly MemoryStream _inner = new(payload);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}