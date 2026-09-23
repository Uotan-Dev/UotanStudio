using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(MtkBootResourceKind.RootCertificate, 0xFF)]
    [InlineData(MtkBootResourceKind.Authentication, 0xFE)]
    public async Task UploadBootResourceByteStreamAsync_AcceptedStatusBoundary_PreservesBootStage(MtkBootResourceKind kind, int initial)
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        EnqueueBootResource(transport, kind, 8, (ushort)initial, 0, 0xFF);
        using var resource = new MemoryStream(new byte[8]);

        await session.UploadBootResourceByteStreamAsync(kind, resource, 8, TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadBootResourceByteStreamAsync_TruncatedSourceOrFinalResponse_Faults(bool shortSource)
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue([0xE2, 0, 0, 0, 8, 0, 0, 0]);
        using var resource = new NonSeekableBootResource(new byte[shortSource ? 6 : 8]);
        var before = transport.Writes.Count;

        if (shortSource)
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => session.UploadBootResourceByteStreamAsync(
                MtkBootResourceKind.Authentication, resource, 8, TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<MtkProtocolException>(() => session.UploadBootResourceByteStreamAsync(
                MtkBootResourceKind.Authentication, resource, 8, TestContext.Current.CancellationToken));
        }

        Assert.Equal(shortSource ? 2 : 3, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(MtkBootResourceKind.RootCertificate)]
    [InlineData(MtkBootResourceKind.Authentication)]
    public async Task UploadBootResourceByteStreamAsync_KnownWireBytes_StreamsAndPreservesBootStage(MtkBootResourceKind kind)
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        EnqueueBootResource(transport, kind, 8, 0, 0x0800, 0);
        using var resource = new FragmentedSource([1, 2, 3, 4, 5, 6, 7, 8]);
        var before = transport.Writes.Count;

        await session.UploadBootResourceByteStreamAsync(kind, resource, 8, TestContext.Current.CancellationToken);

        Assert.Equal(3, transport.Writes.Count - before);
        Assert.Equal(new byte[] { kind == MtkBootResourceKind.RootCertificate ? (byte)0xE0 : (byte)0xE2 },
            transport.Writes[before]);
        Assert.Equal(new byte[] { 0, 0, 0, 8 }, transport.Writes[before + 1]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, transport.Writes[before + 2]);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
        Assert.True(resource.CanRead);
    }

    [Theory]
    [InlineData(MtkBootResourceKind.RootCertificate, 0x100)]
    [InlineData(MtkBootResourceKind.Authentication, 0xFF)]
    [InlineData(MtkBootResourceKind.Authentication, 0x1D0C)]
    public async Task UploadBootResourceByteStreamAsync_RequestRejected_DoesNotReadOrSendResource(MtkBootResourceKind kind, int status)
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        EnqueueBootResource(transport, kind, 8, (ushort)status, 0, 0);
        using var resource = new MemoryStream(new byte[8]);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.UploadBootResourceByteStreamAsync(
            kind, resource, 8, TestContext.Current.CancellationToken));

        Assert.Equal((uint)status, error.Status);
        Assert.Equal(0, resource.Position);
        Assert.Equal(2, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0x1234, 0)]
    [InlineData(0x0800, 0x100)]
    public async Task UploadBootResourceByteStreamAsync_InvalidCompletion_FaultsWithoutAllowingAgentUpload(int checksum, int status)
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        EnqueueBootResource(transport, MtkBootResourceKind.Authentication, 8, 0, (ushort)checksum, (ushort)status);
        using var resource = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.UploadBootResourceByteStreamAsync(
            MtkBootResourceKind.Authentication, resource, 8, TestContext.Current.CancellationToken));
        var before = transport.Writes.Count;
        using var agent = new MemoryStream(new byte[8]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UploadDownloadAgentAsync(
            agent, 0x200000, 8, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task UploadBootResourceByteStreamAsync_LargeFragmentedSelection_BoundsWritesAndDoesNotConsumeSuffix()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        EnqueueBootResource(transport, MtkBootResourceKind.RootCertificate, 65536, 0, 0, 0);
        using var resource = new MemoryStream(new byte[65538]);
        var before = transport.Writes.Count;

        await session.UploadBootResourceByteStreamAsync(MtkBootResourceKind.RootCertificate, resource, 65536,
            TestContext.Current.CancellationToken);

        Assert.Equal(65536, resource.Position);
        Assert.Equal(65536, transport.Writes.Skip(before + 2).Sum(part => part.Length));
        Assert.All(transport.Writes.Skip(before + 2), part => Assert.InRange(part.Length, 1, 1024));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(67108866)]
    public async Task UploadBootResourceByteStreamAsync_InvalidLength_RejectsBeforeIo(int length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        using var resource = new MemoryStream(new byte[8]);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => session.UploadBootResourceByteStreamAsync(
            MtkBootResourceKind.Authentication, resource, length, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(MtkSessionState.Created, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadBootResourceByteStreamAsync_Cancellation_DistinguishesPreIoAndInFlight(bool inFlight)
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (inFlight)
        {
            transport.OnWrite = _ =>
            {
                cancellation.Cancel();
                return [];
            };
        }
        else
        {
            cancellation.Cancel();
        }
        using var resource = new MemoryStream(new byte[8]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.UploadBootResourceByteStreamAsync(
            MtkBootResourceKind.RootCertificate, resource, 8, cancellation.Token));

        Assert.Equal(before + (inFlight ? 1 : 0), transport.Writes.Count);
        Assert.Equal(inFlight ? MtkSessionState.Faulted : MtkSessionState.Bootloader, session.State);
    }

    private static void EnqueueBootResource(ScriptedCommTransport transport, MtkBootResourceKind kind, int length,
        ushort initial, ushort checksum, ushort final)
    {
        var response = new byte[11];
        response[0] = kind == MtkBootResourceKind.RootCertificate ? (byte)0xE0 : (byte)0xE2;
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(1), (uint)length);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(5), initial);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(7), checksum);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(9), final);
        transport.Enqueue(response);
    }

    private sealed class NonSeekableBootResource(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}