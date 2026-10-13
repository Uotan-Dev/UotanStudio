using UotanStudio.Comm.Protocols.Mtk.Discovery;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk.Discovery;

public sealed class MtkModeClassifierPreloaderPortTests
{
    [Theory]
    [InlineData(0x2000)]
    [InlineData(0x2001)]
    [InlineData(0x20FF)]
    [InlineData(0x3000)]
    public void Classify_PreloaderPortProductIdWithSingleCommandPair_ReturnsPreloaderAndNamesProductId(
        ushort productId)
    {
        var result = MtkModeClassifier.Classify(Device(productId, CommandInterface(0)));

        Assert.Equal(MtkUsbMode.Preloader, result.Mode);
        Assert.Contains($"0x{productId:X4}", result.Reason, StringComparison.Ordinal);
        Assert.Contains("connection-agent query", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x2000)]
    [InlineData(0x2001)]
    [InlineData(0x20FF)]
    [InlineData(0x3000)]
    public void Classify_PreloaderPortProductIdWithLoggingInterface_ReturnsDaRuntime(ushort productId)
    {
        var result = MtkModeClassifier.Classify(Device(productId,
            CommandInterface(0),
            LoggingInterface(1)));

        Assert.Equal(MtkUsbMode.DaRuntime, result.Mode);
        Assert.Contains($"0x{productId:X4}", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x2000)]
    [InlineData(0x2001)]
    [InlineData(0x20FF)]
    [InlineData(0x3000)]
    public void Classify_PreloaderPortProductIdWithoutInterfaceMetadata_ReturnsUnknownAndNamesMissingMetadata(
        ushort productId)
    {
        var device = new UsbDeviceInfo { VendorId = 0x0E8D, ProductId = productId };

        var result = MtkModeClassifier.Classify(device);

        Assert.Equal(MtkUsbMode.Unknown, result.Mode);
        Assert.Contains("interface metadata was not observed", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("unique to the Boot ROM stage", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x20FF)]
    [InlineData(0x3000)]
    public void Classify_NewPreloaderPortProductIdWithCommandPair_IsNeverReportedAsBootRom(ushort productId)
    {
        var result = MtkModeClassifier.Classify(Device(productId, CommandInterface(0)));

        Assert.NotEqual(MtkUsbMode.BootRom, result.Mode);
        Assert.Equal(MtkUsbMode.Preloader, result.Mode);
    }

    [Theory]
    [InlineData(0x20FF)]
    [InlineData(0x3000)]
    public void Classify_NewPreloaderPortProductIdWithLoggingOnlyInterfaces_ReturnsLoggingOnly(ushort productId)
    {
        var result = MtkModeClassifier.Classify(Device(productId,
            LoggingInterface(0),
            LoggingInterface(1, endpointIn: 0x83)));

        Assert.Equal(MtkUsbMode.LoggingOnly, result.Mode);
        Assert.Contains("logging-only vendor interface", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x20FF)]
    [InlineData(0x3000)]
    public void Classify_NewPreloaderPortProductIdWithAdbInterfaceOnly_ReturnsAgentlessAdb(ushort productId)
    {
        var result = MtkModeClassifier.Classify(Device(productId, AdbInterface(0)));

        Assert.Equal(MtkUsbMode.AgentlessAdb, result.Mode);
        Assert.Contains("ADB debug-bridge", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x20FF)]
    [InlineData(0x3000)]
    public void Classify_NewPreloaderPortProductIdWithTwoCommandPairs_ReturnsUnknownAndNamesAmbiguity(
        ushort productId)
    {
        var result = MtkModeClassifier.Classify(Device(productId,
            CommandInterface(0, subclass: 1),
            CommandInterface(1, subclass: 2)));

        Assert.Equal(MtkUsbMode.Unknown, result.Mode);
        Assert.Contains("must not be resolved by enumeration order", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x20FE)]
    [InlineData(0x2002)]
    [InlineData(0x3001)]
    [InlineData(0x30FF)]
    [InlineData(0x1234)]
    public void Classify_MediaTekProductIdOutsideKnownBootProductIds_ReturnsUnknownAndNamesKnownSet(
        ushort productId)
    {
        var result = MtkModeClassifier.Classify(Device(productId, CommandInterface(0)));

        Assert.Equal(MtkUsbMode.Unknown, result.Mode);
        Assert.Contains("not a known MediaTek boot product id", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_KnownSetReason_NamesEveryAcceptedPreloaderProductId()
    {
        var result = MtkModeClassifier.Classify(Device(0x1234, CommandInterface(0)));

        Assert.Contains("(0x0003, 0x2000, 0x2001, 0x20FF, 0x3000)", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_BootRomProductIdRemainsUniqueBootRomCaseWithoutMetadata()
    {
        var device = new UsbDeviceInfo { VendorId = 0x0E8D, ProductId = 0x0003 };

        var result = MtkModeClassifier.Classify(device);

        Assert.Equal(MtkUsbMode.BootRom, result.Mode);
        Assert.Contains("unique to the Boot ROM stage", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x9DDE)]
    [InlineData(0xADE3)]
    [InlineData(0xADE5)]
    [InlineData(0xADED)]
    public void Classify_SonyEdlProductIdUnderMtkModeClassifier_ReturnsUnknownAndNamesDocumentedOemPorts(
        ushort productId)
    {
        var device = new UsbDeviceInfo
        {
            VendorId = 0x0FCE,
            ProductId = productId,
            InterfaceMetadataObserved = false
        };

        var result = MtkModeClassifier.Classify(device);

        Assert.Equal(MtkUsbMode.Unknown, result.Mode);
        Assert.Contains("not a documented OEM download port", result.Reason, StringComparison.Ordinal);
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