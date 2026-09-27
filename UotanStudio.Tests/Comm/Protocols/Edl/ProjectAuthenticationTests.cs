using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed partial class ProjectAuthenticationTests
{
    private const string Ack = "<data><response value=\"ACK\"/></data>";

    [Theory]
    [InlineData(EdlProjectAuthenticationKind.OnePlusDemacia, "demacia")]
    [InlineData(EdlProjectAuthenticationKind.OnePlusProject, "setprojmodel")]
    [InlineData(EdlProjectAuthenticationKind.OnePlusSoftwareProject, "setswprojmodel")]
    [InlineData(EdlProjectAuthenticationKind.NothingProject, "ntprojectverify")]
    public async Task AuthenticateProjectAsync_SelectedProfile_UsesExactFieldsAndClearsCredentials(
        EdlProjectAuthenticationKind kind, string command)
    {
        using var transport = new ScriptedCommTransport();
        EnqueuePrelude(transport, kind);
        transport.Enqueue($"<data><{command} verify_res=\"0\" model_check=\"0\" auth_token_verify=\"0\"/><response value=\"ACK\"/></data>");
        using var protocol = new FirehoseProtocol(transport);
        var credentials = Credentials(kind);
        var provider = new Provider((actualKind, project, serial, timestamp, token) =>
        {
            Assert.Equal(kind, actualKind);
            Assert.Equal("22111", project);
            Assert.Equal(42ul, serial);
            Assert.Equal(kind == EdlProjectAuthenticationKind.OnePlusSoftwareProject ? 123456L : (long?)null, timestamp);
            Assert.True(token.CanBeCanceled);
            return Task.FromResult<IReadOnlyDictionary<string, byte[]>>(credentials);
        });

        var result = await protocol.AuthenticateProjectAsync(Options(kind, provider),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsAck);
        Assert.False(protocol.RequiresReconnect);
        Assert.Equal(1, provider.Calls);
        var element = XDocument.Parse(Encoding.UTF8.GetString(transport.Writes[^1])).Root!.Element(command)!;
        foreach (var pair in credentials)
        {
            Assert.Equal(pair.Value.Length, element.Attribute(pair.Key)!.Value.Length);
            Assert.All(pair.Value, value => Assert.Equal(0, value));
        }
        Assert.Equal(credentials.Count, element.Attributes().Count());
    }

    [Theory]
    [InlineData(EdlProjectAuthenticationKind.OnePlusDemacia, "verify_res=\"1\"")]
    [InlineData(EdlProjectAuthenticationKind.OnePlusDemacia, "")]
    [InlineData(EdlProjectAuthenticationKind.OnePlusProject, "model_check=\"0\"")]
    [InlineData(EdlProjectAuthenticationKind.OnePlusProject, "model_check=\"1\" auth_token_verify=\"0\"")]
    [InlineData(EdlProjectAuthenticationKind.OnePlusProject, "model_check=\"0\" auth_token_verify=\"1\"")]
    [InlineData(EdlProjectAuthenticationKind.OnePlusSoftwareProject, "model_check=\"0\" auth_token_verify=\"00\"")]
    public async Task AuthenticateProjectAsync_AckWithoutSuccessfulVerification_Faults(
        EdlProjectAuthenticationKind kind, string attributes)
    {
        using var transport = new ScriptedCommTransport();
        EnqueuePrelude(transport, kind);
        transport.Enqueue($"<data><response value=\"ACK\" {attributes}/></data>");
        using var protocol = new FirehoseProtocol(transport);
        var credentials = Credentials(kind);
        var provider = new Provider((_, _, _, _, _) => Task.FromResult<IReadOnlyDictionary<string, byte[]>>(credentials));

        await Assert.ThrowsAsync<FirehoseException>(() => protocol.AuthenticateProjectAsync(Options(kind, provider),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(protocol.RequiresReconnect);
        Assert.Equal(1, provider.Calls);
        Assert.All(credentials.Values, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("not-a-time")]
    public async Task AuthenticateProjectAsync_InvalidDeviceTimestamp_DoesNotInvokeProvider(string timestamp)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue($"<data><response value=\"ACK\" device_timestamp=\"{timestamp}\"/></data>");
        using var protocol = new FirehoseProtocol(transport);
        var provider = new Provider((_, _, _, _, _) => throw new InvalidOperationException());

        await Assert.ThrowsAsync<FirehoseException>(() => protocol.AuthenticateProjectAsync(
            Options(EdlProjectAuthenticationKind.OnePlusSoftwareProject, provider),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, provider.Calls);
        Assert.Single(transport.Writes);
        Assert.True(protocol.RequiresReconnect);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("short")]
    [InlineData("xml")]
    [InlineData("unicode")]
    public async Task AuthenticateProjectAsync_InvalidCredentials_RejectsBeforeSending(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new FirehoseProtocol(transport);
        var credentials = Credentials(EdlProjectAuthenticationKind.OnePlusProject);
        switch (failure)
        {
            case "missing": credentials.Remove("token"); break;
            case "extra": credentials.Add("unexpected", [1]); break;
            case "short": credentials["token"] = new byte[1]; break;
            case "xml": credentials["token"][0] = (byte)'<'; break;
            case "unicode": credentials["pk"][0] = 0xFF; break;
        }
        var provider = new Provider((_, _, _, _, _) => Task.FromResult<IReadOnlyDictionary<string, byte[]>>(credentials));

        await Assert.ThrowsAsync<FirehoseException>(() => protocol.AuthenticateProjectAsync(
            Options(EdlProjectAuthenticationKind.OnePlusProject, provider),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.True(protocol.RequiresReconnect);
        Assert.All(credentials.Values, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task AuthenticateProjectAsync_CancelledProvider_DoesNotSubmitCredentials()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new FirehoseProtocol(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var credentials = Credentials(EdlProjectAuthenticationKind.OnePlusProject);
        var provider = new Provider((_, _, _, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult<IReadOnlyDictionary<string, byte[]>>(credentials);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => protocol.AuthenticateProjectAsync(
            Options(EdlProjectAuthenticationKind.OnePlusProject, provider), cancellationToken: cancellation.Token));

        Assert.Empty(transport.Writes);
        Assert.True(protocol.RequiresReconnect);
        Assert.All(credentials.Values, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task InitializeAsync_ProjectAuthentication_PrecedesStorageDetection()
    {
        using var transport = new ScriptedCommTransport { OnWrite = _ => [Encoding.UTF8.GetBytes(Ack)] };
        using var session = new EdlSession(transport);
        await session.ProbeProtocolAsync(TestContext.Current.CancellationToken);
        var provider = new Provider((_, _, _, _, _) => Task.FromResult<IReadOnlyDictionary<string, byte[]>>(
            Credentials(EdlProjectAuthenticationKind.NothingProject)));
        var options = new EdlInitializationOptions
        {
            ProjectAuthentication = Options(EdlProjectAuthenticationKind.NothingProject, provider)
        };

        await session.InitializeAsync(null, options, TestContext.Current.CancellationToken);

        Assert.True(session.IsConfigured);
        var commands = transport.Writes.Select(bytes => XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!
            .Elements().Single().Name.LocalName).ToArray();
        Assert.Equal(new[] { "nop", "configure", "checkntfeature", "ntprojectverify", "getstorageinfo" }, commands);
    }

    private static void EnqueuePrelude(ScriptedCommTransport transport, EdlProjectAuthenticationKind kind)
    {
        if (kind == EdlProjectAuthenticationKind.OnePlusSoftwareProject)
        {
            transport.Enqueue("<data><response value=\"ACK\" device_timestamp=\"123456\"/></data>");
        }
        else if (kind == EdlProjectAuthenticationKind.NothingProject)
        {
            transport.Enqueue(Ack);
        }
    }

    private static EdlProjectAuthenticationOptions Options(EdlProjectAuthenticationKind kind, Provider provider) => new()
    {
        Kind = kind,
        ProjectId = "22111",
        SerialNumber = 42,
        Provider = provider
    };

    private static Dictionary<string, byte[]> Credentials(EdlProjectAuthenticationKind kind) =>
        kind == EdlProjectAuthenticationKind.NothingProject
            ? new()
            {
                ["token1"] = Encoding.ASCII.GetBytes(new string('a', 32)),
                ["token2"] = Encoding.ASCII.GetBytes(new string('b', 64)),
                ["token3"] = Encoding.ASCII.GetBytes(new string('c', 64))
            }
            : new()
            {
                ["pk"] = Encoding.ASCII.GetBytes("0123456789ABCDEF"),
                ["token"] = Encoding.ASCII.GetBytes(new string('A', kind == EdlProjectAuthenticationKind.OnePlusSoftwareProject ? 1024 : 512))
            };

    private sealed class Provider(
        Func<EdlProjectAuthenticationKind, string, ulong, long?, CancellationToken, Task<IReadOnlyDictionary<string, byte[]>>> callback)
        : IEdlProjectAuthenticationProvider
    {
        internal int Calls { get; private set; }

        public Task<IReadOnlyDictionary<string, byte[]>> CreateCredentialsAsync(EdlProjectAuthenticationKind kind,
            string projectId, ulong serialNumber, long? deviceTimestamp, CancellationToken cancellationToken)
        {
            Calls++;
            return callback(kind, projectId, serialNumber, deviceTimestamp, cancellationToken);
        }
    }
}