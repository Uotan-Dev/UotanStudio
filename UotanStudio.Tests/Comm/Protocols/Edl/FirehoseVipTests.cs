using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class FirehoseVipTests
{
    private const string Ack = "<data><response value=\"ACK\"/></data>";
    private const string ConfigureAck = "<data><response value=\"ACK\" MaxPayloadSizeToTargetInBytes=\"512\" Erase=\"true\" Patch=\"true\" Wipe=\"true\" UfsProvisioning=\"true\"/></data>";
    private const string RawAck = "<data><response value=\"ACK\" rawmode=\"true\"/></data>";

    [Fact]
    public async Task ConfigureVipAsync_FirstCommand_SendsAndAcknowledgesSignedTableBeforeXml()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Ack);
        transport.Enqueue(Ack);
        using var protocol = NewProtocol(transport);
        var provider = new Tables();
        await protocol.ConfigureVipAsync(Options(provider), TestContext.Current.CancellationToken);
        Assert.Empty(transport.Writes);
        Assert.Empty(provider.Requests);

        await protocol.NopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 0 }, provider.Requests);
        Assert.Equal(Tables.SignedBytes, transport.Writes[0]);
        Assert.Contains("<nop", Encoding.UTF8.GetString(transport.Writes[1]));
        Assert.False(provider.Streams[0].CanRead);
    }

    [Theory]
    [InlineData("<data><response value=\"NACK\"/></data>")]
    [InlineData("<data><response value=\"ACK\" rawmode=\"true\"/></data>")]
    public async Task ConfigureVipAsync_TableRejected_DoesNotSendFollowingCommand(string response)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(response);
        using var protocol = NewProtocol(transport);
        var provider = new Tables();
        await protocol.ConfigureVipAsync(Options(provider), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<FirehoseException>(() => protocol.NopAsync(TestContext.Current.CancellationToken));

        Assert.Single(transport.Writes);
        Assert.True(protocol.RequiresReconnect);
        Assert.False(provider.Streams[0].CanRead);
    }

    [Fact]
    public async Task ProgramAsync_VipBoundaryWithinRawData_RotatesTableWithoutLosingFinalAck()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Ack);
        transport.Enqueue(ConfigureAck);
        transport.Enqueue(RawAck);
        transport.Enqueue(Ack);
        transport.Enqueue(Ack);
        using var protocol = NewProtocol(transport);
        var provider = new Tables();
        await protocol.ConfigureVipAsync(Options(provider), TestContext.Current.CancellationToken);
        await protocol.ConfigureAsync(cancellationToken: TestContext.Current.CancellationToken);
        var bytes = Enumerable.Range(0, 53 * 512).Select(index => (byte)index).ToArray();
        using var source = new MemoryStream(bytes);

        var result = await protocol.ProgramAsync(source, 0, 53, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsAck);
        Assert.Equal(new[] { 0, 1 }, provider.Requests);
        Assert.Equal(57, transport.Writes.Count);
        Assert.Equal(Tables.ChainedBytes, transport.Writes[54]);
        var sent = transport.Writes.Skip(3).Where((_, index) => index != 51).SelectMany(value => value).ToArray();
        Assert.Equal(bytes, sent);
        Assert.False(protocol.RequiresReconnect);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProgramAsync_VipBoundaryFailure_StopsBeforeNextRawBlock(bool missing)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Ack);
        transport.Enqueue(ConfigureAck);
        transport.Enqueue(RawAck);
        if (!missing)
        {
            transport.Enqueue("<data><response value=\"NAK\"/></data>");
        }
        using var protocol = NewProtocol(transport);
        var provider = new Tables { AvailableTables = missing ? 1 : 2 };
        await protocol.ConfigureVipAsync(Options(provider), TestContext.Current.CancellationToken);
        await protocol.ConfigureAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var source = new MemoryStream(new byte[53 * 512]);

        await Assert.ThrowsAsync<FirehoseException>(() => protocol.ProgramAsync(source, 0, 53,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(protocol.RequiresReconnect);
        Assert.Equal(new[] { 0, 1 }, provider.Requests);
        Assert.Equal(missing ? 54 : 55, transport.Writes.Count);
    }

    [Fact]
    public async Task NopAsync_ChainedCapacity_CountsEachXmlPacket()
    {
        using var transport = new ScriptedCommTransport { OnWrite = _ => [Encoding.UTF8.GetBytes(Ack)] };
        using var protocol = NewProtocol(transport);
        var provider = new Tables();
        await protocol.ConfigureVipAsync(Options(provider), TestContext.Current.CancellationToken);

        for (var index = 0; index < 309; index++)
        {
            await protocol.NopAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(new[] { 0, 1, 2 }, provider.Requests);
        Assert.Equal(312, transport.Writes.Count);
        Assert.Equal(Tables.ChainedBytes, transport.Writes[54]);
        Assert.Equal(Tables.ChainedBytes, transport.Writes[310]);
    }

    [Theory]
    [InlineData("ACK")]
    [InlineData("NAK")]
    public async Task ConfigureAsync_VipPayloadRenegotiation_RejectsWithoutRetry(string status)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Ack);
        transport.Enqueue($"<data><response value=\"{status}\" MaxPayloadSizeToTargetInBytes=\"1024\"/></data>");
        using var protocol = NewProtocol(transport);
        var provider = new Tables();
        await protocol.ConfigureVipAsync(Options(provider), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<FirehoseException>(() => protocol.ConfigureAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(2, transport.Writes.Count);
        Assert.True(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ConfigureAsync_Vip_DoesNotInsertAutomaticStorageQueries()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Ack);
        transport.Enqueue(Ack);
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = true });
        await protocol.ConfigureVipAsync(Options(new Tables()), TestContext.Current.CancellationToken);

        await protocol.ConfigureAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public async Task ConfigureVipAsync_AfterCommand_RejectsBeforeTableProvider()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Ack);
        using var protocol = NewProtocol(transport);
        await protocol.NopAsync(TestContext.Current.CancellationToken);
        var provider = new Tables();

        await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.ConfigureVipAsync(
            Options(provider), TestContext.Current.CancellationToken));

        Assert.Empty(provider.Requests);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task ConfigureAsync_RequiredVipMarkerWithoutTables_Faults()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("<data><log value=\"VIP is enabled, receiving the signed table\"/><response value=\"ACK\"/></data>");
        using var protocol = NewProtocol(transport);

        await Assert.ThrowsAsync<FirehoseException>(() => protocol.ConfigureAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task NopAsync_CancelledTableProvider_DisposesReturnedStreamWithoutWriting()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = NewProtocol(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var provider = new Tables { OnOpen = cancellation.Cancel };
        await protocol.ConfigureVipAsync(Options(provider), cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => protocol.NopAsync(cancellation.Token));

        Assert.Empty(transport.Writes);
        Assert.False(provider.Streams[0].CanRead);
        Assert.True(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task SetOnePlusNetTypeAsync_Vip_CountsCommandInSignedPacketSequence()
    {
        using var transport = new ScriptedCommTransport { OnWrite = _ => [Encoding.UTF8.GetBytes(Ack)] };
        using var protocol = NewProtocol(transport);
        var provider = new Tables();
        await protocol.ConfigureVipAsync(Options(provider), TestContext.Current.CancellationToken);
        for (var index = 0; index < 52; index++)
        {
            await protocol.NopAsync(TestContext.Current.CancellationToken);
        }

        await protocol.SetOnePlusNetTypeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await protocol.NopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 0, 1 }, provider.Requests);
        Assert.Equal(56, transport.Writes.Count);
        Assert.Contains("<SetNetType", Encoding.UTF8.GetString(transport.Writes[53]));
        Assert.Equal(Tables.ChainedBytes, transport.Writes[54]);
        Assert.Contains("<nop", Encoding.UTF8.GetString(transport.Writes[55]));
        Assert.False(protocol.RequiresReconnect);
    }

    private static FirehoseProtocol NewProtocol(ScriptedCommTransport transport) =>
        new(transport, new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false });

    private static FirehoseVipOptions Options(Tables tables) => new() { TableProvider = tables, ProgramPayloadSize = 512 };

    private sealed class Tables : IFirehoseVipTableProvider
    {
        internal static readonly byte[] SignedBytes = Enumerable.Repeat((byte)0xA0, 128).ToArray();
        internal static readonly byte[] ChainedBytes = Enumerable.Repeat((byte)0xB0, 8192).ToArray();
        internal List<int> Requests { get; } = [];
        internal List<MemoryStream> Streams { get; } = [];
        internal int AvailableTables { get; init; } = 3;
        internal Action? OnOpen { get; init; }

        public Task<Stream?> OpenTableAsync(int tableIndex, CancellationToken cancellationToken)
        {
            Requests.Add(tableIndex);
            if (tableIndex >= AvailableTables)
            {
                return Task.FromResult<Stream?>(null);
            }
            var source = new MemoryStream(tableIndex == 0 ? SignedBytes : ChainedBytes, writable: false);
            Streams.Add(source);
            OnOpen?.Invoke();
            return Task.FromResult<Stream?>(source);
        }
    }
}