using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

public sealed class FlashViewModelCommandTests
{
    [Fact]
    public async Task FlashImageAsync_TargetAndImage_ForwardsThemToTheFlashBackend()
    {
        var target = new DeviceTarget(new DetectedDevice
        {
            Key = "usb:flash",
            Serial = "FLASH01",
            Protocol = DeviceProtocolKind.Fastboot,
            Transport = DeviceTransportKind.Usb
        });
        var backend = new RecordingFlashService();
        var model = new FlashViewModel(backend);
        var cancellationToken = TestContext.Current.CancellationToken;

        var result = await model.FlashImageAsync(target, "boot", "/images/boot.img", progress: null,
            cancellationToken);

        Assert.True(result.Success);
        var call = Assert.IsType<FlashCall>(backend.LastFlash);
        Assert.Same(target, call.Target);
        Assert.Equal("boot", call.Partition);
        Assert.Equal("/images/boot.img", call.ImagePath);
        Assert.Equal(cancellationToken, call.CancellationToken);
    }

    [Fact]
    public async Task FlashImageAsync_CancelledToken_PropagatesCancellationToTheFlashBackend()
    {
        var target = new DeviceTarget(new DetectedDevice
        {
            Key = "usb:flash",
            Serial = "FLASH01",
            Protocol = DeviceProtocolKind.Fastboot,
            Transport = DeviceTransportKind.Usb
        });
        var backend = new RecordingFlashService();
        var model = new FlashViewModel(backend);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => model.FlashImageAsync(target, "boot", "/images/boot.img", progress: null,
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var call = Assert.IsType<FlashCall>(backend.LastFlash);
        Assert.Equal(cancellation.Token, call.CancellationToken);
    }

    private sealed class RecordingFlashService : IFlashService
    {
        public FlashCall? LastFlash { get; private set; }

        public Task<FlashResult> FlashImageAsync(DeviceTarget device, string partition, string imagePath,
            IProgress<FlashProgress>? progress, CancellationToken ct)
        {
            LastFlash = new FlashCall(device, partition, imagePath, ct);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new FlashResult { Success = true, Summary = "ok" });
        }

        public Task<FlashResult> ErasePartitionAsync(DeviceTarget device, string partition,
            IProgress<FlashProgress>? progress, CancellationToken ct) => throw new NotSupportedException();

        public Task<FlashResult> EraseAllAsync(DeviceTarget device, IProgress<FlashProgress>? progress,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<FlashResult> FlashSideloadAsync(DeviceTarget device, string otaPackagePath,
            IProgress<FlashProgress>? progress, CancellationToken ct) => throw new NotSupportedException();

        public Task<FlashResult> GetDevicePropertyAsync(DeviceTarget device, string propertyName,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed record FlashCall(DeviceTarget Target, string Partition, string ImagePath,
        CancellationToken CancellationToken);
}