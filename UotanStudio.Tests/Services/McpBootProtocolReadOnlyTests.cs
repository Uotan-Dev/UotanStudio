using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Mcp;
using UotanStudio.Mcp.Tools;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware.Architecture;
using Xunit;

namespace UotanStudio.Tests.Services;

[Collection("MCP tool generation")]
public sealed class McpBootProtocolReadOnlyTests : IDisposable
{
    private static readonly string[] BootToolNames =
    [
        "mtk_identify",
        "mtk_peek"
    ];

    private static readonly string[] AdvertisedToolFamilies = ["adb", "fastboot", "mtk"];

    private static readonly string[] AdvertisedClassifierLabels =
        ["adb", "edl", "fastboot", "hdc", "mtk", "rkdl"];

    private static readonly string[] AllowedAsyncCallsInBootFactory =
    [
        "InitializeAsync",
        "MtkIdentifyAsync",
        "MtkPeekAsync",
        "PeekPreloaderMemoryAsync",
        "RunAsync",
        "RunMtkAsync"
    ];

    private static readonly IReadOnlySet<byte> ReadOnlyMtkBootCommands =
        new HashSet<byte>([0xA0, 0x0A, 0x50, 0x05, 0xD0, 0xD1, 0xD8, 0xE1, 0xE7, 0xFC, 0xFD, 0xFE, 0xFF]);

    private readonly string _root;

    public McpBootProtocolReadOnlyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uotanstudio-mcp-boot-readonly-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void BootProtocolFactorySource_EveryAwaitedCallIsOnTheReadOnlyAllowList()
    {
        var source = ReadBootFactorySource();
        var awaited = Regex.Matches(source, @"\.(?<name>[A-Za-z_][A-Za-z0-9_]*Async)\(")
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AllowedAsyncCallsInBootFactory, awaited);
    }

    [Fact]
    public void BootProtocolFactoryMethods_EmittedToolNames_AreCataloguedExactlyOnce()
    {
        var gateway = new SessionlessGateway();
        var names = new List<string>
        {
            EmittedName(BootProtocolToolFactory.CreateMtkIdentify(gateway)),
            EmittedName(BootProtocolToolFactory.CreateMtkPeek(gateway))
        };

        Assert.Equal(BootToolNames.Length, names.Count);
        Assert.Equal(BootToolNames.Order(StringComparer.Ordinal).ToArray(),
            names.Order(StringComparer.Ordinal).ToArray());
        foreach (var name in names)
        {
            Assert.Equal(1, PluginToolCatalog.All.Count(descriptor =>
                string.Equals(descriptor.Name, name, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void BuildTools_BootToolFamily_HasNoRkdlOrHdcTransactingTool()
    {
        var names = ToolNames(allowFlash: true);

        Assert.Equal(BootToolNames.Length,
            names.Count(name => name.StartsWith("edl_", StringComparison.Ordinal)
                || name.StartsWith("mtk_", StringComparison.Ordinal)));
        Assert.DoesNotContain(names, name => name.StartsWith("rkdl_", StringComparison.Ordinal)
            || name.StartsWith("hdc_", StringComparison.Ordinal));
    }

    [Fact]
    public void ListDevicesDescription_AdvertisedToolFamilies_EachResolveToARegisteredTool()
    {
        var description = DescriptionOf("list_devices");
        var families = Regex.Matches(description, @"\b([a-z]+)_\*")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var names = ToolNames(allowFlash: true);

        Assert.Equal(AdvertisedToolFamilies, families);
        foreach (var family in families)
        {
            Assert.Contains(names, name => name.StartsWith(family + "_", StringComparison.Ordinal));
        }

        Assert.Contains("rkdl and hdc endpoints are labelled only", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ListDevicesDescription_ListedLabelSet_MatchesEveryProtocolTheCopyEnumerates()
    {
        var description = DescriptionOf("list_devices");
        var labels = Regex.Match(description, @"\((?<labels>[a-z, ]+)\)").Groups["labels"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AdvertisedClassifierLabels, labels);
    }

    [Fact]
    public async Task MtkIdentifyAsync_ScriptedBootStageDevice_ReportsIdentityFromReadOnlyQueries()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue([
            0x5F, 0xF5, 0xAF, 0xFA,
            0xFD, 0x07, 0x66, 0, 0,
            0xFC, 0x8A, 0, 0xCA, 1, 0, 2, 0, 0,
            0xD8, 0, 0, 0, 0x17, 0, 0
        ]);
        transport.Enqueue([0, 0, 0, 0]);
        using var session = new MtkSession(transport);
        var gateway = new SingleMtkSessionGateway(session);

        var result = await BootProtocolToolHandlers.MtkIdentifyAsync(gateway, "mtk-serial",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        var payload = PayloadOf(result, McpToolJsonContext.Default.MtkChipPayload);
        Assert.Equal(0x0766, payload.HardwareCode);
        Assert.Equal(0x17L, payload.TargetConfiguration);
        Assert.True(payload.SecureBootEnabled);
        Assert.True(payload.RootCertificateRequired);

        var commands = transport.Writes.Where(write => write.Length == 1).Select(write => write[0]).ToArray();
        Assert.NotEmpty(commands);
        Assert.All(commands, command => Assert.True(ReadOnlyMtkBootCommands.Contains(command),
            $"0x{command:X2} is not a read-only MTK boot command."));
        Assert.Contains((byte)0xFD, commands);
        Assert.Contains((byte)0xFC, commands);
        Assert.Contains((byte)0xD8, commands);
    }

    private static T PayloadOf<T>(ToolResult result, JsonTypeInfo<T> typeInfo)
    {
        Assert.NotNull(result.Data);
        return JsonSerializer.Deserialize(result.Data!.Value.GetRawText(), typeInfo)
            ?? throw new InvalidOperationException("The tool result carried no JSON payload.");
    }

    private static string ReadBootFactorySource()
    {
        Assert.True(FirmwareContextDependencyScanner.TryFindRepositoryRoot(AppContext.BaseDirectory,
                out var repositoryRoot),
            "No directory containing UotanStudio.slnx was found above the test host, so the boot-protocol "
            + "factory source cannot be read.");
        var path = Path.Combine(repositoryRoot!, "UotanStudio.Mcp", "Tools", "BootProtocolToolFactory.cs");
        Assert.True(File.Exists(path), $"The boot-protocol factory source is missing from {path}.");
        return File.ReadAllText(path);
    }

    private static string EmittedName(McpServerTool tool) => tool.ProtocolTool.Name
        ?? throw new InvalidOperationException("A boot-protocol tool was emitted without a protocol name.");

    private IReadOnlyList<string> ToolNames(bool allowFlash) =>
        [.. new McpServerHost(new PathSandbox(_root), allowFlash).BuildTools()
            .Select(tool => tool.ProtocolTool.Name ?? string.Empty)];

    private string DescriptionOf(string name) =>
        new McpServerHost(new PathSandbox(_root), allowFlash: true).BuildTools()
            .Single(tool => tool.ProtocolTool.Name == name).ProtocolTool.Description
        ?? throw new InvalidOperationException($"{name} is registered without a description.");

    private sealed class SingleMtkSessionGateway(MtkSession session)
        : IDeviceManagerGateway, IBootProtocolGateway
    {
        public Task<TResult> RunEdlAsync<TResult>(string? target,
            Func<EdlSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
            => throw new NotSupportedException("This fake leases no EDL endpoint.");

        public Task<TResult> RunMtkAsync<TResult>(string? target,
            Func<MtkSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
            => operation(session, cancellationToken);

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

    private sealed class SessionlessGateway : IDeviceManagerGateway, IBootProtocolGateway
    {
        public Task<TResult> RunEdlAsync<TResult>(string? target,
            Func<EdlSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
            => throw new NotSupportedException("This fake leases no EDL endpoint.");

        public Task<TResult> RunMtkAsync<TResult>(string? target,
            Func<MtkSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
            => throw new NotSupportedException("This fake leases no MTK endpoint.");

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
}