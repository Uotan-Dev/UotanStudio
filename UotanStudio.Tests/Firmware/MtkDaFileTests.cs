using UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public partial class MtkDaContainerTests
{
    [Theory]
    [InlineData(MtkDaRegionContent.Complete, 64)]
    [InlineData(MtkDaRegionContent.WithoutSignatureSuffix, 48)]
    public async Task OpenRegion_ExplicitContent_ReturnsOnlySelectedBytes(MtkDaRegionContent content, int length)
    {
        using var dir = new TempDirectory();
        var bytes = ContainerWithData();
        var path = dir.WriteBytes("loader.bin", bytes);
        using var file = MtkDaFile.Open(path, cancellationToken: TestContext.Current.CancellationToken);
        using var region = file.OpenRegion(0, 1, content);
        using var result = new MemoryStream();

        await region.CopyToAsync(result, TestContext.Current.CancellationToken);

        Assert.Equal(length, region.Length);
        Assert.Equal(bytes.AsSpan(1056, length).ToArray(), result.ToArray());
        Assert.Equal(-1, region.ReadByte());
        Assert.False(region.CanWrite);
        Assert.Throws<IOException>(() => region.Seek(1, SeekOrigin.End));
        Assert.Throws<IOException>(() => region.Seek(-1, SeekOrigin.Begin));
    }

    [Fact]
    public async Task OpenRegion_ConcurrentRegions_KeepIndependentCursors()
    {
        using var dir = new TempDirectory();
        var bytes = ContainerWithData();
        using var file = MtkDaFile.Open(dir.WriteBytes("loader.bin", bytes),
            cancellationToken: TestContext.Current.CancellationToken);
        using var first = file.OpenRegion(0, 0, MtkDaRegionContent.Complete);
        using var second = file.OpenRegion(0, 1, MtkDaRegionContent.Complete);
        var left = new byte[32];
        var right = new byte[64];
        Assert.Equal(bytes[1056], second.ReadByte());
        Assert.Equal(bytes[1024], first.ReadByte());
        left[0] = bytes[1024];
        right[0] = bytes[1056];

        await Task.WhenAll(
            first.ReadExactlyAsync(left.AsMemory(1), TestContext.Current.CancellationToken).AsTask(),
            second.ReadExactlyAsync(right.AsMemory(1), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(bytes.AsSpan(1024, 32).ToArray(), left);
        Assert.Equal(bytes.AsSpan(1056, 64).ToArray(), right);
        first.Position = 7;
        second.Position = 9;
        Assert.Equal(bytes[1031], first.ReadByte());
        Assert.Equal(bytes[1065], second.ReadByte());
    }

    [Fact]
    public void OpenRegion_DisposedView_DoesNotCloseOwner()
    {
        using var dir = new TempDirectory();
        using var file = MtkDaFile.Open(dir.WriteBytes("loader.bin", ContainerWithData()),
            cancellationToken: TestContext.Current.CancellationToken);
        var region = file.OpenRegion(0, 0, MtkDaRegionContent.Complete);
        region.Dispose();
        using var next = file.OpenRegion(0, 1, MtkDaRegionContent.WithoutSignatureSuffix);

        Assert.Equal(0x80, next.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => region.ReadByte());
    }

    [Fact]
    public void OpenRegion_DisposedOwner_RejectsNewViews()
    {
        using var dir = new TempDirectory();
        var file = MtkDaFile.Open(dir.WriteBytes("loader.bin", ContainerWithData()),
            cancellationToken: TestContext.Current.CancellationToken);
        file.Dispose();
        file.Dispose();

        Assert.Throws<ObjectDisposedException>(() => file.OpenRegion(0, 0, MtkDaRegionContent.Complete));
    }

    [Theory]
    [InlineData(-1, 0, MtkDaRegionContent.Complete)]
    [InlineData(1, 0, MtkDaRegionContent.Complete)]
    [InlineData(0, -1, MtkDaRegionContent.Complete)]
    [InlineData(0, 2, MtkDaRegionContent.Complete)]
    [InlineData(0, 0, (MtkDaRegionContent)42)]
    public void OpenRegion_InvalidSelection_Rejects(int entry, int region, MtkDaRegionContent content)
    {
        using var dir = new TempDirectory();
        using var file = MtkDaFile.Open(dir.WriteBytes("loader.bin", ContainerWithData()),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Throws<ArgumentOutOfRangeException>(() => file.OpenRegion(entry, region, content));
    }

    [Fact]
    public void Open_InvalidContainer_ReleasesFileHandle()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("loader.bin", new byte[108]);

        Assert.Throws<InvalidDataException>(() => MtkDaFile.Open(path, cancellationToken: TestContext.Current.CancellationToken));

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(108, exclusive.Length);
    }

    [Fact]
    public async Task OpenRegion_PreCancelledRead_PreservesCursor()
    {
        using var dir = new TempDirectory();
        using var file = MtkDaFile.Open(dir.WriteBytes("loader.bin", ContainerWithData()),
            cancellationToken: TestContext.Current.CancellationToken);
        using var region = file.OpenRegion(0, 1, MtkDaRegionContent.Complete);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await region.ReadExactlyAsync(new byte[16], cancellation.Token));

        Assert.Equal(0, region.Position);
    }

    private static byte[] ContainerWithData()
    {
        var bytes = Container(MtkDaDirectoryLayout.Modern);
        bytes.AsSpan(1024, 32).Fill(0x11);
        for (var index = 0; index < 48; index++)
        {
            bytes[1056 + index] = (byte)(0x80 + index);
        }
        bytes.AsSpan(1104, 16).Fill(0xEE);
        return bytes;
    }
}
