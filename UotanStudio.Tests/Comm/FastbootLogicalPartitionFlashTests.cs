using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm;

/// <summary>
/// Logical partition flash preparation tests: fastbootd rejects an image larger than the current
/// logical partition with <c>FAIL EOVERFLOW</c> ("Value too large for defined data type"), so the
/// client must resize the partition to the image's expanded size before <c>flash:</c>, exactly like
/// AOSP fastboot <c>do_flash</c>. These tests assert the emitted command sequence and the
/// sparse-aware image size calculation.
/// <para>逻辑分区刷写准备测试：fastbootd 会以 <c>FAIL EOVERFLOW</c>（"Value too large for defined
/// data type"）拒绝大于当前逻辑分区的镜像，因此客户端必须先按镜像展开尺寸调整分区，再发
/// <c>flash:</c>，与 AOSP fastboot <c>do_flash</c> 一致。测试断言命令序列与稀疏感知的镜像尺寸计算。</para>
/// </summary>
public class FastbootLogicalPartitionFlashTests
{
    private const uint BlockSize = 4096;

    private static FastbootSession NewSession(FakeCommTransport transport)
        => new(transport, timeoutMs: 3000);

    private static List<string> Commands(FakeCommTransport transport)
        => [.. transport.Writes.Select(Encoding.UTF8.GetString)];

    [Fact]
    public void PrepareFlashTarget_ResizesLogicalPartition_ToImageSize()
    {
        var transport = new FakeCommTransport();
        transport.Enqueue("OKAYyes");   // getvar:is-logical:product_a
        transport.Enqueue("OKAY");      // resize-logical-partition:product_a:16384

        using var session = NewSession(transport);
        var target = FlashService.PrepareFlashTarget(session, "product_a", 16384, progress: null);

        Assert.Equal("product_a", target);
        Assert.Equal(
            ["getvar:is-logical:product_a", "resize-logical-partition:product_a:16384"],
            Commands(transport));
    }

    [Fact]
    public void PrepareFlashTarget_ResolvesActiveSlot_ThenResizesSlottedPartition()
    {
        var transport = new FakeCommTransport();
        transport.Enqueue("OKAYyes");        // getvar:has-slot:product
        transport.Enqueue("OKAYa");          // getvar:current-slot
        transport.Enqueue("OKAYyes");        // getvar:is-logical:product_a
        transport.Enqueue("OKAY");           // resize-logical-partition:product_a:8192

        using var session = NewSession(transport);
        var target = FlashService.PrepareFlashTarget(session, "product", 8192, progress: null);

        Assert.Equal("product_a", target);
        Assert.Equal(
            [
                "getvar:has-slot:product",
                "getvar:current-slot",
                "getvar:is-logical:product_a",
                "resize-logical-partition:product_a:8192"
            ],
            Commands(transport));
    }

    [Fact]
    public void PrepareFlashTarget_SkipsResize_ForPhysicalPartition()
    {
        var transport = new FakeCommTransport();
        transport.Enqueue("OKAYno");         // getvar:is-logical:boot_a

        using var session = NewSession(transport);
        var target = FlashService.PrepareFlashTarget(session, "boot_a", 4096, progress: null);

        Assert.Equal("boot_a", target);
        var commands = Commands(transport);
        Assert.DoesNotContain(commands, c => c.StartsWith("resize-logical-partition:", StringComparison.Ordinal));
    }

    [Fact]
    public void PrepareFlashTarget_SkipsResize_WhenDeviceRejectsIsLogical()
    {
        // Bootloader fastboot (pre-fastbootd) fails is-logical: nothing to resize, and the
        // partition must still be flashed as-is instead of failing the whole operation.
        // 旧式 bootloader fastboot 的 is-logical 会 FAIL：无分区可调整，仍按原样返回以便继续刷写。
        var transport = new FakeCommTransport();
        transport.Enqueue("FAILno such partition");  // getvar:is-logical:vendor_dlkm_a

        using var session = NewSession(transport);
        var target = FlashService.PrepareFlashTarget(session, "vendor_dlkm_a", 4096, progress: null);

        Assert.Equal("vendor_dlkm_a", target);
        var commands = Commands(transport);
        Assert.DoesNotContain(commands, c => c.StartsWith("resize-logical-partition:", StringComparison.Ordinal));
    }

    [Fact]
    public void PrepareFlashTarget_ContinuesFlash_WhenResizeFails()
    {
        // AOSP fastboot ignores a failed resize and still attempts the flash: a partition that is
        // already large enough remains writable.
        // AOSP fastboot 忽略调整失败并继续刷写：分区已足够大时仍可写入。
        var transport = new FakeCommTransport();
        transport.Enqueue("OKAYyes");                              // getvar:is-logical:system_ext_a
        transport.Enqueue("FAILNot enough space to resize partition"); // resize-logical-partition

        using var session = NewSession(transport);
        var target = FlashService.PrepareFlashTarget(session, "system_ext_a", 4096, progress: null);

        Assert.Equal("system_ext_a", target);
        Assert.Contains("resize-logical-partition:system_ext_a:4096", Commands(transport));
    }

    [Fact]
    public void PrepareFlashTarget_SkipsResize_WhenImageSizeUnknown()
    {
        var transport = new FakeCommTransport();
        transport.Enqueue("OKAYyes");        // getvar:is-logical:mi_ext_a

        using var session = NewSession(transport);
        var target = FlashService.PrepareFlashTarget(session, "mi_ext_a", imageSize: 0, progress: null);

        Assert.Equal("mi_ext_a", target);
        var commands = Commands(transport);
        Assert.DoesNotContain(commands, c => c.StartsWith("resize-logical-partition:", StringComparison.Ordinal));
    }

    [Fact]
    public void GetImageLogicalSize_UsesSparseExpandedSize()
    {
        using var dir = new TempDirectory();
        var imagePath = Path.Combine(dir.Path, "product_a.img");

        // 4 logical blocks, but only one RAW block on disk: the device validates the expanded size.
        // 逻辑上 4 个块，磁盘上只有 1 个 RAW 块：设备校验的是展开尺寸。
        using (var sparse = new SparseFile(BlockSize, BlockSize * 4))
        {
            sparse.AddRawChunk(new byte[BlockSize]);
            using var fs = File.Create(imagePath);
            sparse.WriteToStream(fs, sparse: true);
        }

        var logicalSize = FlashService.GetImageLogicalSize(imagePath);

        Assert.Equal(BlockSize * 4, logicalSize);
        Assert.True(new FileInfo(imagePath).Length < logicalSize, "the sparse file must be smaller than its expanded size");
    }

    [Fact]
    public void GetImageLogicalSize_UsesFileLength_ForRawImage()
    {
        using var dir = new TempDirectory();
        var imagePath = dir.WriteBytes("boot_a.img", new byte[0x3000]);

        Assert.Equal(0x3000, FlashService.GetImageLogicalSize(imagePath));
    }

    [Fact]
    public void GetImageLogicalSize_UsesFileLength_ForTruncatedFile()
    {
        using var dir = new TempDirectory();
        var imagePath = dir.WriteBytes("tiny.img", new byte[4]);

        Assert.Equal(4, FlashService.GetImageLogicalSize(imagePath));
    }

    [Fact]
    public void PrepareFlashTarget_ThenFlashPartition_ResizesBeforeDownloadAndFlash()
    {
        using var dir = new TempDirectory();
        var imagePath = dir.WriteBytes("product_a.img", new byte[0x2000]);

        var transport = new FakeCommTransport();
        transport.Enqueue("OKAYyes");                       // getvar:is-logical:product_a
        transport.Enqueue("OKAY");                          // resize-logical-partition:product_a:8192
        transport.Enqueue("OKAY");                          // getvar:max-download-size (empty -> fallback)
        transport.Enqueue("DATA00002000");                  // download:00002000
        transport.Enqueue("OKAY");                          // payload accepted
        transport.Enqueue("OKAY");                          // flash:product_a

        using var session = NewSession(transport);
        var target = FlashService.PrepareFlashTarget(
            session, "product_a", FlashService.GetImageLogicalSize(imagePath), progress: null);
        using (var image = File.OpenRead(imagePath))
        {
            session.FlashPartition(
                target, image, image.Length, progress: null,
                TestContext.Current.CancellationToken, resolveSlot: false).ThrowIfFailed();
        }

        // The binary download payload is filtered out so only protocol commands remain.
        // 过滤掉下载的二进制负载，只保留协议命令。
        var commands = Commands(transport)
            .Where(c => c.StartsWith("getvar:", StringComparison.Ordinal)
                        || c.StartsWith("resize-logical-partition:", StringComparison.Ordinal)
                        || c.StartsWith("download:", StringComparison.Ordinal)
                        || c.StartsWith("flash:", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            [
                "getvar:is-logical:product_a",
                "resize-logical-partition:product_a:8192",
                "getvar:max-download-size",
                "download:00002000",
                "flash:product_a"
            ],
            commands);
    }
}