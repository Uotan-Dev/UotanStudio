using UotanStudio.Comm.Protocols.Adb;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Checks how verbose adb target lines are classified into a link class.
/// <para>校验详细 adb 目标行如何归类为链路类别。</para>
/// </summary>
public class AdbTargetInfoTests
{
    [Theory]
    [InlineData("R58M12345 device product:beyond model:SM_G9730 device:beyond transport_id:3", "USB")]
    [InlineData("R58M12345 device usb:1-3 product:beyond model:SM_G9730 device:beyond transport_id:3", "USB")]
    [InlineData("192.168.1.144:40443 device product:beyond model:SM_G9730 device:beyond transport_id:4", "TCP")]
    [InlineData("adb-6a082bda-lZgxIP._adb-tls-connect._tcp device product:beyond model:SM_G9730 device:beyond", "TCP")]
    [InlineData("emulator-5554 device product:sdk model:sdk device:generic transport_id:1", "TCP")]
    public void Parse_ListingLine_ClassifiesItsLinkClass(string line, string expected)
    {
        var target = Assert.Single(AdbTargetInfo.Parse(line));

        Assert.Equal(expected, target.Transport);
    }

    [Fact]
    public void Parse_LineWithoutStateField_SkipsIt()
        => Assert.Empty(AdbTargetInfo.Parse("R58M12345"));
}