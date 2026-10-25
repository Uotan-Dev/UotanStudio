using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class HdcSessionHandleTests
{
    [Fact]
    public void Dispose_SharedReference_ReleasesExactlyOnce()
    {
        var releases = 0;
        var handle = HdcSessionHandle.ForDirect(() => releases++);

        handle.Dispose();
        handle.Dispose();

        Assert.Equal(1, releases);
        Assert.True(handle.SharesConnection);
    }

    [Fact]
    public async Task DisposeAsync_SharedReference_ReleasesExactlyOnce()
    {
        var releases = 0;
        var handle = HdcSessionHandle.ForDirect(() => releases++);

        await handle.DisposeAsync();
        await handle.DisposeAsync();

        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task Dispose_AfterDisposeAsync_DoesNotReleaseTwice()
    {
        var releases = 0;
        var handle = HdcSessionHandle.ForDirect(() => releases++);

        await handle.DisposeAsync();
        handle.Dispose();

        Assert.Equal(1, releases);
    }
}