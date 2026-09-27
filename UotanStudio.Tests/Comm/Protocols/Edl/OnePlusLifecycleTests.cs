using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed partial class ProjectAuthenticationTests
{
    [Fact]
    public async Task SetOnePlusNetTypeAsync_ExplicitCommand_PreservesCaseAndNextResponse()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("<data><log value=\"SetNetType accepted\"/></data>" + Ack + Ack);
        using var protocol = new FirehoseProtocol(transport);

        var response = await protocol.SetOnePlusNetTypeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await protocol.NopAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        var command = XDocument.Parse(Encoding.UTF8.GetString(transport.Writes[0])).Root!.Elements().Single();
        Assert.Equal("SetNetType", command.Name.LocalName);
        Assert.Empty(command.Attributes());
        Assert.Empty(command.Nodes());
        Assert.Equal(new[] { "SetNetType", "nop" }, OnePlusLifecycleCommands(transport));
        Assert.False(protocol.RequiresReconnect);
    }

    [Theory]
    [InlineData("<data><response value=\"NAK\"/></data>")]
    [InlineData("<data><response value=\"NACK\"/></data>")]
    [InlineData("<data><response value=\"ACK\" rawmode=\"true\"/></data>")]
    [InlineData("<data><response value=\"unexpected\"/></data>")]
    [InlineData("<data><response value=\"ACK\"/><response value=\"NAK\"/></data>")]
    [InlineData("<data><response value=\"ACK\"></data>")]
    public async Task SetOnePlusNetTypeAsync_InvalidResponse_FaultsWithoutRetry(string response)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(response);
        using var protocol = new FirehoseProtocol(transport);

        await Assert.ThrowsAnyAsync<EdlException>(() => protocol.SetOnePlusNetTypeAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(protocol.RequiresReconnect);
        Assert.Single(transport.Writes);
        await Assert.ThrowsAsync<EdlException>(() => protocol.NopAsync(TestContext.Current.CancellationToken));
        Assert.Single(transport.Writes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(86400001)]
    public async Task SetOnePlusNetTypeAsync_InvalidDeadline_RejectsBeforeIo(int timeout)
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new FirehoseProtocol(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => protocol.SetOnePlusNetTypeAsync(
            timeout, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.False(protocol.RequiresReconnect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetOnePlusNetTypeAsync_Cancellation_PreservesOnlyPreIoSession(bool inFlight)
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new FirehoseProtocol(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (inFlight)
        {
            transport.OnWrite = _ =>
            {
                cancellation.Cancel();
                return [Encoding.UTF8.GetBytes(Ack)];
            };
        }
        else
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => protocol.SetOnePlusNetTypeAsync(
            cancellationToken: cancellation.Token));

        Assert.Equal(inFlight, protocol.RequiresReconnect);
        Assert.Equal(inFlight ? 1 : 0, transport.Writes.Count);
    }

    [Fact]
    public async Task SetOnePlusNetTypeAsync_BlockedResponse_ExpiresDeadlineWithoutReplay()
    {
        using var transport = new OnePlusLifecycleWaitTransport();
        using var protocol = new FirehoseProtocol(transport);

        await Assert.ThrowsAsync<TimeoutException>(() => protocol.SetOnePlusNetTypeAsync(
            100, TestContext.Current.CancellationToken));

        Assert.True(protocol.RequiresReconnect);
        Assert.Single(transport.Inner.Writes);
    }

    [Fact]
    public async Task SetOnePlusNetTypeAsync_ConcurrentCommand_WaitsForNetTypeAck()
    {
        using var transport = new OnePlusLifecycleWaitTransport();
        transport.Inner.Enqueue(Ack + Ack);
        using var protocol = new FirehoseProtocol(transport);
        var netType = protocol.SetOnePlusNetTypeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Task<FirehoseResponse> nop;
        try
        {
            await transport.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            nop = protocol.NopAsync(TestContext.Current.CancellationToken);
            Assert.False(nop.IsCompleted);
            Assert.Single(transport.Inner.Writes);
        }
        finally
        {
            transport.Resume.TrySetResult();
            await netType;
        }
        await nop;
        Assert.Equal(new[] { "SetNetType", "nop" }, OnePlusLifecycleCommands(transport.Inner));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(EdlProjectAuthenticationKind.OnePlusDemacia)]
    [InlineData(EdlProjectAuthenticationKind.OnePlusSoftwareProject)]
    public async Task InitializeAsync_NetTypeSelection_UsesVendorOrderBeforeStorage(
        EdlProjectAuthenticationKind? kind)
    {
        using var transport = OnePlusLifecycleTransport();
        using var session = new EdlSession(transport);
        await session.ProbeProtocolAsync(TestContext.Current.CancellationToken);
        var provider = new Provider((actual, _, _, timestamp, _) =>
        {
            Assert.Equal(kind, actual);
            Assert.Equal(kind == EdlProjectAuthenticationKind.OnePlusSoftwareProject ? 123456L : (long?)null, timestamp);
            if (kind == EdlProjectAuthenticationKind.OnePlusSoftwareProject)
            {
                Assert.Contains("SetNetType", OnePlusLifecycleCommands(transport));
            }
            else
            {
                Assert.DoesNotContain("SetNetType", OnePlusLifecycleCommands(transport));
            }
            return Task.FromResult<IReadOnlyDictionary<string, byte[]>>(Credentials(actual));
        });
        var options = new EdlInitializationOptions
        {
            SendOnePlusNetType = true,
            ProjectAuthentication = kind is { } selected ? Options(selected, provider) : null
        };

        await session.InitializeAsync(null, options, TestContext.Current.CancellationToken);

        string[] expected = kind switch
        {
            EdlProjectAuthenticationKind.OnePlusDemacia => ["nop", "configure", "demacia", "SetNetType", "getstorageinfo"],
            EdlProjectAuthenticationKind.OnePlusSoftwareProject => ["nop", "configure", "SetNetType", "setprocstart", "setswprojmodel", "getstorageinfo"],
            _ => ["nop", "configure", "SetNetType", "getstorageinfo"]
        };
        Assert.Equal(expected, OnePlusLifecycleCommands(transport));
        Assert.Equal(kind is null ? 0 : 1, provider.Calls);
        Assert.True(session.IsConfigured);
        Assert.True(session.IsConnected);
        if (kind == EdlProjectAuthenticationKind.OnePlusSoftwareProject)
        {
            var before = transport.Writes.Count;
            using var source = new MemoryStream(new byte[512]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.Firehose!.ProgramAsync(source, 0, 1,
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(before, transport.Writes.Count);
            Assert.Equal(0, source.Position);
        }
    }

    [Theory]
    [InlineData("project")]
    [InlineData("nothing")]
    [InlineData("xiaomi")]
    public async Task InitializeAsync_NetTypeWithIncompatibleAuthentication_RejectsBeforeIo(string variant)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new EdlSession(transport);
        var provider = new Provider((_, _, _, _, _) => throw new InvalidOperationException("Must not request credentials."));
        var options = new EdlInitializationOptions
        {
            SendOnePlusNetType = true,
            ProjectAuthentication = variant == "xiaomi" ? null : Options(
                variant == "project" ? EdlProjectAuthenticationKind.OnePlusProject : EdlProjectAuthenticationKind.NothingProject, provider),
            XiaomiAuthenticationProvider = variant == "xiaomi" ? new UnusedLifecycleXiaomiProvider() : null
        };

        await Assert.ThrowsAsync<ArgumentException>(() => session.InitializeAsync(null, options,
            TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
        Assert.Equal(0, provider.Calls);
        Assert.False(session.Firehose!.RequiresReconnect);
    }

    [Fact]
    public async Task InitializeAsync_NetTypeRejected_DoesNotRequestProjectCredentialsOrStorage()
    {
        using var transport = OnePlusLifecycleTransport("<data><response value=\"NAK\"/></data>");
        using var session = new EdlSession(transport);
        await session.ProbeProtocolAsync(TestContext.Current.CancellationToken);
        var provider = new Provider((_, _, _, _, _) => throw new InvalidOperationException("Must not request credentials."));

        await Assert.ThrowsAsync<FirehoseException>(() => session.InitializeAsync(null, new EdlInitializationOptions
        {
            SendOnePlusNetType = true,
            ProjectAuthentication = Options(EdlProjectAuthenticationKind.OnePlusSoftwareProject, provider)
        }, TestContext.Current.CancellationToken));

        Assert.Equal(new[] { "nop", "configure", "SetNetType" }, OnePlusLifecycleCommands(transport));
        Assert.Equal(0, provider.Calls);
        Assert.False(session.IsConfigured);
        Assert.False(session.IsConnected);
        Assert.True(session.Firehose!.RequiresReconnect);
    }

    [Theory]
    [InlineData(EdlProjectAuthenticationKind.OnePlusDemacia)]
    [InlineData(EdlProjectAuthenticationKind.OnePlusSoftwareProject)]
    public async Task InitializeAsync_ProjectVerificationFails_StopsAtSelectedStage(EdlProjectAuthenticationKind kind)
    {
        using var transport = OnePlusLifecycleTransport();
        var normalResponse = transport.OnWrite!;
        transport.OnWrite = bytes =>
        {
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single().Name.LocalName;
            return command is "demacia" or "setswprojmodel"
                ? [Encoding.UTF8.GetBytes("<data><response value=\"ACK\" verify_res=\"1\" model_check=\"1\" auth_token_verify=\"1\"/></data>")]
                : normalResponse(bytes);
        };
        using var session = new EdlSession(transport);
        await session.ProbeProtocolAsync(TestContext.Current.CancellationToken);
        var credentials = Credentials(kind);
        var provider = new Provider((_, _, _, _, _) => Task.FromResult<IReadOnlyDictionary<string, byte[]>>(credentials));
        var options = new EdlInitializationOptions
        {
            SendOnePlusNetType = true,
            ProjectAuthentication = Options(kind, provider)
        };

        await Assert.ThrowsAsync<FirehoseException>(() => session.InitializeAsync(null, options,
            TestContext.Current.CancellationToken));

        string[] expected = kind == EdlProjectAuthenticationKind.OnePlusDemacia
            ? ["nop", "configure", "demacia"]
            : ["nop", "configure", "SetNetType", "setprocstart", "setswprojmodel"];
        Assert.Equal(expected, OnePlusLifecycleCommands(transport));
        Assert.All(credentials.Values, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
        Assert.False(session.IsConfigured);
        Assert.True(session.Firehose!.RequiresReconnect);
        var before = transport.Writes.Count;
        await Assert.ThrowsAsync<EdlException>(() => session.InitializeAsync(null, options,
            TestContext.Current.CancellationToken));
        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task SetOnePlusNetTypeAsync_CancelledWhileWaitingForLease_PreservesSession()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new FirehoseProtocol(transport);
        using var lease = await protocol.EnterOperationAsync(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = protocol.SetOnePlusNetTypeAsync(cancellationToken: cancellation.Token);
        Assert.False(pending.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Empty(transport.Writes);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task InitializeAsync_NetTypeNotSelected_DoesNotInferItFromDeviceLogs()
    {
        using var transport = new ScriptedCommTransport
        {
            OnWrite = _ => [Encoding.UTF8.GetBytes("<data><log value=\"Supported Functions: SetNetType\"/><response value=\"ACK\"/></data>")]
        };
        using var session = new EdlSession(transport);
        await session.ProbeProtocolAsync(TestContext.Current.CancellationToken);

        await session.InitializeAsync(null, new EdlInitializationOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "nop", "configure", "getstorageinfo" }, OnePlusLifecycleCommands(transport));
    }

    [Fact]
    public async Task InitializeAsync_NetTypeAddedAfterConfiguration_RejectsWithoutReinitializing()
    {
        using var transport = OnePlusLifecycleTransport();
        using var session = new EdlSession(transport);
        await session.ProbeProtocolAsync(TestContext.Current.CancellationToken);
        await session.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InitializeAsync(null,
            new EdlInitializationOptions { SendOnePlusNetType = true }, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.True(session.IsConfigured);
        Assert.True(session.IsConnected);
    }

    private static string[] OnePlusLifecycleCommands(ScriptedCommTransport transport) =>
        transport.Writes.Select(bytes => XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single().Name.LocalName).ToArray();

    private static ScriptedCommTransport OnePlusLifecycleTransport(string netTypeResponse = Ack) => new()
    {
        OnWrite = bytes =>
        {
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single().Name.LocalName;
            var response = command switch
            {
                "SetNetType" => netTypeResponse,
                "setprocstart" => "<data><response value=\"ACK\" device_timestamp=\"123456\"/></data>",
                "demacia" => "<data><response value=\"ACK\" verify_res=\"0\"/></data>",
                "setswprojmodel" => "<data><response value=\"ACK\" model_check=\"0\" auth_token_verify=\"0\"/></data>",
                _ => Ack
            };
            return [Encoding.UTF8.GetBytes(response)];
        }
    };

    private sealed class UnusedLifecycleXiaomiProvider : IEdlXiaomiAuthenticationProvider
    {
        public Task<byte[]> CreateSignatureAsync(SaharaDeviceIdentity? identity, ReadOnlyMemory<byte> challenge,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Must not request a signature.");
    }

    private sealed class OnePlusLifecycleWaitTransport : ICommTransport
    {
        internal ScriptedCommTransport Inner { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsConnected => Inner.IsConnected;
        public void Connect() => Inner.Connect();
        public void Disconnect() => Inner.Disconnect();
        public void Reset() => Inner.Reset();
        public Stream GetStream() => Inner.GetStream();
        public void Dispose() => Inner.Dispose();
        public long Write(byte[] data, int offset, int length, int timeoutMs) => Inner.Write(data, offset, length, timeoutMs);
        public int Read(byte[] data, int offset, int length, int timeoutMs) => Inner.Read(data, offset, length, timeoutMs);
        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default) =>
            Inner.WriteAsync(data, offset, length, timeoutMs, cancellationToken);

        public async Task<int> ReadAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return await Inner.ReadAsync(data, offset, length, timeoutMs, cancellationToken);
        }
    }
}