using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Firmware.BlockImage.Sparse.Models;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Fastboot;

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
        transport.Enqueue("OKAYyes");
        transport.Enqueue("OKAY");

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
        transport.Enqueue("OKAYyes");
        transport.Enqueue("OKAYa");
        transport.Enqueue("OKAYyes");
        transport.Enqueue("OKAY");

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
        transport.Enqueue("OKAYno");

        using var session = NewSession(transport);
        var target = FlashService.PrepareFlashTarget(session, "boot_a", 4096, progress: null);

        Assert.Equal("boot_a", target);
        var commands = Commands(transport);
        Assert.DoesNotContain(commands, c => c.StartsWith("resize-logical-partition:", StringComparison.Ordinal));
    }

    [Fact]
    public void PrepareFlashTarget_SkipsResize_WhenDeviceRejectsIsLogical()
    {
        var transport = new FakeCommTransport();
        transport.Enqueue("FAILno such partition");

        using var session = NewSession(transport);
        var target = FlashService.PrepareFlashTarget(session, "vendor_dlkm_a", 4096, progress: null);

        Assert.Equal("vendor_dlkm_a", target);
        var commands = Commands(transport);
        Assert.DoesNotContain(commands, c => c.StartsWith("resize-logical-partition:", StringComparison.Ordinal));
    }

    [Fact]
    public void PrepareFlashTarget_ContinuesFlash_WhenResizeFails()
    {
        var transport = new FakeCommTransport();
        transport.Enqueue("OKAYyes");
        transport.Enqueue("FAILNot enough space to resize partition");

        using var session = NewSession(transport);
        var target = FlashService.PrepareFlashTarget(session, "system_ext_a", 4096, progress: null);

        Assert.Equal("system_ext_a", target);
        Assert.Contains("resize-logical-partition:system_ext_a:4096", Commands(transport));
    }

    [Fact]
    public void PrepareFlashTarget_SkipsResize_WhenImageSizeUnknown()
    {
        var transport = new FakeCommTransport();
        transport.Enqueue("OKAYyes");

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

        using (var sparse = new SparseFile(BlockSize, BlockSize * 4))
        {
            sparse.AddRawChunk(new byte[BlockSize]);
            using var fs = File.Create(imagePath);
            sparse.WriteToStream(fs, sparse: true, cancellationToken: TestContext.Current.CancellationToken);
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
        transport.Enqueue("OKAYyes");
        transport.Enqueue("OKAY");
        transport.Enqueue("OKAY");
        transport.Enqueue("DATA00002000");
        transport.Enqueue("OKAY");
        transport.Enqueue("OKAY");

        using var session = NewSession(transport);
        var target = FlashService.PrepareFlashTarget(
            session, "product_a", FlashService.GetImageLogicalSize(imagePath), progress: null);
        using (var image = File.OpenRead(imagePath))
        {
            session.FlashPartition(
                target, image, image.Length, progress: null,
                TestContext.Current.CancellationToken, resolveSlot: false).ThrowIfFailed();
        }

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