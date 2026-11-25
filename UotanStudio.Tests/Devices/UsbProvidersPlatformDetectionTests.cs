using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;
using UotanStudio.Comm.Transports.Usb.Providers;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class UsbProvidersPlatformDetectionTests
{
    [Fact]
    public void AndroidUsbApiProvider_ApiName_IsAndroid()
    {
        Assert.Equal("android", AndroidUsbApiProvider.ApiNameConst);
    }

    [Fact]
    public void HarmonyUsbApiProvider_ApiName_IsHarmony()
    {
        Assert.Equal("harmony", HarmonyUsbApiProvider.ApiNameConst);
    }

    [Fact]
    public void AndroidUsbApiProvider_IsSupportedOnCurrentPlatform_MatchesDetection()
    {
        Assert.Equal(PlatformDetection.IsAndroidOs, new AndroidUsbApiProvider().IsSupportedOnCurrentPlatform);
    }

    [Fact]
    public void AndroidUsbApiProvider_OnNonAndroid_ReportsUnsupported()
    {
        if (PlatformDetection.IsAndroidOs)
        {
            return;
        }

        Assert.False(new AndroidUsbApiProvider().IsSupportedOnCurrentPlatform);
    }

    [Fact]
    public void HarmonyUsbApiProvider_IsSupportedOnCurrentPlatform_MatchesDetection()
    {
        Assert.Equal(PlatformDetection.IsHarmonyOs, new HarmonyUsbApiProvider().IsSupportedOnCurrentPlatform);
    }

    [Fact]
    public void UsbApiRegistry_CreateDefault_ContainsAndroidAndHarmonyProviders()
    {
        var registry = UsbApiRegistry.CreateDefault();
        Assert.Contains(AndroidUsbApiProvider.ApiNameConst, registry.GetApiNames(), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(HarmonyUsbApiProvider.ApiNameConst, registry.GetApiNames(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AndroidUsbApiProvider_GetCapabilities_ReportsLibUsbFamily()
    {
        var capabilities = new AndroidUsbApiProvider().GetCapabilities();
        Assert.Equal(UsbApiKind.LibUsbDotNet, capabilities.ApiKind);
        Assert.True(capabilities.SupportsNativeDiscovery);
        Assert.True(capabilities.SupportsDeviceSessions);
        Assert.True(capabilities.SupportsControlTransfers);
    }

    [Fact]
    public void HarmonyUsbApiProvider_GetCapabilities_ReportsLibUsbFamily()
    {
        var capabilities = new HarmonyUsbApiProvider().GetCapabilities();
        Assert.Equal(UsbApiKind.LibUsbDotNet, capabilities.ApiKind);
        Assert.True(capabilities.SupportsNativeDiscovery);
        Assert.True(capabilities.SupportsDeviceSessions);
        Assert.False(capabilities.SupportsNativeHotPlugMonitoring);
    }
}