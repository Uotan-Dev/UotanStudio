using UotanStudio.Firmware.CompoundFile;
using UotanStudio.Firmware.Radio.Qcn;

using Xunit;

namespace UotanStudio.Tests.Firmware;

public class CompoundFileWriterTests
{
    private static string TempPath(string name)
    {
        return Path.Combine(Path.GetTempPath(), $"uotan-cfb-{Guid.NewGuid():N}-{name}");
    }

    [Fact]
    public async Task BuildAsync_LargeAndMiniStreams_RoundTripsThroughReader()
    {
        var path = TempPath("container.cfb");
        try
        {
            var large = new byte[9000];
            for (var i = 0; i < large.Length; i++)
            {
                large[i] = (byte)(i * 7);
            }

            var small = "UotanStudio mini stream payload"u8.ToArray();
            var tiny = new byte[] { 0x01, 0x02, 0x03 };
            var nested = new byte[5000];
            Random.Shared.NextBytes(nested);
            var streams = new List<CompoundFileStreamWriterEntry>
            {
                new("NV_NUMBERED_ITEMS/NV_ITEM_ARRAY", large),
                new("File_Version", small),
                new("00000000/default/NV_ITEM_ARRAY", tiny),
                new("Nested/Deep/Stream", nested),
            };
            await CompoundFileWriter.BuildAsync(path, streams, TestContext.Current.CancellationToken);
            using var reader = await CompoundFileReader.OpenAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(4, reader.Entries.Count(entry => entry.Type == CompoundFileEntryType.Stream));
            foreach (var stream in streams)
            {
                var entry = reader.TryGetEntryByPath(stream.Path);
                Assert.NotNull(entry);
                Assert.Equal(CompoundFileEntryType.Stream, entry!.Type);
                Assert.Equal(stream.Data.Length, entry.StreamSize);
                await using var opened = reader.OpenStream(entry);
                var buffer = new byte[stream.Data.Length];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = await opened.ReadAsync(buffer.AsMemory(read), TestContext.Current.CancellationToken);
                    Assert.True(n > 0);
                    read += n;
                }

                Assert.True(buffer.AsSpan().SequenceEqual(stream.Data.Span), $"Stream {stream.Path} content mismatched.");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BuildAsync_DuplicatePath_Throws()
    {
        var path = TempPath("dup.cfb");
        try
        {
            var streams = new List<CompoundFileStreamWriterEntry>
            {
                new("A", new byte[] { 1 }),
                new("a", new byte[] { 2 }),
            };
            await Assert.ThrowsAsync<ArgumentException>(() =>
                CompoundFileWriter.BuildAsync(path, streams, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BuildAsync_EmptyStreamSet_Throws()
    {
        var path = TempPath("empty.cfb");
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                CompoundFileWriter.BuildAsync(path, [], TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BuildAsync_OversizedStream_Throws()
    {
        var path = TempPath("big.cfb");
        try
        {
            var streams = new List<CompoundFileStreamWriterEntry>
            {
                new("TooBig", new byte[CompoundFileStreamWriterEntry.MaxStreamSize + 1]),
            };
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                CompoundFileWriter.BuildAsync(path, streams, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class QcnBuilderTests
{
    private static string TempPath(string name)
    {
        return Path.Combine(Path.GetTempPath(), $"uotan-qcn-{Guid.NewGuid():N}-{name}");
    }

    private static QcnNvItem MakeItem(int ordinal, ushort id, byte seed)
    {
        var data = new byte[QcnNvItem.DataLength];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(seed + i);
        }

        return QcnNvItem.Create(ordinal, id, flags: 0, data, "NV_NUMBERED_ITEMS/NV_ITEM_ARRAY", ordinal);
    }

    [Fact]
    public async Task BuildAsync_Records_RoundTripsThroughQcnReader()
    {
        var path = TempPath("built.qcn");
        try
        {
            var items = new List<QcnNvItem> { MakeItem(0, 10, 1), MakeItem(1, 4471, 2), MakeItem(2, 187, 3) };
            await QcnBuilder.BuildAsync(path, items, cancellationToken: TestContext.Current.CancellationToken);
            var parsed = await QcnReader.ParseAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(items.Count, parsed.Items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                Assert.Equal(items[i].Id, parsed.Items[i].Id);
                Assert.True(items[i].Data.AsSpan().SequenceEqual(parsed.Items[i].Data), $"Record {i} data mismatched.");
            }

            Assert.Equal([2, 0, 0], parsed.FileVersion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BuildAsync_MobilePropertyInfo_IsRecoverable()
    {
        var path = TempPath("props.qcn");
        try
        {
            var items = new List<QcnNvItem> { MakeItem(0, 10, 5) };
            var property = new QcnMobilePropertyInfo(3, 321, 4, 5, "SW-1.2.3", "QPST-9.9");
            await QcnBuilder.BuildAsync(path, items, [2, 1, 0], property, TestContext.Current.CancellationToken);
            var parsed = await QcnReader.ParseAsync(path, TestContext.Current.CancellationToken);
            var recovered = parsed.MobilePropertyInfo;
            Assert.NotNull(recovered);
            Assert.Equal(property.Efs, recovered!.Efs);
            Assert.Equal(property.MobileModelNo, recovered.MobileModelNo);
            Assert.Equal(property.SwVersion, recovered.SwVersion);
            Assert.Equal(property.QpstVersion, recovered.QpstVersion);
            Assert.Equal([2, 1, 0], parsed.FileVersion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Create_OversizedData_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            QcnNvItem.Create(0, 10, flags: 0, new byte[QcnNvItem.DataLength + 1], "NV_NUMBERED_ITEMS/NV_ITEM_ARRAY", 0));
    }

    [Fact]
    public async Task Create_ShortData_ZeroPadsToRecordLength()
    {
        var item = QcnNvItem.Create(0, 10, flags: 0, new byte[8], "NV_NUMBERED_ITEMS/NV_ITEM_ARRAY", 0);
        Assert.Equal(QcnNvItem.DataLength, item.Data.Length);
        Assert.Equal(8, new ReadOnlySpan<byte>(item.Data, 0, 8).ToArray().Length);
        Assert.True(item.Data[8..].All(b => b == 0));
    }
}