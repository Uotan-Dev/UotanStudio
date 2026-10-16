using System.Text;
using UotanStudio.Firmware.VendorFirmware.Dz;
using UotanStudio.Firmware.VendorFirmware.Nb0;
using UotanStudio.Firmware.VendorFirmware.Nb0.Models;
using UotanStudio.Firmware.VendorFirmware.OnePlusOps;
using UotanStudio.Firmware.VendorFirmware.UpdateApp;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Fixed-width vendor name fields and the capacity rules their writers apply: the NB0 entry header holds a
/// 48-byte ASCII <c>Name</c> slot whose final byte must stay a NUL terminator (47 usable bytes), the OPS entry
/// record holds a 20-byte ASCII <c>Name</c> field that the reader slices by width instead of scanning for NUL,
/// the UPDATE.APP entry header holds a 16-byte printable-ASCII <c>file_type</c> field at offset 60, and the KDZ
/// directory record holds a 256-byte UTF-8 <c>Name</c> slot terminated by NUL (255 usable bytes). Each format is
/// checked on the rejection path, where the message carries the measured byte count, the field limit and the
/// offending value, and on the capacity path, where the raw produced bytes are inspected so a <c>'?'</c>
/// substitution or a clipped prefix cannot hide behind a round-trip through the matching reader.
/// <para>各厂商容器的定宽名称字段及其写入器采用的容量规则：NB0 条目头的 48 字节 ASCII <c>Name</c> 槽位末字节必须保留为
/// NUL 结束符（可用 47 字节）；OPS 条目记录的 20 字节 ASCII <c>Name</c> 字段由读取器按宽度切片而非扫描 NUL；
/// UPDATE.APP 条目头在偏移 60 处有 16 字节可打印 ASCII <c>file_type</c> 字段；KDZ 目录记录有 256 字节 UTF-8
/// <c>Name</c> 槽位并以 NUL 收尾（可用 255 字节）。每种格式都校验拒绝路径（消息携带测得的字节数、字段上限与原值）
/// 以及上限路径（检查实际产出的原始字节），因此 <c>'?'</c> 替换或被裁剪的前缀无法借往返读取蒙混过关。</para>
/// </summary>
public sealed class VendorNameFieldTests : IDisposable
{
    private const byte QuestionMark = (byte)'?';

    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-vendor-names-" + Guid.NewGuid().ToString("N"));

    public VendorNameFieldTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Nb0EntryHeader_ToBytes_CjkName_IsRejectedWithFieldWidthAndValue()
    {
        var header = new Nb0EntryHeader { Name = "固件.bin", FileSize = 8 };

        var error = Assert.Throws<InvalidDataException>(header.ToBytes);

        Assert.Contains("NB0 entry name is not representable", error.Message);
        Assert.Contains("U+56FA", error.Message, StringComparison.Ordinal);
        Assert.Contains("48-byte Name field", error.Message, StringComparison.Ordinal);
        Assert.Contains("'固件.bin'", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("????", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nb0EntryHeader_ToBytes_NamePastUsableWidth_IsRejectedInsteadOfClipped()
    {
        var name = new string('x', Nb0EntryHeader.NameLength);
        var header = new Nb0EntryHeader { Name = name };

        var error = Assert.Throws<InvalidDataException>(header.ToBytes);

        Assert.Contains($"{name.Length} ASCII bytes exceed the 47-byte usable portion of the 48-byte Name field",
            error.Message, StringComparison.Ordinal);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nb0EntryHeader_ToBytes_NameAtUsableWidth_WritesExactSlotAndReadsBack()
    {
        var name = new string('k', 47);

        var bytes = new Nb0EntryHeader { Name = name, FileSize = 5 }.ToBytes();

        Assert.Equal(Nb0EntryHeader.StructSize, bytes.Length);
        Assert.Equal(name, Encoding.ASCII.GetString(bytes, 16, 47));
        Assert.Equal(0, bytes[63]);
        Assert.Equal(-1, bytes.AsSpan(63).IndexOfAnyExcept((byte)0));
        Assert.Equal(-1, bytes.AsSpan(16, Nb0EntryHeader.NameLength).IndexOf(QuestionMark));
        var parsed = Nb0EntryHeader.ReadFromBuffer(bytes);
        Assert.Equal(name, parsed.Name);
        Assert.Equal(5, parsed.FileSize);
    }

    [Fact]
    public void Nb0Packer_AddFile_CjkFileName_IsRejectedBeforeWritingContainer()
    {
        var source = WriteFile(Path.Combine(_root, "cjk-source"), "固件镜像.bin", [1, 2, 3]);
        var output = Path.Combine(_root, "rejected.nb0");

        var error = Assert.Throws<InvalidDataException>(() => Nb0Packer.CreateBuilder().AddFile(source));

        Assert.Contains("NB0 entry name is not representable", error.Message);
        Assert.Contains("U+56FA", error.Message, StringComparison.Ordinal);
        Assert.Contains("'固件镜像.bin'", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Nb0Packer_AddFileAndAddEntry_AsciiNamesIncludingSlash_WriteExactSlots()
    {
        var source = WriteFile(Path.Combine(_root, "ascii-source"), "boot.img", [1, 2, 3]);
        var output = Path.Combine(_root, "ascii.nb0");
        var directoryName = "images/modem.img";

        Nb0Packer.CreateBuilder().AddFile(source, "boot.img").AddEntry(directoryName, [9]).Build(output);

        var bytes = File.ReadAllBytes(output);
        Assert.Equal(2u, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal("boot.img", Nb0EntryHeader.ReadFromBuffer(bytes, 4).Name);
        Assert.Equal(directoryName, Nb0EntryHeader.ReadFromBuffer(bytes, 4 + Nb0EntryHeader.StructSize).Name);
        Assert.Equal(-1, bytes.AsSpan(4 + 16, Nb0EntryHeader.NameLength).IndexOf(QuestionMark));
        Assert.Equal(0, bytes[4 + 16 + "boot.img".Length]);
        Assert.Equal(0, bytes[4 + Nb0EntryHeader.StructSize + 16 + directoryName.Length]);
    }

    [Fact]
    public void Nb0Extractor_PackedAsciiEntry_RecoversPayloadThroughContainer()
    {
        var source = WriteFile(Path.Combine(_root, "extract-source"), "vendor.img", [7, 8, 9]);
        var output = Path.Combine(_root, "extract.nb0");
        var outputDirectory = Path.Combine(_root, "extract-out");

        Nb0Packer.CreateBuilder().AddFile(source).Build(output);
        var result = new Nb0Extractor().Extract(output, outputDirectory);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(1, result.ExtractedFileCount);
        Assert.Equal([7, 8, 9], File.ReadAllBytes(Path.Combine(outputDirectory, "vendor.img")));
    }

    [Fact]
    public async Task OnePlusOpsFormatHandler_PackAsync_CjkEntryName_FailsWithoutWritingOutput()
    {
        var directory = Path.Combine(_root, "ops-cjk");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "固件.bin"), [1, 2]);
        var output = Path.Combine(_root, "rejected.ops");

        var result = await new OnePlusOpsFormatHandler().PackAsync(directory, output, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("OPS entry name is not representable", result.Summary, StringComparison.Ordinal);
        Assert.Contains("U+56FA", result.Summary, StringComparison.Ordinal);
        Assert.Contains($"{OpsConstants.ENTRY_NAME_SIZE}-byte Name field", result.Summary, StringComparison.Ordinal);
        Assert.Contains("'固件.bin'", result.Summary, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task OnePlusOpsFormatHandler_PackAsync_NamePastField_FailsWithByteCountInsteadOfClipping()
    {
        var name = "abcdefghijklmnopqrstuvwx.bin";
        var directory = Path.Combine(_root, "ops-long");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name), [1, 2, 3]);
        var output = Path.Combine(_root, "long.ops");

        var result = await new OnePlusOpsFormatHandler().PackAsync(directory, output, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains($"{name.Length} ASCII bytes exceed the {OpsConstants.ENTRY_NAME_SIZE}-byte Name field",
            result.Summary, StringComparison.Ordinal);
        Assert.Contains(name, result.Summary, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task OnePlusOpsFormatHandler_PackAsync_NameAtFieldWidth_WritesFullFieldAndReadsBack()
    {
        var name = "abcdefghij1234567890";
        Assert.Equal(OpsConstants.ENTRY_NAME_SIZE, name.Length);
        var directory = Path.Combine(_root, "ops-capacity");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name), [4, 5, 6, 7]);
        var output = Path.Combine(_root, "capacity.ops");

        var result = await new OnePlusOpsFormatHandler().PackAsync(directory, output, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        var record = File.ReadAllBytes(output).AsSpan(OpsConstants.HEADER_SIZE, OpsConstants.ENTRY_HEADER_SIZE);
        Assert.Equal(name, Encoding.ASCII.GetString(record.Slice(12, OpsConstants.ENTRY_NAME_SIZE)));
        Assert.Equal(-1, record.Slice(12, OpsConstants.ENTRY_NAME_SIZE).IndexOf(QuestionMark));
        var entry = OpsEntry.FromBytes(record);
        Assert.Equal(name, entry.Name);
        Assert.Equal(4u, entry.Size);
    }

    [Fact]
    public void UpdateAppBuilder_Build_CjkEntryName_IsRejectedWithFieldLimitAndValue()
    {
        var directory = Path.Combine(_root, "updateapp-cjk");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "系统.img"), [1, 2]);
        var output = Path.Combine(_root, "rejected.app");

        var error = Assert.Throws<InvalidDataException>(
            () => UpdateAppBuilder.Build(directory, output, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("UPDATE.APP entry name is not representable", error.Message);
        Assert.Contains("U+7CFB", error.Message, StringComparison.Ordinal);
        Assert.Contains("16-byte file_type field", error.Message, StringComparison.Ordinal);
        Assert.Contains("'系统'", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("??", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void UpdateAppBuilder_Build_NamePastField_ReportsByteCountInsteadOfRawArgumentOutOfRange()
    {
        var name = new string('a', 17);
        var directory = Path.Combine(_root, "updateapp-long");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name + ".img"), [1]);
        var output = Path.Combine(_root, "long.app");

        var error = Assert.Throws<InvalidDataException>(
            () => UpdateAppBuilder.Build(directory, output, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains($"{name.Length} ASCII bytes exceed the 16-byte file_type field", error.Message, StringComparison.Ordinal);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void UpdateAppBuilder_Build_NameAtFieldWidth_WritesFullFieldAndReadsBack()
    {
        var name = "0123456789abcdef";
        Assert.Equal(16, name.Length);
        var directory = Path.Combine(_root, "updateapp-capacity");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name + ".img"), [1, 2, 3, 4]);
        var output = Path.Combine(_root, "capacity.app");

        Assert.Equal(1, UpdateAppBuilder.Build(directory, output, cancellationToken: TestContext.Current.CancellationToken));

        var bytes = File.ReadAllBytes(output);
        Assert.Equal(name, Encoding.ASCII.GetString(bytes, 60, 16));
        Assert.Equal(-1, bytes.AsSpan(60, 16).IndexOf(QuestionMark));
        Assert.Equal([name], UpdateAppProcessor.GetEntries(output).Select(entry => entry.Name));
    }

    [Fact]
    public async Task KdzBuilder_BuildAsync_NameFillingWholeSlot_IsRejectedForMissingTerminator()
    {
        var member = WriteFile(Path.Combine(_root, "kdz-source"), "modem.bin", [1, 2, 3]);
        var name = new string('a', 256);
        Assert.Equal(256, Encoding.UTF8.GetByteCount(name));
        var output = Path.Combine(_root, "full-slot.kdz");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => KdzBuilder.BuildAsync(
            [new KdzSource(name, member)], output, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("KDZ member name is not representable", error.Message);
        Assert.Contains("256 UTF-8 bytes exceed the 255-byte usable portion of the 256-byte Name field",
            error.Message, StringComparison.Ordinal);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task KdzBuilder_BuildAsync_CjkNamePastUtf8Capacity_IsRejectedByBytesNotCharacters()
    {
        var member = WriteFile(Path.Combine(_root, "kdz-cjk-source"), "abl.bin", [1, 2]);
        var name = new string('文', 85) + "a";
        Assert.Equal(256, Encoding.UTF8.GetByteCount(name));
        Assert.Equal(86, name.Length);
        var output = Path.Combine(_root, "cjk-capacity.kdz");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => KdzBuilder.BuildAsync(
            [new KdzSource(name, member)], output, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("256 UTF-8 bytes exceed the 255-byte usable portion of the 256-byte Name field",
            error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("86 UTF-8 bytes", error.Message, StringComparison.Ordinal);
        Assert.Contains(name, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KdzBuilder_BuildAsync_NameAtUtf8Capacity_RoundTripsWithTerminatorInsideSlot()
    {
        var member = WriteFile(Path.Combine(_root, "kdz-capacity-source"), "dtbo.bin", [4, 5, 6]);
        var name = new string('文', 85);
        Assert.Equal(255, Encoding.UTF8.GetByteCount(name));
        var output = Path.Combine(_root, "capacity.kdz");

        await KdzBuilder.BuildAsync([new KdzSource(name, member)], output,
            cancellationToken: TestContext.Current.CancellationToken);

        var bytes = File.ReadAllBytes(output);
        Assert.Equal(3, bytes[8]);
        Assert.Equal(name, Encoding.UTF8.GetString(bytes, 9, 255));
        Assert.Equal(0, bytes[9 + 255]);
        Assert.Equal(-1, bytes.AsSpan(9, 256).IndexOf((byte)0x3F));
        var parsed = KdzExtractor.Parse(output);
        Assert.Equal(2, parsed.HeaderType);
        Assert.Equal([name], parsed.Partitions.Select(part => part.Name));
    }

    [Fact]
    public async Task KdzBuilder_BuildAsync_NameStartingWithTerminatorByte_IsRejectedAsMarkerNotAsCapacity()
    {
        var member = WriteFile(Path.Combine(_root, "kdz-marker-source"), "modem.bin", [1, 2]);
        var name = "\x03modem";
        var output = Path.Combine(_root, "marker-name.kdz");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => KdzBuilder.BuildAsync(
            [new KdzSource(name, member)], output, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("must not start with the directory terminator byte", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not representable", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task KdzBuilder_BuildAsync_DuplicateNames_IgnoreCase_AreRejectedAsDuplicates()
    {
        var member = WriteFile(Path.Combine(_root, "kdz-duplicate-source"), "modem.bin", [1, 2]);
        var output = Path.Combine(_root, "duplicate.kdz");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => KdzBuilder.BuildAsync(
            [new KdzSource("modem", member), new KdzSource("MODEM", member)], output,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("KDZ member names must be unique.", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("UTF-8 bytes", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    private static string WriteFile(string directory, string name, byte[] content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}