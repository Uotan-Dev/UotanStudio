using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Devices.Services.Sessions;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class TegraDiscoveryTests
{
    [Theory]
    [InlineData(0x7820)]
    [InlineData(0x7330)]
    [InlineData(0x7135)]
    [InlineData(0x7140)]
    public void Classify_NvidiaRcmPort_ReturnsTegra(ushort productId)
        => Assert.Equal("tegra", UsbProtocolClassifier.Classify(0x0955, productId, null, null, null));

    [Fact]
    public void Classify_PiscesFastbootPort_IsNotClassifiedAsTegra()
    {
        Assert.Null(UsbProtocolClassifier.Classify(0x18D1, 0xD00D, null, null, null));
        Assert.Null(UsbProtocolClassifier.Classify(0x0955, 0xD00D, null, null, null));
    }

    [Fact]
    public void Classify_NvidiaVendorWithUnknownProduct_IsNotClassifiedAsTegra()
        => Assert.Null(UsbProtocolClassifier.Classify(0x0955, 0x7011, null, null, null));

    [Fact]
    public void Classify_ExistingProtocols_AreNotDisturbedByTheTegraBranch()
    {
        Assert.Equal("sunxi", UsbProtocolClassifier.Classify(0x1F3A, 0xEFE8, null, null, null));
        Assert.Equal("edl", UsbProtocolClassifier.Classify(0x05C6, 0x9008, null, null, null));
        Assert.Equal("fastboot", UsbProtocolClassifier.Classify(0x18D1, 0xD00D, 0xFF, 0x42, 0x03));
    }

    [Fact]
    public void ExpectedEndpointProtocol_Tegra_MatchesDiscoveryText()
        => Assert.Equal("tegra", DeviceMonitorService.ExpectedEndpointProtocol(DeviceProtocolKind.Tegra));

    [Fact]
    public void DefaultSessionFactories_RegisterTheTegraFactory()
        => Assert.Contains(DefaultDeviceSessionFactories.All, factory => factory.Kind == DeviceSessionKind.Tegra);

    [Fact]
    public void DeviceSessionKind_DeclaresTegraAfterUnisoc()
    {
        Assert.True(Enum.IsDefined(DeviceSessionKind.Tegra));
        Assert.Equal(nameof(DeviceSessionKind.Tegra), DeviceSessionKind.Tegra.ToString());
    }
}