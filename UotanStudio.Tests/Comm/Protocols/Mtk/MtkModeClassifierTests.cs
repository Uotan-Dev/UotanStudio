using UotanStudio.Comm.Protocols.Mtk.Discovery;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed class MtkModeClassifierTests
{
    [Fact]
    public void Classify_BootRomProductIdWithoutInterfaceMetadata_ReturnsBootRomAndNamesMissingMetadata()
    {
        var device = new UsbDeviceInfo { VendorId = 0x0E8D, ProductId = 0x0003 };

        var result = MtkModeClassifier.Classify(device);

        Assert.Equal(MtkUsbMode.BootRom, result.Mode);
        Assert.Contains("interface metadata was not observed", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_BootRomProductIdWithCommandPair_ReturnsBootRom()
    {
        var result = MtkModeClassifier.Classify(Device(0x0003, CommandInterface(0)));

        Assert.Equal(MtkUsbMode.BootRom, result.Mode);
        Assert.False(string.IsNullOrEmpty(result.Reason));
    }

    [Fact]
    public void Classify_NonMediaTekVendor_ReturnsUnknown()
    {
        var device = new UsbDeviceInfo { VendorId = 0x05C6, ProductId = 0x0003, InterfaceMetadataObserved = true };

        var result = MtkModeClassifier.Classify(device);

        Assert.Equal(MtkUsbMode.Unknown, result.Mode);
        Assert.Contains("not the MediaTek vendor", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_UnknownMediaTekProductId_ReturnsUnknown()
    {
        var result = MtkModeClassifier.Classify(Device(0x1234, CommandInterface(0)));

        Assert.Equal(MtkUsbMode.Unknown, result.Mode);
        Assert.Contains("not a known MediaTek boot product id", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_DownloadProductIdWithoutInterfaceMetadata_ReturnsUnknown()
    {
        var device = new UsbDeviceInfo { VendorId = 0x0E8D, ProductId = 0x2000 };

        var result = MtkModeClassifier.Classify(device);

        Assert.Equal(MtkUsbMode.Unknown, result.Mode);
        Assert.Contains("interface metadata was not observed", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_SingleCommandPairWithoutLoggingInterface_ReturnsPreloaderAndNamesCrossCheck()
    {
        var result = MtkModeClassifier.Classify(Device(0x2000, CommandInterface(0)));

        Assert.Equal(MtkUsbMode.Preloader, result.Mode);
        Assert.Contains("connection-agent query", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_CommandPairWithLoggingInterface_ReturnsDaRuntime()
    {
        var result = MtkModeClassifier.Classify(Device(0x2000,
            CommandInterface(0),
            LoggingInterface(1)));

        Assert.Equal(MtkUsbMode.DaRuntime, result.Mode);
        Assert.Contains("logging-only", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_LogOnlyInterfacesWithoutCommandPair_ReturnsLoggingOnly()
    {
        var result = MtkModeClassifier.Classify(Device(0x2001,
            LoggingInterface(0),
            LoggingInterface(1, endpointIn: 0x83)));

        Assert.Equal(MtkUsbMode.LoggingOnly, result.Mode);
        Assert.Contains("logging-only vendor interface", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_TwoCommandPairs_ReturnsUnknownAndNamesAmbiguity()
    {
        var result = MtkModeClassifier.Classify(Device(0x2000,
            CommandInterface(0, subclass: 1),
            CommandInterface(1, subclass: 2)));

        Assert.Equal(MtkUsbMode.Unknown, result.Mode);
        Assert.Contains("must not be resolved by enumeration order", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_CommandPairWithAdbInterface_ReturnsUnknownAndNamesConflict()
    {
        var result = MtkModeClassifier.Classify(Device(0x2000,
            CommandInterface(0),
            AdbInterface(1)));

        Assert.Equal(MtkUsbMode.Unknown, result.Mode);
        Assert.Contains("conflicts", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_AdbInterfaceWithoutCommandPair_ReturnsAgentlessAdb()
    {
        var result = MtkModeClassifier.Classify(Device(0x2001, AdbInterface(0)));

        Assert.Equal(MtkUsbMode.AgentlessAdb, result.Mode);
        Assert.Contains("ADB debug-bridge", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_TwoDevicesWithSameVidPid_ClassifiesEachByTheirOwnEndpoints()
    {
        var commandDevice = Device(0x2000, CommandInterface(0));
        var loggingDevice = Device(0x2000, LoggingInterface(0));

        var commandResult = MtkModeClassifier.Classify(commandDevice);
        var loggingResult = MtkModeClassifier.Classify(loggingDevice);

        Assert.Equal(MtkUsbMode.Preloader, commandResult.Mode);
        Assert.Equal(MtkUsbMode.LoggingOnly, loggingResult.Mode);
    }

    [Fact]
    public void Classify_NonVendorClassInterfacesOnly_ReturnsUnknown()
    {
        var result = MtkModeClassifier.Classify(Device(0x2000, new UsbInterfaceInfo
        {
            InterfaceNumber = 0,
            Class = 0x02,
            SubClass = 0x01,
            Protocol = 0x00,
            Endpoints = [Endpoint(0x84), Endpoint(0x04)]
        }));

        Assert.Equal(MtkUsbMode.Unknown, result.Mode);
        Assert.Contains("without any bulk command", result.Reason, StringComparison.Ordinal);
    }

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
            Protocol = 0,
            Endpoints = [Endpoint(endpointIn), Endpoint(endpointOut)]
        };

    private static UsbInterfaceInfo LoggingInterface(byte number, byte endpointIn = 0x82) => new()
    {
        InterfaceNumber = number,
        Class = 0xFF,
        SubClass = 0,
        Protocol = 0,
        Endpoints = [Endpoint(endpointIn)]
    };

    private static UsbInterfaceInfo AdbInterface(byte number) => new()
    {
        InterfaceNumber = number,
        Class = 0xFF,
        SubClass = 0x42,
        Protocol = 0x01,
        Endpoints = [Endpoint(0x85), Endpoint(0x05)]
    };

    private static UsbEndpointInfo Endpoint(byte address) => new()
    {
        EndpointAddress = address,
        Attributes = 0x02,
        MaxPacketSize = 512
    };
}