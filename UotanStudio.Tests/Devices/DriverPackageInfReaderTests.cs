using System.Text;
using UotanStudio.Comm.Transports.Usb.Drivers;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Verifies the INF fact reader behind staged package attribution: provider and catalog names resolve
/// through the <c>[Strings]</c> section, and the hardware ids come only from the model sections the
/// <c>[Manufacturer]</c> entries reference, platform-decorated variants included.
/// <para>验证已暂存包归属所依赖的 INF 事实读取器：提供者与目录名经 <c>[Strings]</c> 段解析，硬件 ID 只来自
/// <c>[Manufacturer]</c> 条目引用的模型段（含平台装饰变体）。</para>
/// </summary>
public sealed class DriverPackageInfReaderTests
{
    [Fact]
    public void TryRead_ResolvesProviderCatalogAndReferencedHardwareIds()
    {
        var inf = """
            [Version]
            Signature = "$Windows NT$"
            Provider = %Provider%
            CatalogFile = mtk.cat

            [Manufacturer]
            %Manufacturer% = Models, NTx86, NTamd64, NTarm64

            [Models]
            %Port% = Port_Install, USB\VID_0E8D&PID_2000

            [Models.NTamd64]
            %Preloader% = Preloader_Install, "USB\VID_0E8D&PID_2001"
            %Da% = Da_Install, USB\VID_0E8D&PID_2002, USB\Class_ff

            [UnrelatedModels]
            %Other% = Other_Install, USB\VID_1111&PID_2222

            [Port_Install]
            AddService = WdfSer

            [Strings]
            Provider = "MediaTek Inc."
            Manufacturer = "MediaTek"
            Port = "MediaTek USB Port"
            Preloader = "MediaTek PreLoader USB VCOM"
            Da = "MediaTek DA USB VCOM"
            Other = "Other Device"
            """;

        Assert.True(DriverPackageInfReader.TryRead(Encoding.UTF8.GetBytes(inf), out var metadata));
        Assert.NotNull(metadata);
        Assert.Equal("MediaTek Inc.", metadata!.Provider);
        Assert.Equal(["mtk.cat"], metadata.CatalogFileNames);
        Assert.Contains("USB\\VID_0E8D&PID_2000", metadata.HardwareIds);
        Assert.Contains("USB\\VID_0E8D&PID_2001", metadata.HardwareIds);
        Assert.Contains("USB\\VID_0E8D&PID_2002", metadata.HardwareIds);
        Assert.Contains("USB\\Class_ff", metadata.HardwareIds);
        Assert.DoesNotContain("USB\\VID_1111&PID_2222", metadata.HardwareIds);
    }

    [Fact]
    public void TryRead_ResolvesManufacturerSectionNameThroughStrings()
    {
        var inf = """
            [Version]
            Provider = Acme

            [Manufacturer]
            %Manufacturer% = %Models%

            [MtkModels.NTamd64]
            %Device% = Install, USB\VID_2207&PID_350B

            [Strings]
            Manufacturer = "Acme"
            Models = "MtkModels"
            """;

        Assert.True(DriverPackageInfReader.TryRead(Encoding.UTF8.GetBytes(inf), out var metadata));
        Assert.NotNull(metadata);
        Assert.Equal("Acme", metadata!.Provider);
        Assert.Equal(["USB\\VID_2207&PID_350B"], metadata.HardwareIds);
    }

    [Fact]
    public void TryRead_WithoutManufacturerOrHardwareIds_ReturnsEmptyHardwareIds()
    {
        var inf = """
            [Version]
            Provider = Acme
            """;

        Assert.True(DriverPackageInfReader.TryRead(Encoding.UTF8.GetBytes(inf), out var metadata));
        Assert.NotNull(metadata);
        Assert.Empty(metadata!.HardwareIds);
    }

    [Fact]
    public void TryRead_WithoutProvider_ReturnsFalse()
    {
        var inf = """
            [Version]
            Signature = "$Windows NT$"

            [Manufacturer]
            %Manufacturer% = Models
            """;

        Assert.False(DriverPackageInfReader.TryRead(Encoding.UTF8.GetBytes(inf), out var metadata));
        Assert.Null(metadata);
    }
}