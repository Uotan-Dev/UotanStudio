using UotanStudio.Mirroring;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Scripting;
using UotanStudio.Workflow.Execution;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class ScriptToolCatalogTests
{
    private static readonly string[] ExpectedToolKeys =
    [
        "Device.Partition.Erase",
        "Device.Partition.Flash",
        "Device.Partition.Table.Read",
        "Device.Power.Reboot",
        "Device.Property.Read",
        "Device.Recovery.Clean",
        "Device.Shell.Command",
        "Device.Slot.Query",
        "Device.Slot.Switch",
        "Device.State.Query",
        "Device.Target.Select",
        "Mirroring.Session.Start",
        "Mirroring.Session.Stop",
        "Network.Download.File"
    ];

    private static readonly string[] ConfirmingToolKeys =
    [
        "Device.Partition.Erase",
        "Device.Partition.Flash",
        "Device.Power.Reboot",
        "Device.Recovery.Clean",
        "Device.Shell.Command",
        "Device.Slot.Switch"
    ];

    [Fact]
    public void Create_ExposesAllExpectedToolKeys()
    {
        var tools = CreateTools(new StubGateway(), out _, out _);

        Assert.Equal(ExpectedToolKeys, tools.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void Create_RequiresConfirmation_MatchesDangerousTools()
    {
        var tools = CreateTools(new StubGateway(), out _, out _);

        var confirming = tools.Where(entry => entry.Value.RequiresConfirmation)
            .Select(entry => entry.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ConfirmingToolKeys, confirming);
    }

    [Fact]
    public async Task Invoke_SlotSwitch_RoutesToGateway()
    {
        var gateway = new StubGateway();
        var tools = CreateTools(gateway, out _, out var context);

        var result = await tools["Device.Slot.Switch"].Invoke(new ScriptToolInvocation(
            new Dictionary<string, string?> { ["serial"] = "S1" },
            context,
            TestContext.Current.CancellationToken));

        Assert.Equal("b", result["slot"]);
        Assert.NotNull(gateway.SlotSwitchDevice);
        Assert.Equal("S1", gateway.SlotSwitchDevice!.Serial);
    }

    [Fact]
    public async Task Invoke_MissingRequiredArgument_Throws()
    {
        var tools = CreateTools(new StubGateway(), out _, out var context);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tools["Device.Partition.Erase"].Invoke(
            new ScriptToolInvocation(
                new Dictionary<string, string?>(),
                context,
                TestContext.Current.CancellationToken)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools["Device.Shell.Command"].Invoke(
            new ScriptToolInvocation(
                new Dictionary<string, string?>(),
                context,
                TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Invoke_Download_RejectsNonHttpScheme()
    {
        var tools = CreateTools(new StubGateway(), out _, out var context);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tools["Network.Download.File"].Invoke(
            new ScriptToolInvocation(
                new Dictionary<string, string?> { ["url"] = "file:///tmp/boot.img", ["path"] = "boot.img" },
                context,
                TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Invoke_MirroringStart_DispatchesToGateway()
    {
        var tools = CreateTools(new StubGateway(), out _, out var context);

        await Assert.ThrowsAsync<NotSupportedException>(() => tools["Mirroring.Session.Start"].Invoke(
            new ScriptToolInvocation(
                new Dictionary<string, string?> { ["serial"] = "S1" },
                context,
                TestContext.Current.CancellationToken)));
    }

    private static IReadOnlyDictionary<string, ScriptTool> CreateTools(IScriptToolGateway gateway,
        out HttpClient httpClient, out TestExecutionContext context)
    {
        httpClient = new HttpClient();
        context = new TestExecutionContext(TestContext.Current.CancellationToken);
        return ScriptToolCatalog.Create(context, gateway, httpClient);
    }

    private sealed class StubGateway : IScriptToolGateway
    {
        private readonly DetectedDevice _device = new()
        {
            Key = "tcp:127.0.0.1:5555",
            Serial = "S1",
            Protocol = DeviceProtocolKind.Fastboot,
            Transport = DeviceTransportKind.Tcp
        };

        public DetectedDevice? SlotSwitchDevice { get; private set; }

        public IReadOnlyList<DetectedDevice> Scan() => [_device];

        public Task<string> RebootAsync(DetectedDevice device, string mode, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<FlashResult> FlashImageAsync(DetectedDevice device, string partition, string imagePath,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FlashResult> ErasePartitionAsync(DetectedDevice device, string partition,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FlashResult> GetDevicePropertyAsync(DetectedDevice device, string propertyName,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<(string Name, long Size)>> ReadPartitionTableAsync(DetectedDevice device,
            bool showAll, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<(string Name, long Size)>>([("boot", 1024L)]);

        public Task<string> GetCurrentSlotAsync(DetectedDevice device, CancellationToken cancellationToken)
            => Task.FromResult("a");

        public Task<string> SetOtherSlotAsync(DetectedDevice device, CancellationToken cancellationToken)
        {
            SlotSwitchDevice = device;
            return Task.FromResult("b");
        }

        public Task<string> RecoveryCleanAsync(DetectedDevice device, string mode,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> ShellAsync(DetectedDevice device, string command,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ManagedMirrorSession StartMirror(DetectedDevice device) => throw new NotSupportedException();
    }

    private sealed class TestExecutionContext(CancellationToken cancellationToken)
        : INodeExecutionContext, IUnifiedLog
    {
        public IUnifiedLog Log => this;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public void RegisterResource(IAsyncDisposable resource)
        {
        }

        public void ReportProgress(double progress)
        {
        }

        public void Write(LogLevel level, string source, string message)
        {
        }

        public void Write(LogLevel level, string source, string format, params object[] args)
        {
        }

        public void SetMinimumLevel(LogLevel level)
        {
        }
    }
}