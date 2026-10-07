using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAsync_KnownWireIdentity_DecodesBigEndianAndSecurityFlags(bool fragmented)
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport, fragmented);
        using var session = new MtkSession(transport);

        var info = await session.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0x0766, info.HardwareCode);
        Assert.Equal(0x8A00, info.HardwareSubCode);
        Assert.Equal(0xCA01, info.HardwareVersion);
        Assert.Equal(2, info.SoftwareVersion);
        Assert.Equal(0x17u, info.TargetConfiguration);
        Assert.True(info.SecureBootEnabled);
        Assert.True(info.SerialLinkAuthenticationRequired);
        Assert.True(info.DownloadAgentAuthenticationRequired);
        Assert.True(info.RootCertificateRequired);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
        Assert.Equal((byte)0, info.BlVersion);
        Assert.Equal((byte)0, info.BromVersion);
        Assert.Null(info.Meid);
        Assert.Null(info.Socid);
        Assert.Equal(new byte[] { 0xA0, 0x0A, 0x50, 0x05, 0xFD, 0xFC, 0xD8, 0xFE, 0xFF, 0xFE, 0xFE },
            transport.Writes.SelectMany(bytes => bytes));
    }

    [Fact]
    public async Task InitializeAsync_WrongHandshake_FaultsWithoutFurtherCommands()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue([0]);
        using var session = new MtkSession(transport);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.InitializeAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.False(session.IsConnected);
        Assert.Null(session.DeviceInfo);
        Assert.Equal(new byte[] { 0xA0 }, Assert.Single(transport.Writes));
    }

    [Fact]
    public async Task InitializeAsync_TruncatedField_DoesNotAssumeUnauthenticatedDevice()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue([0x5F, 0xF5, 0xAF, 0xFA, 0xFD, 0x07]);
        using var session = new MtkSession(transport);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Null(session.DeviceInfo);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task UploadDownloadAgentAsync_KnownChecksumAndSignature_StreamsThenAllowsJump()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue([0xD7, 0, 0x20, 0, 0, 0, 0, 0, 8, 0, 0, 0, 2, 0, 0]);
        transport.Enqueue([8, 0, 0, 0]);
        using var image = new FragmentedSource([1, 2, 3, 4, 5, 6, 7, 8]);
        var progress = new ProgressRecorder();

        await session.UploadDownloadAgentAsync(image, 0x200000, 8, 2, progress, TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.AgentUploaded, session.State);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, transport.Writes[^1]);
        Assert.Equal(new byte[] { 0, 0, 0, 2 }, transport.Writes[^2]);
        Assert.Equal(8, Assert.Single(progress.Values));
        transport.Enqueue([0xD5, 0, 0x20, 0, 0, 0, 0]);
        await session.StartDownloadAgentAsync(TestContext.Current.CancellationToken);
        Assert.Equal(MtkSessionState.AgentStarted, session.State);
        Assert.Equal(new byte[] { 0, 0x20, 0, 0 }, transport.Writes[^1]);
    }

    [Fact]
    public async Task UploadDownloadAgentAsync_TargetRequiresAuthentication_PreservesStatusAndSendsNoImage()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue([0xD7, 0, 0x20, 0, 0, 0, 0, 0, 8, 0, 0, 0, 0, 0x1D, 0x0D]);
        using var image = new MemoryStream(new byte[8]);

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.UploadDownloadAgentAsync(image, 0x200000, 8, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal((ushort)0x1D0D, error.Status);
        Assert.Equal(0, image.Position);
        Assert.Equal(15, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task UploadDownloadAgentAsync_ChecksumMismatch_PreventsAgentExecution()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue([0xD7, 0, 0x20, 0, 0, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0]);
        transport.Enqueue([0x12, 0x34, 0, 0]);
        using var image = new MemoryStream(new byte[8]);

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.UploadDownloadAgentAsync(image, 0x200000, 8, cancellationToken: TestContext.Current.CancellationToken));
        var count = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartDownloadAgentAsync(TestContext.Current.CancellationToken));

        Assert.Equal(count, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task UploadDownloadAgentAsync_LargeFragmentedSource_BoundsEveryWrite()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue([0xD7, 0, 0x20, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0]);
        transport.Enqueue([0, 0, 0, 0]);
        using var image = new FragmentedSource(new byte[0x20000]);

        await session.UploadDownloadAgentAsync(image, 0x200000, 0x20000, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0x20000, transport.Writes.Skip(15).Sum(bytes => bytes.Length));
        Assert.All(transport.Writes.Skip(15), bytes => Assert.InRange(bytes.Length, 1, 1024));
        Assert.Equal(MtkSessionState.AgentUploaded, session.State);
    }

    [Fact]
    public async Task UploadDownloadAgentAsync_PooledStagingBuffer_SendsExactlyDeclaredChunkBytes()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue([0xD7, 0, 0x20, 0, 0, 0, 0, 0x06, 0, 0, 0, 0, 0, 0, 0]);
        transport.Enqueue([0, 0, 0, 0]);
        using var image = new FragmentedSource([.. Enumerable.Repeat((byte)0x01, 1536)]);

        await session.UploadDownloadAgentAsync(image, 0x200000, 1536, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.AgentUploaded, session.State);
        Assert.Equal(1024, transport.Writes[^2].Length);
        Assert.All(transport.Writes[^2], value => Assert.Equal(0x01, value));
        Assert.Equal(512, transport.Writes[^1].Length);
        Assert.All(transport.Writes[^1], value => Assert.Equal(0x01, value));
    }

    [Fact]
    public async Task InitializeAsync_PreCancelled_PreservesCreatedState()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.InitializeAsync(cancellation.Token));

        Assert.Empty(transport.Writes);
        Assert.Equal(MtkSessionState.Created, session.State);
    }

    [Fact]
    public async Task UploadDownloadAgentAsync_CancelledAfterRequest_DoesNotRetry()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        transport.OnWrite = _ =>
        {
            cancellation.Cancel();
            return [];
        };
        using var image = new MemoryStream(new byte[8]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.UploadDownloadAgentAsync(image, 0x200000, 8, cancellationToken: cancellation.Token));

        Assert.Equal(12, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(7, 0)]
    [InlineData(8, 9)]
    [InlineData(8, -1)]
    [InlineData(67108865, 0)]
    public async Task UploadDownloadAgentAsync_InvalidImageGeometry_RejectsBeforeIo(int length, int signatureLength)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        using var image = new MemoryStream(new byte[8]);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => session.UploadDownloadAgentAsync(
            image, 0x200000, length, signatureLength, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(MtkSessionState.Created, session.State);
    }

    [Fact]
    public async Task StartDownloadAgentAsync_NoVerifiedUpload_RejectsWithoutIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartDownloadAgentAsync(TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task InitializeXFlashAsync_ValidExchange_EntersStageOneWithExactParameters()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStartedSessionAsync(transport);
        EnqueueXFlashSetup(transport);
        var before = transport.Writes.Count;

        await session.InitializeXFlashAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.XFlashStageOneReady, session.State);
        Assert.Equal(10, transport.Writes.Count - before);
        Assert.Equal(Convert.FromHexString("53594E43"), transport.Writes[before + 1]);
        Assert.Equal(Convert.FromHexString("00010100"), transport.Writes[before + 3]);
        var environment = transport.Writes[before + 5];
        Assert.Equal(20, environment.Length);
        Assert.Equal(2, environment[0]);
        Assert.Equal(1, environment[4]);
        Assert.Equal(OperatingSystem.IsWindows() ? 0 : 1, environment[8]);
        Assert.All(environment.Skip(9), value => Assert.Equal(0, value));
        Assert.Equal(Convert.FromHexString("01010100"), transport.Writes[before + 7]);
        Assert.Equal(new byte[4], transport.Writes[before + 9]);
    }

    [Theory]
    [InlineData("brom", MtkXFlashConnectionAgent.BootRom)]
    [InlineData("preloader", MtkXFlashConnectionAgent.Preloader)]
    public async Task GetXFlashConnectionAgentAsync_RecognizedAgent_ConsumesCompletion(string name, MtkXFlashConnectionAgent expected)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStartedSessionAsync(transport);
        EnqueueXFlashSetup(transport);
        await session.InitializeXFlashAsync(cancellationToken: TestContext.Current.CancellationToken);
        for (var i = 0; i < 2; i++)
        {
            EnqueueXFlashFrame(transport, new byte[4]);
            EnqueueXFlashFrame(transport, new byte[4]);
            EnqueueXFlashFrame(transport, System.Text.Encoding.ASCII.GetBytes(name));
            EnqueueXFlashFrame(transport, new byte[4]);
            Assert.Equal(expected, await session.GetXFlashConnectionAgentAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(MtkSessionState.XFlashStageOneReady, session.State);
    }

    [Fact]
    public async Task InitializeXFlashAsync_TargetRejection_PreservesFullStatusAndStops()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStartedSessionAsync(transport);
        transport.Enqueue([0xC0]);
        EnqueueXFlashFrame(transport, [0x53, 0, 2, 0xC0]);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.InitializeXFlashAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0xC0020053u, error.Status);
        Assert.Equal(6, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData("bad-marker")]
    [InlineData("bad-type")]
    [InlineData("bad-length")]
    [InlineData("bad-sync")]
    public async Task InitializeXFlashAsync_InvalidReply_FaultsWithoutRetry(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStartedSessionAsync(transport);
        transport.Enqueue([failure == "bad-marker" ? (byte)0 : (byte)0xC0]);
        EnqueueXFlashFrame(transport, failure == "bad-length" ? new byte[3] : new byte[4],
            failure == "bad-type" ? 2u : 1u);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.InitializeXFlashAsync(cancellationToken: TestContext.Current.CancellationToken));
        var writes = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InitializeXFlashAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(writes, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    private static async Task<MtkSession> CreateStartedSessionAsync(ScriptedCommTransport transport)
    {
        EnqueueIdentity(transport);
        var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue(Convert.FromHexString("D7002000000000000800000000000000000000"));
        using var image = new MemoryStream(new byte[8]);
        await session.UploadDownloadAgentAsync(image, 0x200000, 8, cancellationToken: TestContext.Current.CancellationToken);
        transport.Enqueue(Convert.FromHexString("D5002000000000"));
        await session.StartDownloadAgentAsync(TestContext.Current.CancellationToken);
        return session;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeXFlashAsync_Cancellation_DistinguishesBeforeAndDuringExchange(bool duringExchange)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStartedSessionAsync(transport);
        using var cancellation = new CancellationTokenSource();
        EnqueueXFlashSetup(transport);
        var before = transport.Writes.Count;
        if (duringExchange)
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.InitializeXFlashAsync(cancellationToken: cancellation.Token));

        Assert.Equal(before + (duringExchange ? 1 : 0), transport.Writes.Count);
        Assert.Equal(duringExchange ? MtkSessionState.Faulted : MtkSessionState.AgentStarted, session.State);
    }

    private static void EnqueueXFlashSetup(ScriptedCommTransport transport)
    {
        transport.Enqueue([0xC0]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[2]);
        EnqueueXFlashFrame(transport, Convert.FromHexString("53594E43"));
    }

    private static void EnqueueXFlashFrame(ScriptedCommTransport transport, byte[] payload, uint type = 1)
    {
        var header = Convert.FromHexString("EFEEEEFE0100000000000000");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), type);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)payload.Length);
        transport.Enqueue([.. header, .. payload]);
    }

    private static void EnqueueIdentity(ScriptedCommTransport transport, bool fragmented = false)
    {
        byte[] wire =
        [
            0x5F, 0xF5, 0xAF, 0xFA,
            0xFD, 0x07, 0x66, 0, 0,
            0xFC, 0x8A, 0, 0xCA, 1, 0, 2, 0, 0,
            0xD8, 0, 0, 0, 0x17, 0, 0
        ];
        if (fragmented)
        {
            foreach (var value in wire)
            {
                transport.Enqueue([value]);
            }
        }
        else
        {
            transport.Enqueue(wire);
        }

        transport.Enqueue([0, 0, 0, 0]);
    }

    private sealed class FragmentedSource(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(7, buffer.Length)], cancellationToken);
    }

    private sealed class ProgressRecorder : IProgress<long>
    {
        internal List<long> Values { get; } = [];
        public void Report(long value) => Values.Add(value);
    }
}