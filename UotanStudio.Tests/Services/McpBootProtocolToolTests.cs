using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Mcp;
using UotanStudio.Mcp.Tools;
using Xunit;

namespace UotanStudio.Tests.Services;

[Collection("MCP tool generation")]
public sealed class McpBootProtocolToolTests : IDisposable
{
    private static readonly string[] BootToolNames =
    [
        "mtk_identify",
        "mtk_peek"
    ];

    private static readonly string[] MutationVerbFragments =
    [
        "flash",
        "erase",
        "wipe",
        "format",
        "factory",
        "program",
        "patch",
        "poke",
        "write",
        "bypass",
        "exploit",
        "provision",
        "reboot",
        "reset",
        "power",
        "efuse",
        "seccfg",
        "oemkey",
        "rpmb",
        "slot"
    ];

    private static readonly DeviceEndpoint MtkEndpoint = new()
    {
        Source = "usb",
        Transport = "Native",
        Address = "mtk-device-key",
        Serial = "mtk-serial",
        Protocol = "mtk",
        Route = "direct-usb",
        UsbInfo = new UsbDeviceInfo
        {
            SourceApiKind = UsbApiKind.Native,
            VendorId = 0x0E8D,
            ProductId = 0x0003,
            DeviceKey = "mtk-device-key",
            SerialNumber = "mtk-serial"
        }
    };

    private readonly string _root;

    public McpBootProtocolToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotanstudio-mcp-boot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void BuildTools_DefaultConfig_RegistersEveryBootProtocolTool()
    {
        var names = ToolNames(allowFlash: false);

        Assert.Equal(BootToolNames.Length, names.Count(name => BootToolNames.Contains(name, StringComparer.Ordinal)));
        foreach (var name in BootToolNames)
        {
            Assert.Contains(name, names);
        }
    }

    [Fact]
    public void BuildTools_BootProtocolDescriptors_AreReadOnlyNonDestructiveAndUngated()
    {
        var descriptors = PluginToolCatalog.All
            .Where(descriptor => BootToolNames.Contains(descriptor.Name, StringComparer.Ordinal))
            .ToList();

        Assert.Equal(BootToolNames.Length, descriptors.Count);
        Assert.All(descriptors, descriptor =>
            Assert.True(descriptor.ReadOnly, $"{descriptor.Name} is not declared read-only."));
        Assert.All(descriptors, descriptor =>
            Assert.False(descriptor.Destructive, $"{descriptor.Name} is declared destructive."));
        Assert.All(descriptors, descriptor =>
            Assert.False(descriptor.FlashGated, $"{descriptor.Name} is gated behind --allow-flash."));
    }

    [Fact]
    public void BuildTools_BootProtocolTools_AdvertiseReadOnlyAndNonDestructiveOnTheWire()
    {
        var tools = ToolList(allowFlash: false)
            .Where(tool => BootToolNames.Contains(tool.ProtocolTool.Name, StringComparer.Ordinal))
            .ToList();

        Assert.Equal(BootToolNames.Length, tools.Count);
        foreach (var tool in tools)
        {
            var annotations = tool.ProtocolTool.Annotations;
            Assert.NotNull(annotations);
            Assert.True(annotations!.ReadOnlyHint, $"{tool.ProtocolTool.Name} is not advertised read-only.");
            Assert.False(annotations.DestructiveHint, $"{tool.ProtocolTool.Name} is advertised destructive.");
            Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Description));
        }
    }

    [Fact]
    public void BootProtocolToolNames_NoneCarriesAMutationVerb()
    {
        foreach (var name in BootToolNames)
        {
            foreach (var fragment in MutationVerbFragments)
            {
                Assert.False(name.Contains(fragment, StringComparison.Ordinal),
                    $"{name} advertises the mutation verb '{fragment}'.");
            }
        }
    }

    [Fact]
    public async Task MtkIdentifyAsync_LoggingOnlyEndpoint_ReportsRefusalWithoutRetrying()
    {
        var gateway = new BootProtocolGatewayFake
        {
            Failure = () => new NotSupportedException("The endpoint must not receive MTK commands.")
        };

        var result = await ToolGuard.RunAsync(
            token => BootProtocolToolHandlers.MtkIdentifyAsync(gateway, null, token),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("must not receive MTK commands", Text(result), StringComparison.Ordinal);
        Assert.Equal(1, gateway.MtkCalls);
    }

    [Fact]
    public async Task MtkPeekAsync_AddressOutsideThirtyTwoBits_FailsBeforeAnyDeviceTransaction()
    {
        var gateway = new BootProtocolGatewayFake();

        var result = await BootProtocolToolHandlers.MtkPeekAsync(gateway, null, (long)uint.MaxValue + 1, 64,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("32-bit address space", result.Summary, StringComparison.Ordinal);
        Assert.Equal(0, gateway.MtkCalls);
    }

    [Fact]
    public void MtkPayloadRecords_RoundTripThroughSourceGeneratedContext_PreserveEveryField()
    {
        AssertRoundTrip(new MtkChipPayload("mtk-serial", "Bootloader", "Handshake", "BootRom", "known pid",
            0x8112, 0x0001, 0x0002, 0x0003, 0x1F, true, true, true, true, 2, 1, "00ff", "ff00"));
        AssertRoundTrip(new MtkPeekPayload("mtk-serial", 0x10000, 4, "00112233", new string('d', 64)));
    }

    [Fact]
    public async Task RunMtkAsync_SessionTypeMismatch_ReleasesTheLeasedTransport()
    {
        var opener = new RecordingOpener { ReturnTransport = true };
        using var controller = new DeviceController([new Source("usb", [MtkEndpoint])], opener,
            [new StubSessionFactory(DeviceSessionKind.Mtk)]);
        var gateway = new HeadlessDeviceManagerGateway(controller, () => false);

        var result = await ToolGuard.RunAsync(
            token => BootProtocolToolHandlers.MtkIdentifyAsync(gateway, "mtk-serial", token),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("no mtk session", Text(result), StringComparison.Ordinal);
        Assert.Equal(1, opener.Calls);
        Assert.True(opener.Transport.Disposed);
    }

    [Fact]
    public async Task RunMtkAsync_EndpointAlreadyLeased_KeepsTheHeldTransportOpen()
    {
        var opener = new RecordingOpener { ReturnTransport = true };
        using var controller = new DeviceController([new Source("usb", [MtkEndpoint])], opener,
            [new StubSessionFactory(DeviceSessionKind.Mtk)]);
        var snapshot = await controller.RefreshAsync(TestContext.Current.CancellationToken);
        await using var held = await controller.OpenAsync(snapshot, MtkEndpoint.Id, DeviceSessionKind.Mtk,
            TestContext.Current.CancellationToken);
        var gateway = new HeadlessDeviceManagerGateway(controller, () => false);

        var result = await ToolGuard.RunAsync(
            token => BootProtocolToolHandlers.MtkPeekAsync(gateway, "mtk-device-key", 0, 64, token),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("already leased", Text(result), StringComparison.Ordinal);
        Assert.Equal(1, opener.Calls);
        Assert.False(opener.Transport.Disposed);
    }

    private static void AssertRoundTrip<T>(T payload)
    {
        var element = ToolJson.Data(payload);
        var info = McpToolJsonContext.Default.GetTypeInfo(typeof(T));

        Assert.NotNull(info);
        var restored = JsonSerializer.Deserialize(element.GetRawText(), (JsonTypeInfo<T>)info!);

        Assert.Equal(payload, restored);
    }

    private List<string> ToolNames(bool allowFlash) =>
        ToolList(allowFlash).Select(tool => tool.ProtocolTool.Name).ToList();

    private IReadOnlyList<McpServerTool> ToolList(bool allowFlash) =>
        new McpServerHost(new PathSandbox(_root), allowFlash).BuildTools();

    private static string Text(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(result.Content[0]).Text ?? string.Empty;

    private sealed class Source(string name, DeviceEndpoint[] endpoints) : IDeviceDiscoverySource
    {
        public string Name => name;

        public Task<IReadOnlyList<DeviceEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DeviceEndpoint>>(endpoints);
    }

    private sealed class StubSessionFactory(DeviceSessionKind kind) : IDeviceSessionFactory
    {
        public DeviceSessionKind Kind => kind;

        public Task<object> CreateAsync(DeviceEndpoint endpoint, IAsyncDisposable transport,
            CancellationToken cancellationToken) => Task.FromResult<object>(new object());
    }

    private sealed class RecordingOpener : IDeviceTransportOpener
    {
        public int Calls { get; private set; }
        public bool ReturnTransport { get; init; }
        public DisposableTransport Transport { get; } = new();

        public Task<IAsyncDisposable> OpenAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
        {
            Calls++;
            if (ReturnTransport)
            {
                return Task.FromResult<IAsyncDisposable>(Transport);
            }

            throw new InvalidOperationException("No endpoint should be opened.");
        }
    }

    private sealed class DisposableTransport : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BootProtocolGatewayFake : IDeviceManagerGateway, IBootProtocolGateway
    {
        public int EdlCalls { get; private set; }
        public int MtkCalls { get; private set; }
        public Func<Exception>? Failure { get; init; }

        public Task<TResult> RunEdlAsync<TResult>(string? target,
            Func<EdlSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EdlCalls++;
            if (Failure is { } failure)
            {
                throw failure();
            }

            throw new NotSupportedException("The fake gateway owns no EDL transport.");
        }

        public Task<TResult> RunMtkAsync<TResult>(string? target,
            Func<MtkSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MtkCalls++;
            if (Failure is { } failure)
            {
                throw failure();
            }

            throw new NotSupportedException("The fake gateway owns no MTK transport.");
        }

        public IReadOnlyList<UsbDeviceInfo> ListUsbDevices() => [];

        public IReadOnlyList<(string Serial, string State)> ListAdbTargets() => [];

        public TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void PushAdbFile(string serial, string localPath, string remotePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void PullAdbFile(string serial, string remotePath, string localPath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public TResult RunFastboot<TResult>(string? target, Func<FastbootSession, TResult> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public (string ServiceName, string Password, string Payload) CreateQrPairingSession() =>
            throw new NotSupportedException();

        public Task<(string Route, string Address)> PairQrWirelessAsync(string serviceName, string password,
            int timeoutSeconds = 180, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class InventoryOnlyGateway : IDeviceManagerGateway
    {
        public IReadOnlyList<UsbDeviceInfo> ListUsbDevices() =>
            [new UsbDeviceInfo { SourceApiKind = UsbApiKind.Native, DeviceKey = "mtk-device-key" }];

        public IReadOnlyList<(string Serial, string State)> ListAdbTargets() => [];

        public TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void PushAdbFile(string serial, string localPath, string remotePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void PullAdbFile(string serial, string remotePath, string localPath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public TResult RunFastboot<TResult>(string? target, Func<FastbootSession, TResult> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public (string ServiceName, string Password, string Payload) CreateQrPairingSession() =>
            throw new NotSupportedException();

        public Task<(string Route, string Address)> PairQrWirelessAsync(string serviceName, string password,
            int timeoutSeconds = 180, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}