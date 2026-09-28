using System.Text;
using UotanStudio.Firmware.VendorFirmware.MediaTek;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class MtkScatterReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_FlatOrGroupedLayout_PreservesAddressAndPolicyDeclarations(bool grouped)
    {
        using var stream = Input(Document(grouped));

        var result = MtkScatterReader.Read(stream, TestContext.Current.CancellationToken);

        Assert.Equal("MT6785", result.Platform);
        var storage = Assert.Single(result.Storages);
        Assert.Equal("EMMC", storage.Name);
        Assert.Equal(0x20000ul, storage.BlockSize);
        var partition = Assert.Single(storage.Partitions);
        Assert.Equal(0x80000ul, partition.LinearStartAddress);
        Assert.Equal(0ul, partition.PhysicalStartAddress);
        Assert.Equal("EMMC_BOOT1_BOOT2", partition.Region);
        Assert.Equal("NONE", partition.FileName);
        Assert.False(partition.IsDownload);
        Assert.Equal("false", partition.Fields["empty_boot_needed"]);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("negative")]
    [InlineData("overflow")]
    [InlineData("boolean")]
    [InlineData("missing")]
    [InlineData("nested-field")]
    [InlineData("tag")]
    [InlineData("anchor")]
    [InlineData("alias")]
    [InlineData("second-document")]
    [InlineData("bad-index")]
    [InlineData("zero-block-size")]
    [InlineData("storage-mismatch")]
    public void Read_UnsafeOrAmbiguousInput_RejectsWithoutClosingSource(string failure)
    {
        var text = Document(true).Replace("\r\n", "\n", StringComparison.Ordinal);
        text = failure switch
        {
            "duplicate" => text.Replace("    file_name: NONE", "    file_name: NONE\n    file_name: boot.img", StringComparison.Ordinal),
            "negative" => text.Replace("0x80000", "-1", StringComparison.Ordinal),
            "overflow" => text.Replace("0x80000", "0x10000000000000000", StringComparison.Ordinal),
            "boolean" => text.Replace("is_download: false", "is_download: yes", StringComparison.Ordinal),
            "missing" => text.Replace("    file_name: NONE\n", "", StringComparison.Ordinal),
            "nested-field" => text.Replace("file_name: NONE", "file_name: [NONE]", StringComparison.Ordinal),
            "tag" => text.Replace("file_name: NONE", "file_name: !!str NONE", StringComparison.Ordinal),
            "anchor" => text.Replace("file_name: NONE", "file_name: &file NONE", StringComparison.Ordinal),
            "alias" => text.Replace("file_name: NONE", "file_name: *missing", StringComparison.Ordinal),
            "second-document" => text + "\n---\n[]",
            "bad-index" => text.Replace("SYS0", "SYS-1", StringComparison.Ordinal),
            "zero-block-size" => text.Replace("block_size: 0x20000", "block_size: 0", StringComparison.Ordinal),
            _ => text.Replace("storage_type: EMMC", "storage_type: UFS", StringComparison.Ordinal)
        };
        using var stream = Input(text);

        Assert.Throws<InvalidDataException>(() => MtkScatterReader.Read(stream, TestContext.Current.CancellationToken));
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData("depth")]
    [InlineData("scalar")]
    [InlineData("document")]
    public void Read_ExcessiveInput_RejectsBoundedResources(string limit)
    {
        var text = limit switch
        {
            "depth" => new string('[', 32) + "1" + new string(']', 32),
            "scalar" => Document(false).Replace("file_name: NONE", "file_name: " + new string('x', 4097), StringComparison.Ordinal),
            _ => "#" + new string('x', 1024 * 1024)
        };
        using var stream = Input(text);

        Assert.Throws<InvalidDataException>(() => MtkScatterReader.Read(stream, TestContext.Current.CancellationToken));
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void Read_DuplicateNumericIndex_RejectsAlternativeSpelling()
    {
        using var stream = Input(Document(false) + "\n" + Partition.Replace("SYS0", "SYS00", StringComparison.Ordinal));

        Assert.Throws<InvalidDataException>(() => MtkScatterReader.Read(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Read_QuotedPathAndAddressSentinel_PreservesLiteralValues()
    {
        var text = Document(false).Replace("file_name: NONE", "file_name: '../images/name #1.img'", StringComparison.Ordinal)
            .Replace("physical_start_addr: 0", "physical_start_addr: 0xFFFFFFFFFFFFFFFF", StringComparison.Ordinal);
        using var stream = Input(text);

        var result = MtkScatterReader.Read(stream, TestContext.Current.CancellationToken);

        var entry = Assert.Single(Assert.Single(result.Storages).Partitions);
        Assert.Equal("../images/name #1.img", entry.FileName);
        Assert.Equal(ulong.MaxValue, entry.PhysicalStartAddress);
    }

    [Fact]
    public void Read_PreCancelled_DoesNotConsumeInput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = Input(Document(false));

        Assert.ThrowsAny<OperationCanceledException>(() => MtkScatterReader.Read(stream, cancellation.Token));
        Assert.Equal(0, stream.Position);
    }

    private static MemoryStream Input(string text) => new(Encoding.UTF8.GetBytes(text));

    private static string Document(bool grouped)
    {
        const string header = """
            - general: MTK_PLATFORM_CFG
              info:
              - config_version: V2.1.0
                platform: MT6785
                project: test
            """;
        const string storage = """
            - storage_type: EMMC
              description:
              - general: MTK_STORAGE_CFG
                info:
                - storage: EMMC
                  boot_channel: MSDC_0
                  block_size: 0x20000
            """;
        return grouped
            ? header + "\n" + storage + "\n" + string.Join('\n', Partition.Split('\n').Select(line => "  " + line))
            : header + "\n    storage: EMMC\n    boot_channel: MSDC_0\n    block_size: 0x20000\n" + Partition;
    }

    private const string Partition = """
        - partition_index: SYS0
          partition_name: preloader
          file_name: NONE
          is_download: false
          type: SV5_BL_BIN
          linear_start_addr: 0x80000
          physical_start_addr: 0
          partition_size: 524288
          region: EMMC_BOOT1_BOOT2
          storage: HW_STORAGE_EMMC
          boundary_check: true
          is_reserved: false
          operation_type: BOOTLOADERS
          empty_boot_needed: false
        """;
}