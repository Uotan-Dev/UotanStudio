using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public class FirehoseConcurrencyTests
{
    private const string Ready = "<data><response value='ACK' rawmode='true'/></data>";
    private const string Complete = "<data><response value='ACK' rawmode='false'/></data>";

    [Theory]
    [InlineData("nop")]
    [InlineData("configure")]
    [InlineData("detect")]
    [InlineData("storage")]
    [InlineData("bootable")]
    [InlineData("slot")]
    [InlineData("erase")]
    [InlineData("patch")]
    [InlineData("read")]
    [InlineData("stream")]
    [InlineData("peek")]
    [InlineData("poke")]
    [InlineData("pokedword")]
    [InlineData("memcpy")]
    [InlineData("ufs")]
    [InlineData("ufs-reset")]
    [InlineData("power")]
    [InlineData("program")]
    [InlineData("wipe")]
    [InlineData("verify")]
    public async Task ProgramAsync_ConcurrentOperation_WaitsThroughPayloadAndFinalAck(string command)
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);
        using var source = new PausedStream();
        var payloadWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OnWrite = bytes =>
        {
            if (transport.Writes.Count == 1)
            {
                return [Encoding.UTF8.GetBytes(Ready)];
            }

            if (transport.Writes.Count == 2)
            {
                Assert.Equal(new byte[512], bytes);
                payloadWritten.TrySetResult();
                return [];
            }

            var xml = Encoding.UTF8.GetString(bytes);
            if (xml.Contains("<program ", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes(Ready)];
            }

            return xml.Contains("<read ", StringComparison.Ordinal) || xml.Contains("<peek ", StringComparison.Ordinal)
                ? [Encoding.UTF8.GetBytes(Ready), new byte[512], Encoding.UTF8.GetBytes(Complete)]
                : [Encoding.UTF8.GetBytes(Complete)];
        };

        var program = protocol.ProgramAsync(source, 0, 1, cancellationToken: TestContext.Current.CancellationToken);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var pending = RunCommandAsync(protocol, command, TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        Assert.Single(transport.Writes);

        source.Continue.TrySetResult();
        await payloadWritten.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        Assert.Equal(2, transport.Writes.Count);
        transport.Enqueue(Complete);
        await Task.WhenAll(program, pending).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(command == "power", protocol.RequiresReconnect);
        Assert.True(transport.Writes.Count >= 3);
    }

    [Fact]
    public async Task ReadToStreamAsync_DestinationPaused_KeepsOwnershipUntilCompletion()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);
        using var destination = new PausedStream();
        transport.OnWrite = _ => transport.Writes.Count == 1
            ? [Encoding.UTF8.GetBytes(Ready), new byte[512], Encoding.UTF8.GetBytes(Complete)]
            : [Encoding.UTF8.GetBytes(Complete)];

        var read = protocol.ReadToStreamAsync(destination, 0, 1, cancellationToken: TestContext.Current.CancellationToken);
        await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var pending = protocol.NopAsync(TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        Assert.Single(transport.Writes);

        destination.Continue.TrySetResult();
        await Task.WhenAll(read, pending).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(new byte[512], destination.ToArray());
        Assert.Equal(2, transport.Writes.Count);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task NopAsync_CancelledWhileQueued_DoesNotFaultOwnerOrSendCommand()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(transport.Writes.Count == 1 ? Ready : Complete)];
        using var protocol = CreateProtocol(transport);
        using var source = new PausedStream();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var program = protocol.ProgramAsync(source, 0, 1, cancellationToken: TestContext.Current.CancellationToken);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var pending = protocol.NopAsync(cancellation.Token);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(protocol.RequiresReconnect);
        Assert.Single(transport.Writes);
        source.Continue.TrySetResult();
        await program.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await protocol.NopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, transport.Writes.Count);
    }

    [Fact]
    public async Task ProgramAsync_CancelledOwner_RejectsAlreadyQueuedCommand()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Ready);
        using var protocol = CreateProtocol(transport);
        using var source = new PausedStream();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var program = protocol.ProgramAsync(source, 0, 1, cancellationToken: cancellation.Token);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var pending = protocol.NopAsync(TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => program);
        await Assert.ThrowsAsync<EdlException>(() => pending);

        Assert.True(protocol.RequiresReconnect);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task ProgramAsync_DisposedOwner_RejectsQueuedCommandWithoutFurtherWrites()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Ready);
        using var protocol = CreateProtocol(transport);
        using var source = new PausedStream();
        var program = protocol.ProgramAsync(source, 0, 1, cancellationToken: TestContext.Current.CancellationToken);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var pending = protocol.NopAsync(TestContext.Current.CancellationToken);

        protocol.Dispose();
        source.Continue.TrySetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => program);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);

        Assert.Single(transport.Writes);
    }

    [Theory]
    [InlineData("configure", 3)]
    [InlineData("ufs", 4)]
    [InlineData("power", 2)]
    public async Task RunAsync_MultiCommandOperation_QueuesOtherCommandsUntilAllStepsFinish(string command, int expectedWrites)
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);
        protocol.Configuration.AutoDetectSectorSize = true;
        protocol.Configuration.SectorSize = 0;
        Task<FirehoseResponse>? pending = null;
        transport.OnWrite = _ =>
        {
            if (transport.Writes.Count == 1)
            {
                pending = protocol.NopAsync(TestContext.Current.CancellationToken);
                Assert.False(pending.IsCompleted);
                Assert.Single(transport.Writes);
            }

            return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false' sector_size='512'/></data>")];
        };

        await RunCommandAsync(protocol, command, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(pending);
        if (command == "power")
        {
            await Assert.ThrowsAsync<EdlException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        else
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        Assert.Equal(expectedWrites, transport.Writes.Count);
        Assert.Contains(command == "power" ? "<power " : "<nop ", Encoding.UTF8.GetString(transport.Writes[^1]), StringComparison.Ordinal);
        Assert.Equal(command == "power", protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ProgramAsync_InvalidSource_ReleasesGateWithoutFaultingSession()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);
        using var source = new MemoryStream();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(Complete)];

        await Assert.ThrowsAsync<ArgumentException>(() =>
            protocol.ProgramAsync(source, 0, 1, cancellationToken: TestContext.Current.CancellationToken));
        var response = await protocol.NopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Single(transport.Writes);
        Assert.False(protocol.RequiresReconnect);
    }

    private static FirehoseProtocol CreateProtocol(ScriptedCommTransport transport) => new(transport, new FirehoseConfiguration
    {
        SectorSize = 512,
        AutoDetectSectorSize = false,
        ResponseTimeoutSeconds = 5,
        PowerResetVerifyTimeoutMs = 0
    }, timeoutMs: 10);

    private static async Task RunCommandAsync(FirehoseProtocol protocol, string command, CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        using var source = new MemoryStream(new byte[512]);
        await (command switch
        {
            "nop" => protocol.NopAsync(cancellationToken),
            "configure" => protocol.ConfigureAsync(cancellationToken: cancellationToken),
            "detect" => protocol.AutoDetectSectorSizeAsync(cancellationToken),
            "storage" => protocol.GetStorageInfoAsync(cancellationToken: cancellationToken),
            "bootable" => protocol.SetBootableStorageDriveAsync(0, cancellationToken),
            "slot" => protocol.SetActivePartitionAsync("a", cancellationToken),
            "erase" => protocol.EraseAsync(0, 1, cancellationToken: cancellationToken),
            "patch" => protocol.PatchAsync("DISK", 0, 0, 0, "1", 4, cancellationToken),
            "read" => protocol.ReadAsync(0, 1, cancellationToken: cancellationToken),
            "stream" => protocol.ReadToStreamAsync(destination, 0, 1, cancellationToken: cancellationToken),
            "peek" => protocol.PeekAsync(512, 0, cancellationToken),
            "poke" => protocol.PokeAsync(4, 0, new byte[4], cancellationToken),
            "pokedword" => protocol.PokeDwordAsync(0, 1, cancellationToken),
            "memcpy" => protocol.MemcpyAsync(0, 512, 512, cancellationToken),
            "ufs" => protocol.ProvisionUfsAsync(new UfsCommon(), [new UfsBody()], new UfsEpilogue(), cancellationToken: cancellationToken),
            "ufs-reset" => protocol.UfsResetAsync(cancellationToken),
            "power" => protocol.ResetAsync(cancellationToken),
            "program" => protocol.ProgramAsync(source, 0, 1, cancellationToken: cancellationToken),
            "wipe" => protocol.WipeAsync(0, 1, cancellationToken: cancellationToken),
            "verify" => protocol.VerifyProgramAsync(source, 0, 1, cancellationToken: cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        });
    }

    private sealed class PausedStream() : MemoryStream(new byte[512])
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}