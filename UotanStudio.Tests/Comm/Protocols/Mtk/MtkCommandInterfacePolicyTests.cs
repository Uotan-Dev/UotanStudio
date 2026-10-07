using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Discovery;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed class MtkCommandInterfacePolicyTests
{
    [Fact]
    public void Select_SingleVendorBulkPair_ReturnsSelectedInterfaceNumber()
    {
        var result = MtkCommandInterfacePolicy.Select(Device(CommandInterface(2, endpointIn: 0x83, endpointOut: 0x03)));

        Assert.Equal(MtkCommandInterfaceStatus.Selected, result.Status);
        Assert.Equal((byte)2, result.InterfaceNumber);
        Assert.Contains("only bulk command pair", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_LoggingInterfaceAlongsideCommandPair_SelectsCommandInterfaceOnly()
    {
        var result = MtkCommandInterfacePolicy.Select(Device(
            LoggingInterface(0),
            CommandInterface(1, endpointIn: 0x83, endpointOut: 0x03)));

        Assert.Equal(MtkCommandInterfaceStatus.Selected, result.Status);
        Assert.Equal((byte)1, result.InterfaceNumber);
    }

    [Fact]
    public void Select_LogOnlyEndpoint_ReturnsLoggingOnly()
    {
        var result = MtkCommandInterfacePolicy.Select(Device(LoggingInterface(0)));

        Assert.Equal(MtkCommandInterfaceStatus.LoggingOnly, result.Status);
        Assert.Null(result.InterfaceNumber);
        Assert.Contains("must not be sent here", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_TwoCommandPairsWithoutFilter_ReturnsAmbiguous()
    {
        var result = MtkCommandInterfacePolicy.Select(Device(
            CommandInterface(0, subclass: 1),
            CommandInterface(1, subclass: 2)));

        Assert.Equal(MtkCommandInterfaceStatus.Ambiguous, result.Status);
        Assert.Contains("0, 1", result.Reason, StringComparison.Ordinal);
        Assert.Contains("enumeration order is forbidden", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_TwoCommandPairsWithSubClassFilter_ReturnsSelected()
    {
        var device = Device(
            CommandInterface(0, subclass: 1),
            CommandInterface(1, subclass: 2));

        var result = MtkCommandInterfacePolicy.Select(device, new MtkOpenOptions { CommandInterfaceSubClass = 2 });

        Assert.Equal(MtkCommandInterfaceStatus.Selected, result.Status);
        Assert.Equal((byte)1, result.InterfaceNumber);
    }

    [Fact]
    public void Select_BoundEndpointsBelongToCandidate_ReturnsSelected()
    {
        var device = Device(
            LoggingInterface(0),
            CommandInterface(1, endpointIn: 0x83, endpointOut: 0x03));

        var result = MtkCommandInterfacePolicy.Select(device, boundEndpointIn: 0x83, boundEndpointOut: 0x03);

        Assert.Equal(MtkCommandInterfaceStatus.Selected, result.Status);
        Assert.Equal((byte)1, result.InterfaceNumber);
    }

    [Fact]
    public void Select_BoundEndpointsBelongToNoCandidate_ReturnsBoundEndpointMismatch()
    {
        var device = Device(CommandInterface(0, endpointIn: 0x81, endpointOut: 0x01));

        var result = MtkCommandInterfacePolicy.Select(device, boundEndpointIn: 0x86, boundEndpointOut: 0x06);

        Assert.Equal(MtkCommandInterfaceStatus.BoundEndpointMismatch, result.Status);
        Assert.Contains("wrong interface", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_NoInterfaceMetadata_ReturnsNoMetadata()
    {
        var device = new UsbDeviceInfo { VendorId = 0x0E8D, ProductId = 0x2000 };

        var result = MtkCommandInterfacePolicy.Select(device);

        Assert.Equal(MtkCommandInterfaceStatus.NoMetadata, result.Status);
        Assert.Contains("no per-interface metadata", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_VendorInterfaceWithoutBulkPair_ReturnsNoCommandInterface()
    {
        var device = Device(new UsbInterfaceInfo
        {
            InterfaceNumber = 0,
            Class = 0xFF,
            Endpoints = [new UsbEndpointInfo { EndpointAddress = 0x84, Attributes = 0x03 }]
        });

        var result = MtkCommandInterfacePolicy.Select(device);

        Assert.Equal(MtkCommandInterfaceStatus.NoCommandInterface, result.Status);
    }

    [Fact]
    public async Task OpenUsbAsync_LoggingOnlyEndpoint_ThrowsBeforeAnyProtocolByte()
    {
        using var transport = new ScriptedCommTransport();
        var device = Device(LoggingInterface(0));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MtkSession.OpenUsbAsync(transport, device, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("logging-only", error.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task OpenUsbAsync_ExpectedModeMismatch_ThrowsBeforeAnyProtocolByte()
    {
        using var transport = new ScriptedCommTransport();
        var device = Device(0x2000, CommandInterface(0));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MtkSession.OpenUsbAsync(
            transport, device,
            new MtkOpenOptions { ExpectedMode = MtkUsbMode.DaRuntime },
            TestContext.Current.CancellationToken));

        Assert.Contains("expected to present DaRuntime", error.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task OpenUsbAsync_AmbiguousCommandPairs_ThrowsBeforeAnyProtocolByte()
    {
        using var transport = new ScriptedCommTransport();
        var device = Device(0x2000,
            CommandInterface(0, subclass: 1),
            CommandInterface(1, subclass: 2));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MtkSession.OpenUsbAsync(transport, device, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("unambiguous command interface", error.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task OpenUsbAsync_KnownCommandPairDescriptor_InitializesAndRecordsDiscoveryEvidence()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);

        using var session = await MtkSession.OpenUsbAsync(transport, Device(0x2000, CommandInterface(0)),
            new MtkOpenOptions { ExpectedMode = MtkUsbMode.Preloader },
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.Bootloader, session.State);
        Assert.Equal(MtkUsbMode.Preloader, session.DiscoveredUsbMode!.Mode);
        var discovery = Assert.Single(session.Evidence,
            evidence => evidence.Phase == "mode-discovery");
        var selection = Assert.Single(session.Evidence,
            evidence => evidence.Phase == "command-interface-selection");
        Assert.Equal(CommandOutcomeKind.Completed, discovery.Outcome);
        Assert.Equal(CommandOutcomeKind.Completed, selection.Outcome);
    }

    [Fact]
    public async Task OpenUsbAsync_MissingInterfaceMetadata_KeepsLegacyOpenAndRecordsUnknownSelection()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        var device = new UsbDeviceInfo { VendorId = 0x0E8D, ProductId = 0x0003 };

        using var session = await MtkSession.OpenUsbAsync(transport, device,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.Bootloader, session.State);
        Assert.Equal(MtkUsbMode.BootRom, session.DiscoveredUsbMode!.Mode);
        var selection = Assert.Single(session.Evidence,
            evidence => evidence.Phase == "command-interface-selection");
        Assert.Equal(CommandOutcomeKind.Unknown, selection.Outcome);
    }

    private static UsbDeviceInfo Device(params UsbInterfaceInfo[] interfaces) =>
        Device(0x2000, interfaces);

    private static UsbDeviceInfo Device(ushort productId, params UsbInterfaceInfo[] interfaces) => new()
    {
        VendorId = 0x0E8D,
        ProductId = productId,
        InterfaceMetadataObserved = true,
        Interfaces = interfaces
    };

    private static UsbInterfaceInfo CommandInterface(byte number, byte subclass = 0,
        byte endpointIn = 0x81, byte endpointOut = 0x01) => new()
        {
            InterfaceNumber = number,
            Class = 0xFF,
            SubClass = subclass,
            Endpoints = [Endpoint(endpointIn), Endpoint(endpointOut)]
        };

    private static UsbInterfaceInfo LoggingInterface(byte number) => new()
    {
        InterfaceNumber = number,
        Class = 0xFF,
        Endpoints = [Endpoint(0x82)]
    };

    private static UsbEndpointInfo Endpoint(byte address) => new()
    {
        EndpointAddress = address,
        Attributes = 0x02
    };

    private static void EnqueueIdentity(ScriptedCommTransport transport)
    {
        transport.Enqueue([
            0x5F, 0xF5, 0xAF, 0xFA,
            0xFD, 0x07, 0x66, 0, 0,
            0xFC, 0x8A, 0, 0xCA, 1, 0, 2, 0, 0,
            0xD8, 0, 0, 0, 0x17, 0, 0
        ]);
        transport.Enqueue([0, 0, 0, 0]);
    }
}