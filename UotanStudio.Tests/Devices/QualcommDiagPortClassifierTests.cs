using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Discovery;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Pins the Qualcomm diagnostic port classification apart from the download ports.
/// <para>固定高通诊断端口的分类，使其与下载端口区分开。</para>
/// </summary>
public class QualcommDiagPortClassifierTests
{
    [Theory]
    [InlineData(0x9008)]
    [InlineData(0x9006)]
    [InlineData(0x900E)]
    [InlineData(0x9025)]
    public void Classify_QualcommDownloadPort_ReturnsEdl(ushort productId)
        => Assert.Equal("edl", UsbProtocolClassifier.Classify(0x05C6, productId, null, null, null));

    [Fact]
    public void Classify_QualcommPid901D_ReturnsDiagNotEmptyEdl()
        => Assert.Equal("diag", UsbProtocolClassifier.Classify(0x05C6, 0x901D, null, null, null));

    [Theory]
    [InlineData(0x1199, 0x9071)]
    [InlineData(0x1199, 0x9091)]
    [InlineData(0x2C7C, 0x0125)]
    [InlineData(0x0846, 0x68E2)]
    [InlineData(0x19D2, 0x0016)]
    public void Classify_ModemDiagnosticPort_ReturnsDiag(ushort vendorId, ushort productId)
        => Assert.Equal("diag", UsbProtocolClassifier.Classify(vendorId, productId, null, null, null));

    [Theory]
    [InlineData(0x1199, 0x9062)]
    [InlineData(0x1199, 0x9070)]
    [InlineData(0x1199, 0x9090)]
    public void Classify_SierraEdlPort_ReturnsEdl(ushort vendorId, ushort productId)
        => Assert.Equal("edl", UsbProtocolClassifier.Classify(vendorId, productId, null, null, null));

    [Fact]
    public void Classify_WithNoPid_DoesNotClaimDiag()
        => Assert.Null(UsbProtocolClassifier.Classify(0x05C6, null, null, null, null));

    [Fact]
    public void DeviceSessionKind_Diagnostic_IsDistinctFromEdl()
        => Assert.NotEqual(DeviceSessionKind.Edl, DeviceSessionKind.Diagnostic);
}