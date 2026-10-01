using System.Buffers.Binary;
using UotanStudio.Firmware.VendorFirmware.MediaTek;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class MediaTekFormatHandlerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-mtk-" + Guid.NewGuid().ToString("N"));

    public MediaTekFormatHandlerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] DownloadAgentContainer()
    {
        var bytes = new byte[2048];
        "MTK_DOWNLOAD_AGENT"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100), 0x22668899);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104), 2);
        for (var entry = 0; entry < 2; entry++)
        {
            var record = 108 + entry * 220;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(record), 0xDADA);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(record + 2), checked((ushort)(0x6873 + entry)));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(record + 4), 0x8A00);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(record + 6), 0xCA00);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(record + 8), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(record + 12), 4096);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(record + 16), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(record + 18), 1);
            var region = record + 20;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(region), (uint)(1024 + entry * 256));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(region + 4), 64);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(region + 8), 0x200000);
            for (var index = 0; index < 64; index++)
            {
                bytes[1024 + entry * 256 + index] = (byte)(entry * 16 + index);
            }
        }

        return bytes;
    }

    private static string ScatterYaml(string platform)
    {
        const string header = """
            - general: MTK_PLATFORM_CFG
              info:
              - config_version: V1.2.3
                platform: @PLATFORM@
                project: battery
            - storage_type: EMMC
              description:
              - general: MTK_STORAGE_CFG
                info:
                - storage: EMMC
                  boot_channel: MSDC_0
                  block_size: 0x20000
            """;
        const string partition = """
            - partition_index: SYS0
              partition_name: boot
              file_name: boot.img
              is_download: true
              type: LCS_NORMAL_IMAGE
              linear_start_addr: 0x1800000
              physical_start_addr: 0x1800000
              partition_size: 0x4000000
              region: EMMC_USER
              storage: HW_STORAGE_EMMC
              boundary_check: true
              is_reserved: false
              operation_type: IMAGE
            """;
        return header.Replace("@PLATFORM@", platform, StringComparison.Ordinal) + "\n"
            + string.Join('\n', partition.Split('\n').Select(line => "  " + line));
    }

    private const string ScatterXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <scatter>
          <platform platform="MT6580" name="demo_project">
            <download_mode>
              <image file_name="preloader.bin"/>
              <image file_name="NONE"/>
            </download_mode>
          </platform>
        </scatter>
        """;

    [Fact]
    public async Task CanHandle_ScatterVariantsAndDownloadAgent_AllAccepts_UnrelatedRejects()
    {
        var handler = new MtkFormatHandler();
        var yamlPath = Path.Combine(_root, "MT6785_scatter.txt");
        await File.WriteAllTextAsync(yamlPath, ScatterYaml("MT6785"), TestContext.Current.CancellationToken);
        var xmlPath = Path.Combine(_root, "scatter.xml");
        await File.WriteAllTextAsync(xmlPath, ScatterXml, TestContext.Current.CancellationToken);
        var daPath = Path.Combine(_root, "DA.bin");
        await File.WriteAllBytesAsync(daPath, DownloadAgentContainer(), TestContext.Current.CancellationToken);
        var foreignPath = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(foreignPath, "just notes", TestContext.Current.CancellationToken);

        Assert.True(handler.CanHandle(yamlPath));
        Assert.True(handler.CanHandle(xmlPath));
        Assert.True(handler.CanHandle(daPath));
        Assert.False(handler.CanHandle(foreignPath));
    }

    [Fact]
    public async Task ReadMetadataAsync_ScatterYaml_ReportsChipPlatformAndDownloadImages()
    {
        var path = Path.Combine(_root, "mt6765_scatter.txt");
        await File.WriteAllTextAsync(path, ScatterYaml("MT6765"), TestContext.Current.CancellationToken);

        var result = await new MtkFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("ScatterYaml", result.Metadata["Kind"]);
        Assert.Equal("MT6765", result.Metadata["Chip"]);
        Assert.Contains("boot boot.img", result.Entries);
    }

    [Fact]
    public async Task ReadMetadataAsync_ScatterXml_ReportsPlatformAttributeAndSkipsNoneSentinel()
    {
        var path = Path.Combine(_root, "legacy.xml");
        await File.WriteAllTextAsync(path, ScatterXml, TestContext.Current.CancellationToken);

        var result = await new MtkFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("ScatterXml", result.Metadata["Kind"]);
        Assert.Equal("MT6580", result.Metadata["Chip"]);
        Assert.Equal("demo_project", result.Metadata["Project"]);
        Assert.Equal(["preloader.bin"], result.Entries);
    }

    [Fact]
    public async Task ReadMetadataAsync_DownloadAgent_ReportsHardwareCodes()
    {
        var path = Path.Combine(_root, "da_all_in_one.bin");
        await File.WriteAllBytesAsync(path, DownloadAgentContainer(), TestContext.Current.CancellationToken);

        var result = await new MtkFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("DA", result.Metadata["Kind"]);
        Assert.Contains("0x6873/0x8A00", result.Entries);
        Assert.Contains("0x6874/0x8A00", result.Entries);
    }

    [Fact]
    public async Task UnpackAsync_DownloadAgent_WritesByteIdenticalRegionPayloads()
    {
        var container = DownloadAgentContainer();
        var path = Path.Combine(_root, "da.bin");
        await File.WriteAllBytesAsync(path, container, TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "da-out");

        var result = await new MtkFormatHandler().UnpackAsync(path, output,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(2, result.Components.Count);
        Assert.Equal(container.AsSpan(1024, 64).ToArray(),
            await File.ReadAllBytesAsync(Path.Combine(output, result.Components[0].Name), TestContext.Current.CancellationToken));
        Assert.Equal(container.AsSpan(1280, 64).ToArray(),
            await File.ReadAllBytesAsync(Path.Combine(output, result.Components[1].Name), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnpackAsync_ScatterYaml_CopiesManifestIntoOutput()
    {
        var path = Path.Combine(_root, "copy_scatter.txt");
        await File.WriteAllTextAsync(path, ScatterYaml("MT6893"), TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "copy-out");

        var result = await new MtkFormatHandler().UnpackAsync(path, output,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("copy_scatter.txt", Assert.Single(result.Entries));
        Assert.Equal(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(Path.Combine(output, "copy_scatter.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadMetadataAsync_TruncatedDownloadAgent_FailsWithoutThrowing()
    {
        var bytes = DownloadAgentContainer();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104), 4_000_000_000);
        var path = Path.Combine(_root, "broken_da.bin");
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        var result = await new MtkFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }
}