using System.Text;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class AdbRootCommandTests
{
    [Theory]
    [InlineData("0\n", true)]
    [InlineData("uid=0(root) gid=0(root)\n", true)]
    [InlineData("2000\n", false)]
    [InlineData("uid=2000(shell) gid=2000(shell)\n", false)]
    [InlineData("", false)]
    public void IsRootIdentity_OutputText_ClassifiesUidZero(string stdout, bool expected)
    {
        var result = AdbRootCommand.IsRootIdentity(Encoding.UTF8.GetBytes(stdout));

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ProbeAsync_RootedDirectShell_ReturnsAdbdRootWithoutSu()
    {
        var commands = new List<string>();
        var session = new FakeSession(command =>
        {
            commands.Add(command);
            return Result("0\n");
        });

        var mode = await AdbRootCommand.ProbeAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal(AdbRootMode.AdbdRoot, mode);
        Assert.Single(commands);
        Assert.Equal("id -u", commands[0]);
    }

    [Fact]
    public async Task ProbeAsync_SuElevation_ReturnsSu()
    {
        var commands = new List<string>();
        var session = new FakeSession(command =>
        {
            commands.Add(command);
            return command == "id -u" ? Result("2000\n") : Result("0\n");
        });

        var mode = await AdbRootCommand.ProbeAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal(AdbRootMode.Su, mode);
        Assert.Equal(new[] { "id -u", "su -c 'id -u'" }, commands);
    }

    [Fact]
    public async Task ProbeAsync_DeniedSu_ReturnsNone()
    {
        var session = new FakeSession(command => command == "id -u"
            ? Result("2000\n")
            : new ShellResult([], Encoding.UTF8.GetBytes("permission denied\n"), 1));

        var mode = await AdbRootCommand.ProbeAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal(AdbRootMode.None, mode);
    }

    [Fact]
    public async Task ProbeAsync_SuTimeout_ReturnsNone()
    {
        var session = new FakeSession(command => command == "id -u"
            ? Result("2000\n")
            : throw new TimeoutException("su did not answer."));

        var mode = await AdbRootCommand.ProbeAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal(AdbRootMode.None, mode);
    }

    [Fact]
    public async Task ProbeAsync_Cancelled_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var session = new FakeSession(_ => throw new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => AdbRootCommand.ProbeAsync(session, cancellation.Token));
    }

    [Fact]
    public void Build_AdbdRoot_LeavesCommandForDirectShell()
    {
        const string command = "dd if=/data/local/tmp/a.img of=/dev/block/by-name/boot bs=1048576 conv=fsync";

        var built = AdbRootCommand.Build(AdbRootMode.AdbdRoot, command);

        Assert.Equal(command, built);
    }

    [Fact]
    public void Build_Su_WrapsCommandForSuDashC()
    {
        var built = AdbRootCommand.Build(AdbRootMode.Su, "id -u");

        Assert.Equal("su -c 'id -u'", built);
    }

    [Fact]
    public void Build_Su_EscapesEmbeddedSingleQuote()
    {
        var built = AdbRootCommand.Build(AdbRootMode.Su, "echo a'b");

        Assert.Equal("su -c 'echo a'\\''b'", built);
    }

    [Fact]
    public void Build_NoRoot_ThrowsBeforeCommandRuns()
    {
        Assert.Throws<InvalidOperationException>(() => AdbRootCommand.Build(AdbRootMode.None, "id -u"));
    }

    [Fact]
    public void Build_EmptyCommand_Throws()
    {
        Assert.Throws<ArgumentException>(() => AdbRootCommand.Build(AdbRootMode.Su, " "));
    }

    private static ShellResult Result(string stdout) => new(Encoding.UTF8.GetBytes(stdout), [], 0);

    private sealed class FakeSession(Func<string, ShellResult> execute) : IAdbDeviceSession
    {
        public string ProtocolName => "adb";

        public bool IsConnected => true;

        public bool UsesServer => true;

        public uint PeerVersion => 0;

        public void Connect()
        {
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IAdbStream OpenStream(string service) => throw new NotSupportedException();

        public Stream OpenServiceStream(string service) => throw new NotSupportedException();

        public IAdbShellClient CreateShellClient(string command, string? term = null, bool pty = false)
            => new FakeShell(command, execute);

        public IAdbSyncClient CreateSyncClient(uint maxChunk = 64 * 1024) => throw new NotSupportedException();

        public IAdbDeviceServices CreateDeviceServices() => throw new NotSupportedException();

        public ShellResult ExecuteCommand(string command, int timeoutMs = 30000) => throw new NotSupportedException();

        public ValueTask<ShellResult> ExecuteCommandAsync(string command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class FakeShell(string command, Func<string, ShellResult> execute) : IAdbShellClient
    {
        public ValueTask<ShellResult> ExecuteAsync(int timeoutMs = 30000,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(execute(command));

        public ValueTask<int> ExecuteStreamingAsync(Action<byte[]>? onStdout, Action<byte[]>? onStderr,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}