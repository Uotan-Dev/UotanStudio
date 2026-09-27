using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed partial class ProjectAuthenticationTests
{
    [Theory]
    [InlineData(EdlProjectAuthenticationKind.OnePlusProject)]
    [InlineData(EdlProjectAuthenticationKind.OnePlusSoftwareProject)]
    public async Task ProgramAndPatchAsync_AuthenticatedOnePlus_RequestsFreshTokensForEachCommand(EdlProjectAuthenticationKind kind)
    {
        using var transport = new ScriptedCommTransport();
        EnqueuePrelude(transport, kind);
        transport.Enqueue("<data><response value=\"ACK\" model_check=\"0\" auth_token_verify=\"0\"/></data>");
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration { SectorSize = 512 });
        var provider = new Provider((_, _, _, _, _) => Task.FromResult<IReadOnlyDictionary<string, byte[]>>(Credentials(kind)));
        var tokenProvider = new CommandTokens();
        await protocol.AuthenticateProjectAsync(Options(kind, provider) with { CommandTokenProvider = tokenProvider },
            cancellationToken: TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;
        for (var index = 0; index < 2; index++)
        {
            transport.Enqueue("<data><response value=\"ACK\" rawmode=\"true\"/></data>");
            transport.Enqueue(Ack);
            using var source = new MemoryStream(new byte[512]);
            await protocol.ProgramAsync(source, index, 1, cancellationToken: TestContext.Current.CancellationToken);
        }
        transport.Enqueue(Ack);
        await protocol.PatchAsync("DISK", 0, 1, 0, "123", 4, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "program", "program", "patch" }, tokenProvider.Commands);
        var commands = transport.Writes.Skip(before).Where(bytes => bytes[0] == (byte)'<')
            .Select(bytes => XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single()).ToArray();
        Assert.Equal(3, commands.Length);
        for (var index = 0; index < commands.Length; index++)
        {
            Assert.Equal("0123456789ABCDEF", commands[index].Attribute("pk")!.Value);
            Assert.Equal(new string((char)('B' + index), kind == EdlProjectAuthenticationKind.OnePlusSoftwareProject ? 1024 : 512),
                commands[index].Attribute("token")!.Value);
        }
        Assert.All(tokenProvider.Buffers, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task ProgramAsync_OnePlusWithoutCommandProvider_RejectsBeforeProgramIo()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("<data><response value=\"ACK\" model_check=\"0\" auth_token_verify=\"0\"/></data>");
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration { SectorSize = 512 });
        var provider = new Provider((_, _, _, _, _) => Task.FromResult<IReadOnlyDictionary<string, byte[]>>(
            Credentials(EdlProjectAuthenticationKind.OnePlusProject)));
        await protocol.AuthenticateProjectAsync(Options(EdlProjectAuthenticationKind.OnePlusProject, provider),
            cancellationToken: TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;
        using var source = new MemoryStream(new byte[512]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.ProgramAsync(source, 0, 1,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(0, source.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgramAsync_CommandTokenInvalidOrCancelled_DoesNotSendCommand(bool cancel)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("<data><response value=\"ACK\" model_check=\"0\" auth_token_verify=\"0\"/></data>");
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration { SectorSize = 512 });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var provider = new Provider((_, _, _, _, _) => Task.FromResult<IReadOnlyDictionary<string, byte[]>>(
            Credentials(EdlProjectAuthenticationKind.OnePlusProject)));
        var tokenProvider = new CommandTokens { Invalid = !cancel, BeforeReturn = cancel ? cancellation.Cancel : null };
        await protocol.AuthenticateProjectAsync(Options(EdlProjectAuthenticationKind.OnePlusProject, provider) with
        {
            CommandTokenProvider = tokenProvider
        }, cancellationToken: cancellation.Token);
        var before = transport.Writes.Count;
        using var source = new MemoryStream(new byte[512]);

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => protocol.ProgramAsync(source, 0, 1,
                cancellationToken: cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<FirehoseException>(() => protocol.ProgramAsync(source, 0, 1,
                cancellationToken: cancellation.Token));
        }

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(0, source.Position);
        Assert.True(protocol.RequiresReconnect);
        Assert.All(tokenProvider.Buffers, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
    }

    private sealed class CommandTokens : IEdlOnePlusCommandTokenProvider
    {
        internal List<string> Commands { get; } = [];
        internal List<byte[]> Buffers { get; } = [];
        internal Action? BeforeReturn { get; init; }
        internal bool Invalid { get; init; }

        public Task<IReadOnlyDictionary<string, byte[]>> CreateCommandTokenAsync(EdlProjectAuthenticationKind kind,
            string projectId, ulong serialNumber, long? deviceTimestamp, string command, CancellationToken cancellationToken)
        {
            Assert.Equal("22111", projectId);
            Assert.Equal(42ul, serialNumber);
            Assert.Equal(kind == EdlProjectAuthenticationKind.OnePlusSoftwareProject ? 123456L : (long?)null, deviceTimestamp);
            Commands.Add(command);
            var credentials = Credentials(kind);
            credentials["token"].AsSpan().Fill(Invalid ? (byte)'Z' : (byte)('A' + Commands.Count));
            Buffers.AddRange(credentials.Values);
            BeforeReturn?.Invoke();
            return Task.FromResult<IReadOnlyDictionary<string, byte[]>>(credentials);
        }
    }
}