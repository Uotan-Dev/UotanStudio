using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Ext4;
using UotanStudio.Tests.Infrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// 真实固件语料深度套件：语料库（默认 D:\Firmware，可用 UOTAN_FIRMWARE_CORPUS 覆盖）存在时，
/// 对真实生产文件执行元数据/结构解析校验；语料缺失或样本不在语料库中时以 Assert.Skip 跳过。
/// <para>Real-corpus deep suite: when the firmware corpus (default D:\Firmware, overridable via
/// UOTAN_FIRMWARE_CORPUS) is present, parses and validates real production files; cases are
/// skipped via Assert.Skip when the corpus or the sample is missing.</para>
/// </summary>
public class RealCorpusDeepTests
{
    private static string? Resolve(string fileName)
    {
        var path = FirmwareCorpus.FindFile(fileName);
        if (path is null)
        {
            Assert.Skip($"{fileName} not found in firmware corpus");
        }

        return path;
    }

    [Fact]
    public void Ext4_SM7150SystemImage_ParsesMetadataAndWalksTree()
    {
        var path = Resolve("system_a.img");
        using var parser = new Ext4Parser(path);
        var files = parser.ListFiles();

        Assert.True(files.Count > 100, $"expected a populated system image, got {files.Count} entries");
        Assert.True(files.Any(f => f.Path.Contains("build.prop", StringComparison.OrdinalIgnoreCase)
                                   || f.Path.Contains("framework", StringComparison.OrdinalIgnoreCase)),
            "system image tree should contain system content");
    }

    [Fact]
    public void Avb_SM7150Vbmeta_ParsesMetadata()
    {
        var path = Resolve("vbmeta_a.img");
        FirmwareFormatRegistry.RegisterAll();
        var handler = FormatDetector.Detect(path);
        Assert.NotNull(handler);
        Assert.Contains("Verified Boot", handler!.FormatName, StringComparison.OrdinalIgnoreCase);

        var result = handler.ReadMetadataAsync(path, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Assert.True(result.Success, result.Summary);
        Assert.NotNull(result.Metadata);
    }

    [Fact]
    public void Boot_SM7150BootImage_ParsesMetadata()
    {
        var path = Resolve("boot_a.img");
        FirmwareFormatRegistry.RegisterAll();
        var handler = FormatDetector.Detect(path);
        Assert.NotNull(handler);
        Assert.Contains("Boot", handler!.FormatName, StringComparison.OrdinalIgnoreCase);

        var result = handler.ReadMetadataAsync(path, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Assert.True(result.Success, result.Summary);
    }

    [Fact]
    public void Payload_RootPayloadBin_ReadsManifest()
    {
        var path = Resolve("payload.bin");
        FirmwareFormatRegistry.RegisterAll();
        var handler = FormatDetector.Detect(path);
        Assert.NotNull(handler);
        Assert.Contains("Payload", handler!.FormatName, StringComparison.OrdinalIgnoreCase);

        // 仅读取 manifest 元数据（有界头部读取），不解包 7.4GB 镜像。
        // Reads manifest metadata only (bounded head reads), never unpacks the 7.4 GB image.
        var result = handler.ReadMetadataAsync(path, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Assert.True(result.Success, result.Summary);
        Assert.True(result.Entries.Count > 10, "payload manifest should list many partitions");
    }

    [Fact]
    public void Huawei_RootUpdateApp_ReadsMetadata()
    {
        var path = Resolve("UPDATE.APP");
        FirmwareFormatRegistry.RegisterAll();
        var handler = FormatDetector.Detect(path);
        Assert.NotNull(handler);
        Assert.Contains("update.app", handler!.FormatName, StringComparison.OrdinalIgnoreCase);

        var result = handler.ReadMetadataAsync(path, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Assert.True(result.Success, result.Summary);
    }

    [Fact]
    public void Oppo_RootOzip_ReadsMetadata()
    {
        var path = Resolve("1.ozip");
        FirmwareFormatRegistry.RegisterAll();
        var handler = FormatDetector.Detect(path);
        Assert.NotNull(handler);
        Assert.Contains("Oppo", handler!.FormatName, StringComparison.OrdinalIgnoreCase);

        var result = handler.ReadMetadataAsync(path, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Assert.True(result.Success, result.Summary);
        Assert.True(result.Components.Count > 10 || result.Entries.Count > 10,
            "ozip should list its internal entries or firmware components");
    }

    [Fact]
    public void Dt_SM7150Dtbo_ReadsMetadata()
    {
        var path = Resolve("dtbo_a.img");
        FirmwareFormatRegistry.RegisterAll();
        var handler = FormatDetector.Detect(path);
        Assert.NotNull(handler);
        Assert.Contains("Device Tree", handler!.FormatName, StringComparison.OrdinalIgnoreCase);

        var result = handler.ReadMetadataAsync(path, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Assert.True(result.Success, result.Summary);
    }
}