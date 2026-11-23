using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Radio.Qcn;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class QcnDocumentTests
{
    private static byte[] Record(ushort id, ushort flags, byte[] data)
    {
        Assert.True(data.Length <= 128);
        var record = new byte[136];
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0, 2), 136);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4, 2), id);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6, 2), flags);
        data.CopyTo(record, 8);
        return record;
    }

    private static CompoundFileBuilder CreateQcn(IReadOnlyList<byte[]> numberedRecords, IReadOnlyList<byte[]> defaultRecords)
    {
        var builder = new CompoundFileBuilder();
        builder.AddStream("File_Version", [0x02, 0x00, 0x00, 0x00, 0x00, 0x00]);
        var info = new List<byte>();
        info.AddRange(BitConverter.GetBytes(1u));
        info.AddRange(BitConverter.GetBytes((ushort)0x1234));
        info.Add(2);
        info.Add(0);
        AddLengthPrefixed(info, "1.0");
        AddLengthPrefixed(info, "QPST 2.7 build 323");
        builder.AddStream("00000000/default/Mobile_Property_Info", [.. info]);
        builder.AddStream("NV_NUMBERED_ITEMS/NV_ITEM_ARRAY", numberedRecords.SelectMany(record => record).ToArray());
        builder.AddStream("00000000/default/NV_ITEM_ARRAY", defaultRecords.SelectMany(record => record).ToArray());
        return builder;
    }

    private static void AddLengthPrefixed(List<byte> target, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        target.AddRange(BitConverter.GetBytes((ushort)bytes.Length));
        target.AddRange(bytes);
    }

    private static async Task<string> WriteAsync(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-qcn-{Guid.NewGuid():N}.qcn");
        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }

    private static async Task<string> WriteTextAsync(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-qcn-{Guid.NewGuid():N}.xqcn");
        await File.WriteAllTextAsync(path, content, Encoding.Latin1, TestContext.Current.CancellationToken);
        return path;
    }

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length * 3);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(bytes[i].ToString("X2"));
        }

        return builder.ToString();
    }

    private static string XmlQcn(IReadOnlyList<byte[]> numbered, IReadOnlyList<byte[]> defaults, byte[]? property = null)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"Windows-1252\" ?>\r\n\r\n");
        builder.Append("<Storage Name='sample.xqcn'>\r\n");
        builder.Append("  <Storage Name='00000000'>\r\n");
        builder.Append("    <Storage Name='default'>\r\n");
        builder.Append("      <Storage Name='NV_NUMBERED_ITEMS'>\r\n");
        builder.Append("        <Stream Length='").Append(numbered.Sum(record => record.Length))
            .Append("' Name='NV_ITEM_ARRAY' Value='").Append(Hex(numbered.SelectMany(record => record).ToArray())).Append("'/>\r\n");
        builder.Append("      </Storage>\r\n");
        if (property is not null)
        {
            builder.Append("      <Stream Length='").Append(property.Length)
                .Append("' Name='Mobile_Property_Info' Value='").Append(Hex(property)).Append("'/>\r\n");
        }

        builder.Append("      <Stream Length='8' Name='Feature_Mask' Value='36 00 86 23 00 00 00 00'/>\r\n");
        if (defaults.Count > 0)
        {
            builder.Append("      <Stream Length='").Append(defaults.Sum(record => record.Length))
                .Append("' Name='NV_ITEM_ARRAY' Value='").Append(Hex(defaults.SelectMany(record => record).ToArray())).Append("'/>\r\n");
        }

        builder.Append("    </Storage>\r\n");
        builder.Append("  </Storage>\r\n");
        builder.Append("  <Stream Length='6' Name='File_Version' Value='02 00 00 00 00 00'/>\r\n");
        builder.Append("</Storage>");
        return builder.ToString();
    }

    [Fact]
    public async Task ParseAsync_TwoNvItemArrayStreams_MergesAllRecords()
    {
        List<byte[]> numbered = [Record(0, 0, [0xAA, 0xBB, 0xCC, 0xDD]), Record(85, 0, "000000"u8.ToArray())];
        var defaults = new List<byte[]>();
        for (var i = 0; i < 40; i++)
        {
            var data = new byte[9];
            data[0] = 0x08;
            defaults.Add(Record(550, (ushort)i, data));
        }

        var path = await WriteAsync(CreateQcn(numbered, defaults).Build());
        try
        {
            var document = await QcnReader.ParseAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(42, document.Items.Count);
            Assert.Equal(2, document.NvItemArrayStreamPaths.Count);
            Assert.Equal([2, 0, 0], document.FileVersion);
            Assert.NotNull(document.MobilePropertyInfo);
            Assert.Equal(1u, document.MobilePropertyInfo!.Efs);
            Assert.Equal(0x1234, document.MobilePropertyInfo.MobileModelNo);
            Assert.Equal("1.0", document.MobilePropertyInfo.SwVersion);
            Assert.Equal("QPST 2.7 build 323", document.MobilePropertyInfo.QpstVersion);

            var esn = document.Items.Single(item => item.Id == 0);
            Assert.Equal([0xAA, 0xBB, 0xCC, 0xDD], esn.Data[..4]);
            Assert.Equal(4, esn.EffectiveLength);
            Assert.Equal("NV_NUMBERED_ITEMS/NV_ITEM_ARRAY", esn.SourceStreamPath);
            Assert.Equal(0, esn.RecordIndex);

            var imei = document.Items.Single(item => item.Id == 550 && item.Flags == 7);
            Assert.Equal("00000000/default/NV_ITEM_ARRAY", imei.SourceStreamPath);
            Assert.Equal(7, imei.RecordIndex);
            Assert.Equal(1, imei.EffectiveLength);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsync_ContainerWithoutNvRecords_ThrowsInvalidFormat()
    {
        var builder = new CompoundFileBuilder();
        builder.AddStream("WordDocument", new byte[512]);
        var path = await WriteAsync(builder.Build());
        try
        {
            await Assert.ThrowsAsync<InvalidQcnFormatException>(() => QcnReader.ParseAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsync_BadRecordHeader_ThrowsInvalidFormat()
    {
        var bad = new byte[137];
        bad[0] = 0x89;
        var path = await WriteAsync(CreateQcn([bad], []).Build());
        try
        {
            await Assert.ThrowsAsync<InvalidQcnFormatException>(() => QcnReader.ParseAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PatchAsync_TwoEditedRecords_RewritesOnlyChangedDataRegions()
    {
        var numbered = new List<byte[]>();
        for (var i = 0; i < 40; i++)
        {
            numbered.Add(Record((ushort)(1000 + i), (ushort)(i == 5 ? 0x1234 : 0), Enumerable.Range(0, 16).Select(j => (byte)(i + j)).ToArray()));
        }

        var path = await WriteAsync(CreateQcn(numbered, [Record(85, 0, "000000"u8.ToArray())]).Build());
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        try
        {
            var document = await QcnReader.ParseAsync(path, TestContext.Current.CancellationToken);
            document.Items[3].Data[0] = 0xEE;
            document.Items[3].Data[1] = 0xD0;
            document.Items[39].Data[15] = 0x77;
            var patched = await QcnWriter.PatchAsync(path, document.Items, TestContext.Current.CancellationToken);
            Assert.Equal(2, patched);

            var after = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(original.Length, after.Length);
            var diffPositions = original.Select((value, index) => (value, index)).Zip(after, (left, right) => (left.value != right, left.index)).Where(pair => pair.Item1).Select(pair => pair.index).ToList();
            Assert.Equal(3, diffPositions.Count);

            var reparsed = await QcnReader.ParseAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(0xEE, reparsed.Items[3].Data[0]);
            Assert.Equal(0xD0, reparsed.Items[3].Data[1]);
            Assert.Equal(0x77, reparsed.Items[39].Data[15]);
            Assert.Equal(0x1234, reparsed.Items.Single(item => item.Id == 1005).Flags);
            for (var i = 0; i < reparsed.Items.Count; i++)
            {
                if (i is not (3 or 39))
                {
                    Assert.Equal(document.Items[i].Data, reparsed.Items[i].Data);
                    Assert.Equal(document.Items[i].Id, reparsed.Items[i].Id);
                    Assert.Equal(document.Items[i].Flags, reparsed.Items[i].Flags);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PatchAsync_NoEdits_ReturnsZeroWithoutTouchingFile()
    {
        var path = await WriteAsync(CreateQcn([Record(85, 0, "000000"u8.ToArray())], []).Build());
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        try
        {
            var document = await QcnReader.ParseAsync(path, TestContext.Current.CancellationToken);
            var patched = await QcnWriter.PatchAsync(path, document.Items, TestContext.Current.CancellationToken);
            Assert.Equal(0, patched);
            Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PatchAsync_ItemsFromAnotherFile_ThrowsMismatch()
    {
        var pathA = await WriteAsync(CreateQcn([Record(85, 0, "000000"u8.ToArray()), Record(0, 0, [1, 2, 3, 4])], []).Build());
        var pathB = await WriteAsync(CreateQcn([Record(85, 0, "000000"u8.ToArray())], []).Build());
        try
        {
            var documentB = await QcnReader.ParseAsync(pathB, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<QcnPatchMismatchException>(() => QcnWriter.PatchAsync(pathA, documentB.Items, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    [Fact]
    public void ImeiCodec_KnownVector_EncodesDecodesWithValidCheckDigit()
    {
        Assert.Equal('8', QcnImeiCodec.ComputeCheckDigit("49015420323751"));

        Span<byte> encoded = stackalloc byte[9];
        Assert.True(QcnImeiCodec.TryEncode("490154203237518", encoded));
        Assert.Equal([0x08, 0x4A, 0x09, 0x51, 0x24, 0x30, 0x32, 0x57, 0x81], encoded.ToArray());

        Assert.True(QcnImeiCodec.TryDecode(encoded, out var imei, out var valid));
        Assert.Equal("490154203237518", imei);
        Assert.True(valid);
    }

    [Fact]
    public void ImeiCodec_InvalidInputs_Rejected()
    {
        Span<byte> encoded = stackalloc byte[9];
        Assert.False(QcnImeiCodec.TryEncode("49015420323751", encoded));
        Assert.False(QcnImeiCodec.TryEncode("49015420323751X", encoded));
        Assert.False(QcnImeiCodec.TryDecode([0x01, 0x4A, 0x09, 0x51, 0x24, 0x30, 0x32, 0x57, 0x81], out _, out _));
        Assert.True(QcnImeiCodec.TryDecode([0x08, 0x4A, 0x09, 0x51, 0x24, 0x30, 0x32, 0x57, 0x81], out _, out var valid));
        Assert.True(valid);
    }

    [Fact]
    public void ItemCodecs_SpcMeidEsnMac_RoundTrip()
    {
        Span<byte> spc = stackalloc byte[6];
        Assert.True(QcnItemCodecs.TryEncodeSpc("123456", spc));
        Assert.Equal("123456", QcnItemCodecs.DecodeSpc(spc));
        Assert.False(QcnItemCodecs.TryEncodeSpc("12345", spc));
        Assert.Null(QcnItemCodecs.DecodeSpc("12 456"u8));

        Span<byte> meid = stackalloc byte[7];
        Assert.True(QcnItemCodecs.TryEncodeReversedHex("0123456789ABCD", meid));
        Assert.Equal("0123456789ABCD", QcnItemCodecs.DecodeReversedHex(meid, 7));
        Assert.Equal(0x01, meid[6]);
        Assert.Equal(0xCD, meid[0]);

        Span<byte> esn = stackalloc byte[4];
        Assert.True(QcnItemCodecs.TryEncodeReversedHex("DEADBEEF", esn));
        Assert.Equal("DEADBEEF", QcnItemCodecs.DecodeReversedHex(esn, 4));

        Span<byte> mac = stackalloc byte[6];
        Assert.True(QcnItemCodecs.TryEncodeMac("aa:bb:cc:dd:ee:ff", mac));
        Assert.Equal([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF], mac.ToArray());
        Assert.Equal("AA:BB:CC:DD:EE:FF", QcnItemCodecs.DecodeMac(mac));
        Assert.False(QcnItemCodecs.TryEncodeMac("aa:bb:cc:dd:ee", mac));
    }

    [Fact]
    public async Task ParseAsync_XmlQcn_DecodesAllStreamsAndRecords()
    {
        var info = new List<byte>();
        info.AddRange(BitConverter.GetBytes(7u));
        info.AddRange(BitConverter.GetBytes((ushort)0x2211));
        info.Add(3);
        info.Add(4);
        AddLengthPrefixed(info, "MI-1.2");
        AddLengthPrefixed(info, "QPST 2.7");
        var path = await WriteTextAsync(XmlQcn(
            [Record(85, 0, "123456"u8.ToArray()), Record(550, 2, [0x08, 0x4A, 0x09, 0x51, 0x24, 0x30, 0x32, 0x57, 0x81])],
            [Record(0, 0, [0xAA])], info.ToArray()));
        try
        {
            var document = await QcnReader.ParseAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(3, document.Items.Count);
            Assert.Equal(2, document.NvItemArrayStreamPaths.Count);
            Assert.Equal([2, 0, 0], document.FileVersion);
            Assert.NotNull(document.MobilePropertyInfo);
            Assert.Equal(7u, document.MobilePropertyInfo!.Efs);
            Assert.Equal(0x2211, document.MobilePropertyInfo.MobileModelNo);
            Assert.Equal("MI-1.2", document.MobilePropertyInfo.SwVersion);
            Assert.Equal("QPST 2.7", document.MobilePropertyInfo.QpstVersion);
            Assert.Equal(85, document.Items[0].Id);
            Assert.Equal("00000000/default/NV_NUMBERED_ITEMS/NV_ITEM_ARRAY", document.Items[0].SourceStreamPath);
            Assert.Equal("123456", Encoding.ASCII.GetString(document.Items[0].Data, 0, 6));
            Assert.Equal(550, document.Items[1].Id);
            Assert.Equal(2, document.Items[1].Flags);
            Assert.Equal("00000000/default/NV_ITEM_ARRAY", document.Items[2].SourceStreamPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PatchAsync_XmlQcn_RewritesOnlyChangedHexValues()
    {
        var records = new List<byte[]>();
        for (var i = 0; i < 3; i++)
        {
            records.Add(Record((ushort)(100 + i), 0, Enumerable.Range(0, 8).Select(j => (byte)(i + j)).ToArray()));
        }

        var path = await WriteTextAsync(XmlQcn(records, []));
        var original = await File.ReadAllTextAsync(path, Encoding.Latin1, TestContext.Current.CancellationToken);
        try
        {
            var document = await QcnReader.ParseAsync(path, TestContext.Current.CancellationToken);
            var firstRecordTail = Hex(document.Items[0].Data)[3..];
            document.Items[0].Data[0] = 0xEE;
            document.Items[2].Data[7] = 0x77;
            var patched = await QcnWriter.PatchAsync(path, document.Items, TestContext.Current.CancellationToken);
            Assert.Equal(2, patched);

            var text = await File.ReadAllTextAsync(path, Encoding.Latin1, TestContext.Current.CancellationToken);
            Assert.Contains("Feature_Mask", text);
            Assert.Contains("36 00 86 23 00 00 00 00", text);
            Assert.NotEqual(original, text);
            Assert.Contains(Hex(document.Items[1].Data), text);

            var reparsed = await QcnReader.ParseAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(0xEE, reparsed.Items[0].Data[0]);
            Assert.Equal(0x77, reparsed.Items[2].Data[7]);
            Assert.Equal(firstRecordTail, Hex(reparsed.Items[0].Data)[3..]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsync_MalformedXml_ThrowsInvalidFormat()
    {
        var path = await WriteTextAsync("<Storage Name='broken'><Stream Name='NV_ITEM_ARRAY' Length='1' Value='ZZ'/></Storage>");
        try
        {
            await Assert.ThrowsAsync<InvalidQcnFormatException>(() => QcnReader.ParseAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseAsync_XmlQcnWithLengthValueMismatch_ThrowsInvalidFormat()
    {
        var path = await WriteTextAsync("<Storage Name='broken'><Stream Name='NV_ITEM_ARRAY' Length='136' Value='88 00'/></Storage>");
        try
        {
            await Assert.ThrowsAsync<InvalidQcnFormatException>(() => QcnReader.ParseAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }
}