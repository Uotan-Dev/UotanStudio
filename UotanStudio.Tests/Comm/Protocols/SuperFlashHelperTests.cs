using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Fastboot.Utils;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

/// <summary>
/// Super flash helper tests: the userspace-fastboot command sequences emitted for
/// update-super / create / delete / resize / wipe, and the full
/// <see cref="SuperFlashHelper.FlashSuper"/> flow (metadata download + update-super,
/// then one download + flash per logical partition) asserted against a scripted transport.
/// <para>Super 刷写辅助测试：update-super / create / delete / resize / wipe 发出的用户空间
/// fastboot 命令序列，以及完整 <see cref="SuperFlashHelper.FlashSuper"/> 流程
/// （元数据下载 + update-super，随后每个逻辑分区一次 download + flash）基于脚本化传输断言。</para>
/// </summary>
public class SuperFlashHelperTests
{
    private const uint MetadataMaxSize = 64 * 1024;
    private const uint MetadataSlotCount = 2;

    private static (byte[] Image, SuperImageMetadata Metadata) BuildFixture()
    {
        var builder = new SuperImageBuilder();
        var image = builder.Build(
        [
            new SuperBuildPartition("system", "default", 0x10000),
            new SuperBuildPartition("vendor", "default", 0x8000)
        ],
        deviceSize: 8 * 1024 * 1024,
        MetadataMaxSize,
        MetadataSlotCount);

        var metadata = builder.ReadMetadata(image) ?? throw new InvalidOperationException("fixture must be readable");
        return (image, metadata);
    }

    private static FastbootSession NewSession(ScriptedCommTransport transport)
        => new(transport, timeoutMs: 3000);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static byte[] Slice(byte[] data, long offset, int length)
    {
        var result = new byte[length];
        Array.Copy(data, offset, result, 0, length);
        return result;
    }

    // ============== simple command sequences ==============

    [Fact]
    public void UpdateSuperMetadata_IssuesDownloadThenUpdateSuper()
    {
        using var dir = new TempDirectory();
        var metadataPath = dir.WriteBytes("metadata.img", new byte[4096]);

        var transport = new ScriptedCommTransport();
        transport.Enqueue("OKAY");                  // getvar:max-download-size
        transport.Enqueue("DATA00001000");          // download:00001000
        transport.Enqueue("OKAY");                  // payload accepted
        transport.Enqueue("OKAY");                  // update-super:super

        using var session = NewSession(transport);
        var helper = new SuperFlashHelper(session);
        var response = helper.UpdateSuperMetadata(metadataPath);

        Assert.Equal(FastbootResponseState.Success, response.State);
        Assert.Equal(4, transport.Writes.Count);
        Assert.Equal("getvar:max-download-size", Text(transport.Writes[0]));
        Assert.Equal("download:00001000", Text(transport.Writes[1]));
        Assert.Equal("update-super:super", Text(transport.Writes[3]));
        // The metadata payload bytes are the file contents, streamed verbatim.
        // 元数据负载字节即文件内容，原样流式传输。
        Assert.Equal(new byte[4096], transport.Writes[2]);
    }

    [Fact]
    public void UpdateSuperMetadata_Wipe_AppendsWipeSuffix()
    {
        using var dir = new TempDirectory();
        var metadataPath = dir.WriteBytes("metadata.img", new byte[512]);

        var transport = new ScriptedCommTransport();
        transport.Enqueue("OKAY");
        transport.Enqueue("DATA00000200");
        transport.Enqueue("OKAY");
        transport.Enqueue("OKAY");

        using var session = NewSession(transport);
        var helper = new SuperFlashHelper(session);
        var response = helper.UpdateSuperMetadata(metadataPath, wipe: true);

        Assert.Equal(FastbootResponseState.Success, response.State);
        Assert.Equal("update-super:super:wipe", Text(transport.Writes[3]));
    }

    [Fact]
    public void LogicalPartitionCommands_IssueExpectedCommands()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("OKAY");
        transport.Enqueue("OKAY");
        transport.Enqueue("OKAY");
        transport.Enqueue("OKAY");

        using var session = NewSession(transport);
        var helper = new SuperFlashHelper(session);

        Assert.Equal(FastbootResponseState.Success, helper.CreateLogicalPartition("system", 0x10000).State);
        Assert.Equal(FastbootResponseState.Success, helper.DeleteLogicalPartition("vendor").State);
        Assert.Equal(FastbootResponseState.Success, helper.ResizeLogicalPartition("vendor", 0x20000).State);
        Assert.Equal(FastbootResponseState.Success, helper.WipeSuper().State);

        Assert.Equal(4, transport.Writes.Count);
        Assert.Equal("create-logical-partition:system:65536", Text(transport.Writes[0]));
        Assert.Equal("delete-logical-partition:vendor", Text(transport.Writes[1]));
        Assert.Equal("resize-logical-partition:vendor:131072", Text(transport.Writes[2]));
        Assert.Equal("wipe-super:super", Text(transport.Writes[3]));
    }

    // ============== FlashSuper ==============

    [Fact]
    public void FlashSuper_AppliesMetadataThenFlashesEachPartition()
    {
        var (image, metadata) = BuildFixture();
        using var dir = new TempDirectory();
        var superPath = dir.WriteBytes("super.img", image);
        var transport = new ScriptedCommTransport();

        var firstLogicalSector = metadata.BlockDevices[0].FirstLogicalSector;
        var metadataImageLength = (int)(firstLogicalSector * SuperImageBuilder.LpSectorSize);
        var systemExtent = metadata.Extents[0];
        var vendorExtent = metadata.Extents[1];

        // device responses for: getvar, metadata download, update-super,
        // then per partition: download, flash.
        // 设备响应：getvar、元数据下载、update-super，随后每分区 download + flash。
        transport.Enqueue("OKAY");                                      // getvar:max-download-size
        transport.Enqueue($"DATA{metadataImageLength:x8}");             // download metadata
        transport.Enqueue("OKAY");
        transport.Enqueue("OKAY");                                      // update-super:super
        transport.Enqueue($"DATA{systemExtent.NumSectors * SuperImageBuilder.LpSectorSize:x8}");
        transport.Enqueue("OKAY");
        transport.Enqueue("OKAY");                                      // flash:system
        transport.Enqueue($"DATA{vendorExtent.NumSectors * SuperImageBuilder.LpSectorSize:x8}");
        transport.Enqueue("OKAY");
        transport.Enqueue("OKAY");                                      // flash:vendor

        using var session = NewSession(transport);
        var helper = new SuperFlashHelper(session);
        helper.FlashSuper(superPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(10, transport.Writes.Count);
        Assert.Equal("getvar:max-download-size", Text(transport.Writes[0]));
        Assert.Equal($"download:{metadataImageLength:x8}", Text(transport.Writes[1]));
        // The update-super payload is everything before the first logical sector.
        // update-super 负载是首个逻辑扇区之前的全部内容。
        Assert.Equal(image[..metadataImageLength], transport.Writes[2]);
        Assert.Equal("update-super:super", Text(transport.Writes[3]));

        var systemPayload = Slice(image, (long)((firstLogicalSector + systemExtent.TargetData) * SuperImageBuilder.LpSectorSize), (int)(systemExtent.NumSectors * SuperImageBuilder.LpSectorSize));
        var vendorPayload = Slice(image, (long)((firstLogicalSector + vendorExtent.TargetData) * SuperImageBuilder.LpSectorSize), (int)(vendorExtent.NumSectors * SuperImageBuilder.LpSectorSize));

        Assert.Equal($"download:{systemPayload.Length:x8}", Text(transport.Writes[4]));
        Assert.Equal(systemPayload, transport.Writes[5]);
        Assert.Equal("flash:system", Text(transport.Writes[6]));
        Assert.Equal($"download:{vendorPayload.Length:x8}", Text(transport.Writes[7]));
        Assert.Equal(vendorPayload, transport.Writes[8]);
        Assert.Equal("flash:vendor", Text(transport.Writes[9]));
    }

    [Fact]
    public void FlashSuper_Wipe_AppendsWipeSuffixToUpdateSuper()
    {
        var (image, metadata) = BuildFixture();
        using var dir = new TempDirectory();
        var superPath = dir.WriteBytes("super.img", image);
        var transport = new ScriptedCommTransport();

        var firstLogicalSector = metadata.BlockDevices[0].FirstLogicalSector;
        var metadataImageLength = (int)(firstLogicalSector * SuperImageBuilder.LpSectorSize);
        var systemSize = (int)(metadata.Extents[0].NumSectors * SuperImageBuilder.LpSectorSize);
        var vendorSize = (int)(metadata.Extents[1].NumSectors * SuperImageBuilder.LpSectorSize);

        transport.Enqueue("OKAY");
        transport.Enqueue($"DATA{metadataImageLength:x8}");
        transport.Enqueue("OKAY");
        transport.Enqueue("OKAY");                          // update-super:super:wipe
        transport.Enqueue($"DATA{systemSize:x8}");
        transport.Enqueue("OKAY");
        transport.Enqueue("OKAY");                          // flash:system
        transport.Enqueue($"DATA{vendorSize:x8}");          // vendor 分区同样需要 download + flash 响应
        transport.Enqueue("OKAY");
        transport.Enqueue("OKAY");                          // flash:vendor

        using var session = NewSession(transport);
        new SuperFlashHelper(session).FlashSuper(superPath, wipe: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(transport.Writes, w => Text(w) == "update-super:super:wipe");
        Assert.Equal("flash:system", Text(transport.Writes[6]));
        Assert.Equal("flash:vendor", Text(transport.Writes[9]));
    }

    [Fact]
    public void FlashSuper_MissingFile_ThrowsFileNotFound()
    {
        var transport = new ScriptedCommTransport();
        using var session = NewSession(transport);
        var helper = new SuperFlashHelper(session);

        Assert.Throws<FileNotFoundException>(() =>
            helper.FlashSuper(Path.Combine(Path.GetTempPath(), $"uotan-missing-super-{Guid.NewGuid():N}.img"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void FlashSuper_NonSuperFile_ThrowsInvalidData()
    {
        using var dir = new TempDirectory();
        var notSuper = dir.WriteBytes("junk.img", new byte[16384]);

        var transport = new ScriptedCommTransport();
        using var session = NewSession(transport);
        var helper = new SuperFlashHelper(session);

        Assert.Throws<InvalidDataException>(() => helper.FlashSuper(notSuper, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(transport.Writes);
    }

    // ============== delegation helpers ==============

    [Fact]
    public void IsSuperImageAndReadLogicalPartitions_DelegateToBuilder()
    {
        var (image, _) = BuildFixture();
        using var dir = new TempDirectory();
        var superPath = dir.WriteBytes("super.img", image);
        var transport = new ScriptedCommTransport();

        using var session = NewSession(transport);
        var helper = new SuperFlashHelper(session);

        Assert.True(helper.IsSuperImage(superPath));
        Assert.False(helper.IsSuperImage(dir.Write("junk.bin", "nope")));

        var partitions = helper.ReadLogicalPartitions(superPath);
        Assert.Equal(["system", "vendor"], partitions.Select(p => p.Name));
    }

    [Fact]
    public void MainPartition_DefaultsToSuper_AndRejectsBlank()
    {
        var transport = new ScriptedCommTransport();
        using var session = NewSession(transport);

        Assert.Equal("super", new SuperFlashHelper(session).MainPartition);
        Assert.Equal("super", new SuperFlashHelper(session, "  ").MainPartition);
        Assert.Equal("custom", new SuperFlashHelper(session, "custom").MainPartition);
    }
}