using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Mirroring;
using UotanStudio.Services.Scripting;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class ScriptNodeViewModelTests
{
    private const string Script = "local out = tool.Device.Slot.Query('serial', 'S1')\nreturn out.slot";

    [Fact]
    public async Task ExecuteAsync_RegisteredScript_RunsAgainstInjectedGateway()
    {
        NodeFactoryRegistrar.RegisterAll();
        var package = VisualPluginPackageBuilder.CreateScript("Slot reader", "tests.slotreader", Script);
        NodeFactoryRegistrar.RegisterVisualPlugin(package);
        try
        {
            var gateway = new StubGateway();
            var node = new ScriptNodeViewModel(package, gateway: gateway);

            await node.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken));

            Assert.Equal(1, gateway.SlotQueryCount);
            Assert.Equal(true, node.Output.Single(connector => connector.Title == "Completed").Data);
        }
        finally
        {
            NodeFactoryRegistrar.UnregisterVisualPlugin(package.NodeTypeKey);
        }
    }

    [Fact]
    public async Task ExecuteAsync_VersionMismatch_ThrowsBeforeRunningScript()
    {
        NodeFactoryRegistrar.RegisterAll();
        var installed = VisualPluginPackageBuilder.CreateScript("Slot reader", "tests.slotreader", Script,
            version: "1.0.0");
        NodeFactoryRegistrar.RegisterVisualPlugin(installed);
        try
        {
            var executor = VisualPluginPackageBuilder.CreateScript("Slot reader", "tests.slotreader", Script,
                version: "2.0.0");
            var gateway = new StubGateway();
            var node = new ScriptNodeViewModel(executor, gateway: gateway);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                node.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken)));

            Assert.Contains("requires its exact installed script version", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, gateway.SlotQueryCount);
        }
        finally
        {
            NodeFactoryRegistrar.UnregisterVisualPlugin(installed.NodeTypeKey);
        }
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

        public int SlotQueryCount { get; private set; }

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
            bool showAll, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> GetCurrentSlotAsync(DetectedDevice device, CancellationToken cancellationToken)
        {
            SlotQueryCount++;
            return Task.FromResult("a");
        }

        public Task<string> SetOtherSlotAsync(DetectedDevice device, CancellationToken cancellationToken)
            => throw new NotSupportedException();

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