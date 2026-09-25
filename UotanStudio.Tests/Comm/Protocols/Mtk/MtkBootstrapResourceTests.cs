using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Fact]
    public async Task ConnectSerialAsync_InvalidResourcePolicy_DoesNotOpenPortOrProvider()
    {
        using var directory = new TempDirectory();
        var options = BootstrapOptions(directory) with
        {
            BootResourceOrder = [MtkBootResourceKind.Authentication]
        };
        var opens = 0;

        await Assert.ThrowsAsync<ArgumentException>(() => MtkXFlashBootstrap.ConnectSerialCoreAsync(
            ExplicitSerial(), options, (_, _) =>
            {
                opens++;
                throw new InvalidOperationException();
            }, TestContext.Current.CancellationToken));

        Assert.Equal(0, opens);
    }

    [Fact]
    public async Task BootstrapInitializeAsync_IdentityMismatch_DoesNotAcquireSecurityResources()
    {
        using var directory = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        var calls = 0;
        var options = BootstrapOptions(directory) with
        {
            BootResourceOrder = [MtkBootResourceKind.Authentication],
            BootResourceProvider = new BootResourceProvider((_, _, _) =>
            {
                calls++;
                throw new InvalidOperationException("Must not call provider.");
            })
        };
        var loader = BootstrapContainer();
        loader[110] = 0x67;
        directory.WriteBytes("loader.bin", loader);

        await Assert.ThrowsAsync<InvalidOperationException>(() => MtkXFlashBootstrap.InitializeAsync(
            transport, options, TestContext.Current.CancellationToken));

        Assert.Equal(0, calls);
        Assert.Equal(7, transport.Writes.Count);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapInitializeAsync_CleanupThrows_PreservesPrimaryFailureAndReleasesOtherResources(bool cancel)
    {
        using var directory = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        EnqueueBootResource(transport, MtkBootResourceKind.Authentication, 8, 0x1D0D, 0, 0);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = new FailingBootResourceDispose();
        var second = new MemoryStream(new byte[8]);
        var options = BootstrapOptions(directory) with
        {
            BootResourceOrder = [MtkBootResourceKind.Authentication, MtkBootResourceKind.RootCertificate],
            BootResourceProvider = new BootResourceProvider((_, kind, _) =>
            {
                if (kind == MtkBootResourceKind.RootCertificate && cancel)
                {
                    cancellation.Cancel();
                }
                return Task.FromResult(new MtkBootResource(
                    kind == MtkBootResourceKind.Authentication ? first : second, 8));
            })
        };
        var pending = MtkXFlashBootstrap.InitializeAsync(transport, options, cancellation.Token);
        Exception error;
        if (cancel)
        {
            error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            var protocolError = await Assert.ThrowsAsync<MtkProtocolException>(() => pending);
            Assert.Equal(0x1D0Du, protocolError.Status);
            error = protocolError;
        }

        Assert.Single(Assert.IsType<Exception[]>(error.Data["MtkBootResourceCleanupErrors"]));
        Assert.Equal(1, first.DisposeCalls);
        Assert.False(second.CanRead);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapInitializeAsync_ExplicitResourceOrder_AcquiresBeforeUploadsAndDisposes(bool authenticationFirst)
    {
        using var directory = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        var order = authenticationFirst
            ? new[] { MtkBootResourceKind.Authentication, MtkBootResourceKind.RootCertificate }
            : new[] { MtkBootResourceKind.RootCertificate, MtkBootResourceKind.Authentication };
        EnqueueBootstrap(transport, false, order);
        var streams = new List<MemoryStream>();
        var calls = new List<MtkBootResourceKind>();
        var options = BootstrapOptions(directory) with
        {
            BootResourceOrder = order,
            BootResourceProvider = new BootResourceProvider((device, kind, token) =>
            {
                Assert.Equal(0x766, device.HardwareCode);
                Assert.Equal(7, transport.Writes.Count);
                token.ThrowIfCancellationRequested();
                calls.Add(kind);
                var stream = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
                streams.Add(stream);
                return Task.FromResult(new MtkBootResource(stream, 8));
            })
        };

        using var session = await MtkXFlashBootstrap.InitializeAsync(transport, options, TestContext.Current.CancellationToken);

        Assert.Equal(order, calls);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        Assert.All(streams, stream => Assert.False(stream.CanRead));
        Assert.Equal(new byte[] { authenticationFirst ? (byte)0xE2 : (byte)0xE0 }, transport.Writes[7]);
        Assert.Equal(new byte[] { authenticationFirst ? (byte)0xE0 : (byte)0xE2 }, transport.Writes[10]);
        Assert.Equal(new byte[] { 0xD7 }, transport.Writes[13]);
    }

    [Fact]
    public async Task BootstrapInitializeAsync_SelectionMutatedDuringProviderWait_UsesCapturedOrder()
    {
        using var directory = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        List<MtkBootResourceKind> order = [MtkBootResourceKind.RootCertificate, MtkBootResourceKind.Authentication];
        EnqueueBootstrap(transport, false, [.. order]);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<MtkBootResourceKind>();
        var options = BootstrapOptions(directory) with
        {
            BootResourceOrder = order,
            BootResourceProvider = new BootResourceProvider(async (_, kind, token) =>
            {
                calls.Add(kind);
                await release.Task.WaitAsync(token);
                return new MtkBootResource(new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]), 8);
            })
        };
        var pending = MtkXFlashBootstrap.InitializeAsync(transport, options, TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        order.Clear();
        release.SetResult();

        using var session = await pending;

        Assert.Equal(new[] { MtkBootResourceKind.RootCertificate, MtkBootResourceKind.Authentication }, calls);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData("missing-provider")]
    [InlineData("missing-selection")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("too-many")]
    public async Task BootstrapInitializeAsync_InvalidResourcePolicy_RejectsBeforeDeviceIo(string failure)
    {
        using var directory = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        var calls = 0;
        var provider = new BootResourceProvider((_, _, _) =>
        {
            calls++;
            throw new InvalidOperationException();
        });
        var options = BootstrapOptions(directory) with
        {
            BootResourceProvider = failure == "missing-provider" ? null : provider,
            BootResourceOrder = failure switch
            {
                "missing-selection" => [],
                "duplicate" => [MtkBootResourceKind.Authentication, MtkBootResourceKind.Authentication],
                "unknown" => [(MtkBootResourceKind)9],
                "too-many" => [MtkBootResourceKind.Authentication, MtkBootResourceKind.RootCertificate, MtkBootResourceKind.Authentication],
                _ => [MtkBootResourceKind.Authentication]
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => MtkXFlashBootstrap.InitializeAsync(
            transport, options, TestContext.Current.CancellationToken));

        Assert.Equal(0, calls);
        Assert.Empty(transport.Writes);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapInitializeAsync_SecondResourceInvalid_ReleasesAllWithoutResourceUpload(bool missing)
    {
        using var directory = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        var streams = new List<MemoryStream>();
        var options = BootstrapOptions(directory) with
        {
            BootResourceOrder = [MtkBootResourceKind.RootCertificate, MtkBootResourceKind.Authentication],
            BootResourceProvider = new BootResourceProvider((_, kind, _) =>
            {
                if (missing && kind == MtkBootResourceKind.Authentication)
                {
                    throw new FileNotFoundException("No authorized resource.");
                }
                var stream = new MemoryStream(new byte[8]);
                streams.Add(stream);
                return Task.FromResult(new MtkBootResource(stream, kind == MtkBootResourceKind.Authentication ? 7 : 8));
            })
        };

        await Assert.ThrowsAnyAsync<Exception>(() => MtkXFlashBootstrap.InitializeAsync(
            transport, options, TestContext.Current.CancellationToken));

        Assert.Equal(7, transport.Writes.Count);
        Assert.All(streams, stream => Assert.False(stream.CanRead));
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task BootstrapInitializeAsync_ResourceRejected_DisposesAllAndDoesNotUploadAgent()
    {
        using var directory = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        EnqueueBootResource(transport, MtkBootResourceKind.Authentication, 8, 0x1D0D, 0, 0);
        var streams = new List<MemoryStream>();
        var options = BootstrapOptions(directory) with
        {
            BootResourceOrder = [MtkBootResourceKind.Authentication, MtkBootResourceKind.RootCertificate],
            BootResourceProvider = new BootResourceProvider((_, _, _) =>
            {
                var stream = new MemoryStream(new byte[8]);
                streams.Add(stream);
                return Task.FromResult(new MtkBootResource(stream, 8));
            })
        };

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => MtkXFlashBootstrap.InitializeAsync(
            transport, options, TestContext.Current.CancellationToken));

        Assert.Equal(0x1D0Du, error.Status);
        Assert.Equal(9, transport.Writes.Count);
        Assert.Equal(2, streams.Count);
        Assert.All(streams, stream => Assert.False(stream.CanRead));
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task BootstrapInitializeAsync_CancelledAfterProviderReturns_DisposesResourceBeforeAnyUpload()
    {
        using var directory = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stream = new MemoryStream(new byte[8]);
        var options = BootstrapOptions(directory) with
        {
            BootResourceOrder = [MtkBootResourceKind.Authentication],
            BootResourceProvider = new BootResourceProvider((_, _, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(new MtkBootResource(stream, 8));
            })
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MtkXFlashBootstrap.InitializeAsync(
            transport, options, cancellation.Token));

        Assert.False(stream.CanRead);
        Assert.Equal(7, transport.Writes.Count);
        Assert.Equal(1, transport.DisposeCount);
    }

    private sealed class BootResourceProvider(
        Func<MtkDeviceInfo, MtkBootResourceKind, CancellationToken, Task<MtkBootResource>> open) : IMtkBootResourceProvider
    {
        public Task<MtkBootResource> OpenAsync(MtkDeviceInfo device, MtkBootResourceKind kind,
            CancellationToken cancellationToken) => open(device, kind, cancellationToken);
    }

    private sealed class FailingBootResourceDispose() : MemoryStream(new byte[8])
    {
        internal int DisposeCalls { get; private set; }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                DisposeCalls++;
                throw new IOException("Provider stream cleanup failed.");
            }
        }
    }
}