using UotanStudio.Services.Launch;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

public class DesktopInstanceRelayTests
{
    [Fact]
    public void StartInDirectory_PrimaryInstance_PublishesLockAndPipeName()
    {
        using var temp = new TempDirectory();
        using var primary = DesktopInstanceRelay.StartInDirectory(temp.Path, [], null);

        Assert.NotNull(primary);
        Assert.True(File.Exists(Path.Combine(temp.Path, "desktop-instance.lock")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "desktop-instance.pipe")));
    }

    [Fact]
    public void StartInDirectory_AfterPrimaryDisposed_StartsANewPrimary()
    {
        using var temp = new TempDirectory();
        var first = DesktopInstanceRelay.StartInDirectory(temp.Path, [], null);
        Assert.NotNull(first);
        first!.Dispose();

        using var second = DesktopInstanceRelay.StartInDirectory(temp.Path, [], null);

        Assert.NotNull(second);
    }

    [Fact]
    public async Task StartInDirectory_SecondLaunch_ForwardsFilesAndMirrorToPrimary()
    {
        using var temp = new TempDirectory();
        var image = temp.WriteBytes("boot.img", [0x41, 0x42, 0x43]);
        using var primary = DesktopInstanceRelay.StartInDirectory(temp.Path, [], null);
        Assert.NotNull(primary);
        var received = new TaskCompletionSource<DesktopLaunchRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary!.SetLaunchHandler(request =>
        {
            received.TrySetResult(request);
        });

        var secondary = DesktopInstanceRelay.StartInDirectory(temp.Path, [image], "SERIAL42");

        Assert.Null(secondary);
        var forwarded = await received.Task
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { image }, forwarded.Files);
        Assert.Equal("SERIAL42", forwarded.ScreenMirrorSerial);
    }
}