using UotanStudio.Compress.Zip;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Characterization tests for <c>PayloadTool</c>, the convenience façade over the payload parser. The tool has no
/// production consumer, so these tests pin the observable contract as shipped: <c>CrAU</c> magic detection for both
/// the byte-array and the path overload, and in-memory single-partition extraction including the case-insensitive
/// partition match and the <see langword="null"/> returns that stand for "not a payload" or "no such partition".
/// <para><c>PayloadTool</c>（payload 解析器之上的便捷门面）的表征测试。该工具在生产代码中没有消费者，
/// 因此本测试钉住其已发布的可观察契约：字节数组与路径两种重载的 <c>CrAU</c> 魔数识别，
/// 以及单分区内存提取（含不区分大小写的分区名匹配，和代表"不是 payload"或"无此分区"的 <see langword="null"/> 返回）。</para>
/// </summary>
public class PayloadToolTests
{
    private const uint BlockSize = 4096;

    private static readonly byte[] CrAuMagic = [0x43, 0x72, 0x41, 0x55];

    private static byte[] Image(int blocks, byte seed)
    {
        var data = new byte[blocks * (int)BlockSize];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = unchecked((byte)(seed + index * 3));
        }

        return data;
    }

    [Fact]
    public void IsPayload_Bytes_AcceptsOnlyTheCrAuMagic()
    {
        Assert.True(PayloadTool.IsPayload([.. CrAuMagic, .. new byte[64]]));
        Assert.False(PayloadTool.IsPayload([]));
        Assert.False(PayloadTool.IsPayload([0x43, 0x72, 0x41]));
        Assert.False(PayloadTool.IsPayload([0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]));
        Assert.False(PayloadTool.IsPayload((byte[])null!));
    }

    [Fact]
    public void IsPayload_Bytes_ReadsMagicAsBigEndian()
    {
        var littleEndianMagic = new byte[] { 0x55, 0x41, 0x72, 0x43, 0, 0, 0, 0 };

        Assert.False(PayloadTool.IsPayload(littleEndianMagic));
        Assert.Equal((byte)0x43, CrAuMagic[0]);
    }

    [Fact]
    public void IsPayload_Path_AcceptsRealPayloadAndRejectsEverythingElse()
    {
        using var temp = new TempDirectory();
        var payload = BuildRealPayload(temp, "system", Image(1, 0x11));

        Assert.True(PayloadTool.IsPayload(payload));
        Assert.False(PayloadTool.IsPayload(temp.WriteBytes("notpayload.bin", [0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0])));
        Assert.True(PayloadTool.IsPayload(temp.WriteBytes("magic-only.bin", CrAuMagic)));
        Assert.False(PayloadTool.IsPayload(temp.WriteBytes("short.bin", [0x43, 0x72, 0x41])));
        Assert.False(PayloadTool.IsPayload(Path.Combine(temp.Path, "absent.bin")));
        Assert.False(PayloadTool.IsPayload("   "));
        Assert.False(PayloadTool.IsPayload(string.Empty));
    }

    [Fact]
    public void IsPayloadArchive_ZipWithPayloadEntry_AcceptsAndRejectsByEntryName()
    {
        using var temp = new TempDirectory();
        var withPayload = Path.Combine(temp.Path, "update.zip");
        using (var archive = ZipContainer.Create(withPayload))
        {
            using var entry = archive.CreateEntry("payload.bin").Open();
            entry.Write([0x43, 0x72, 0x41, 0x55, 0, 0, 0, 0]);
        }

        Assert.True(PayloadTool.IsPayloadArchive(withPayload));

        var withoutPayload = Path.Combine(temp.Path, "plain.zip");
        using (var archive = ZipContainer.Create(withoutPayload))
        {
            using var entry = archive.CreateEntry("boot.img").Open();
            entry.Write([1, 2, 3]);
        }

        Assert.False(PayloadTool.IsPayloadArchive(withoutPayload));
        Assert.False(PayloadTool.IsPayloadArchive(temp.WriteBytes("raw.bin", CrAuMagic)));
        Assert.False(PayloadTool.IsPayloadArchive(Path.Combine(temp.Path, "absent.zip")));
    }

    [Fact]
    public void ExtractImage_Bytes_ReturnsTheWholePartitionImage()
    {
        var system = Image(2, 0x21);
        using var temp = new TempDirectory();
        var payload = BuildRealPayload(temp, "system", system, ("boot", Image(1, 0x31)));

        Assert.Equal(system, PayloadTool.ExtractImage(File.ReadAllBytes(payload), "system"));
    }

    [Fact]
    public void ExtractImage_PartitionNameMatch_IsCaseInsensitive()
    {
        var system = Image(1, 0x41);
        using var temp = new TempDirectory();
        var payload = BuildRealPayload(temp, "system", system);
        var bytes = File.ReadAllBytes(payload);

        Assert.Equal(system, PayloadTool.ExtractImage(bytes, "SYSTEM"));
        Assert.Equal(system, PayloadTool.ExtractImage(bytes, "SyStEm"));
        Assert.Equal(system, PayloadTool.ExtractImage(payload, "system"));
    }

    [Fact]
    public void ExtractImage_Bytes_ReportsNullForNonPayloadAndUnknownPartition()
    {
        using var temp = new TempDirectory();
        var payload = BuildRealPayload(temp, "system", Image(1, 0x51));

        Assert.Null(PayloadTool.ExtractImage([], "system"));
        Assert.Null(PayloadTool.ExtractImage([0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0], "system"));
        Assert.Null(PayloadTool.ExtractImage(File.ReadAllBytes(payload), "vendor"));
        Assert.Null(PayloadTool.ExtractImage(payload, "vendor"));
    }

    [Fact]
    public void ExtractImage_BlankArgumentsAndMissingFile_ThrowBeforeReading()
    {
        using var temp = new TempDirectory();
        var payload = BuildRealPayload(temp, "system", Image(1, 0x61));

        Assert.Throws<ArgumentException>(() => PayloadTool.ExtractImage(new byte[64], "  "));
        Assert.Throws<ArgumentException>(() => PayloadTool.ExtractImage(payload, string.Empty));
        Assert.Throws<FileNotFoundException>(() => PayloadTool.ExtractImage(Path.Combine(temp.Path, "absent.bin"), "system"));
        Assert.Throws<ArgumentException>(() => PayloadTool.ExtractImage(Path.Combine(temp.Path, "absent.bin"), "  "));
        Assert.Throws<ArgumentNullException>(() => PayloadTool.ExtractImage((byte[])null!, "system"));
        Assert.Throws<ArgumentException>(() => PayloadTool.ExtractImage("  ", "system"));
    }

    [Fact]
    public void ExtractImage_Bytes_CleansUpItsTemporaryFile()
    {
        using var temp = new TempDirectory();
        var payload = BuildRealPayload(temp, "system", Image(1, 0x71));
        var before = Directory.GetFiles(Path.GetTempPath(), "uotan_payload_*").Length;

        Assert.NotNull(PayloadTool.ExtractImage(File.ReadAllBytes(payload), "system"));

        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "uotan_payload_*").Length);
    }

    [Fact]
    public void ExtractImage_NonPayloadFile_ReturnsNullWithoutThrowing()
    {
        using var temp = new TempDirectory();
        var notPayload = temp.WriteBytes("notpayload.bin", [0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0]);

        Assert.Null(PayloadTool.ExtractImage(notPayload, "system"));
    }

    [Fact]
    public void ExtractImage_MultiplePartitions_SelectsOnlyTheRequestedOne()
    {
        var system = Image(2, 0x81);
        var boot = Image(1, 0x91);
        using var temp = new TempDirectory();
        var payload = BuildRealPayload(temp, "system", system, ("boot", boot));

        Assert.Equal(system, PayloadTool.ExtractImage(payload, "system"));
        Assert.Equal(boot, PayloadTool.ExtractImage(payload, "boot"));
    }

    private static string BuildRealPayload(TempDirectory temp, string firstName, byte[] firstImage, params ReadOnlySpan<(string Name, byte[] Image)> extra)
    {
        var path = Path.Combine(temp.Path, "payload.bin");
        var sources = new List<MemoryStream>();
        using var builder = new PayloadBuilder().WithBlockSize(BlockSize);
        sources.Add(new MemoryStream(firstImage, writable: false));
        builder.AddPartition(firstName, sources[^1]);
        foreach (var (name, image) in extra)
        {
            sources.Add(new MemoryStream(image, writable: false));
            builder.AddPartition(name, sources[^1]);
        }

        try
        {
            builder.Build(path);
        }
        finally
        {
            foreach (var source in sources)
            {
                source.Dispose();
            }
        }

        Assert.True(PayloadTool.IsPayload(path), "The built payload must carry the CrAU magic for these tests to be meaningful.");
        return path;
    }
}