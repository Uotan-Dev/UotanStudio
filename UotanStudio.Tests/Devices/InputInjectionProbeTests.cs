using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class InputInjectionProbeTests
{
    [Fact]
    public void Parse_GrantedPermission_ReportsInjectionAvailable()
    {
        const string output =
            "  Package [com.android.shell] (1a2b3c4d):\n" +
            "    requested permissions:\n" +
            "      android.permission.INJECT_EVENTS\n" +
            "    install permissions:\n" +
            "      android.permission.INJECT_EVENTS: granted=true";

        Assert.True(InputInjectionProbe.Parse(output));
    }

    [Fact]
    public void Parse_DeniedPermission_ReportsInjectionBlocked()
    {
        const string output =
            "  Package [com.android.shell] (1a2b3c4d):\n" +
            "    install permissions:\n" +
            "      android.permission.INJECT_EVENTS: granted=false";

        Assert.False(InputInjectionProbe.Parse(output));
    }

    [Fact]
    public void Parse_RequestedButNotGranted_ReportsInjectionBlocked()
    {
        const string output =
            "  Package [com.android.shell] (1a2b3c4d):\n" +
            "    requested permissions:\n" +
            "      android.permission.INJECT_EVENTS";

        Assert.False(InputInjectionProbe.Parse(output));
    }

    [Fact]
    public void Parse_OtherPermissionGranted_DoesNotCountAsInjectionGrant()
    {
        const string output =
            "  Package [com.android.shell] (1a2b3c4d):\n" +
            "    install permissions:\n" +
            "      android.permission.DUMP: granted=true";

        Assert.False(InputInjectionProbe.Parse(output));
    }

    [Fact]
    public void Parse_GrantWithoutPackageHeader_ReportsUnknown()
    {
        const string output = "      android.permission.INJECT_EVENTS: granted=true";

        Assert.Null(InputInjectionProbe.Parse(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public void Parse_EmptyOutput_ReportsUnknown(string output)
    {
        Assert.Null(InputInjectionProbe.Parse(output));
    }
}