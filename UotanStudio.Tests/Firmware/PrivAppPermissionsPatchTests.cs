using UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class PrivAppPermissionsPatchTests
{
    private const string Whitelist = """
        <?xml version="1.0" encoding="utf-8"?>
        <permissions>
            <!-- Global whitelist for test devices -->
            <privapp-permissions package="com.uotan.app">
                <permission name="android.permission.READ_LOGS"/>
            </privapp-permissions>
            <privapp-permissions package="com.other.app">
                <permission name="android.permission.PACKAGE_USAGE_STATS"/>
            </privapp-permissions>
        </permissions>
        """;

    private const string Override = """
        <?xml version="1.0" encoding="utf-8"?>
        <permissions>
            <allow name="android.permission.CAMERA" package="com.uotan.app"/>
            <allow name="android.permission.RECORD_AUDIO" package="com.uotan.app"/>
        </permissions>
        """;

    private const string FlatPermission = """
        <permissions>
            <exception package="com.exempt.app"/>
            <permission name="android.permission.ACCESS_CACHE_FILESYSTEM" package="com.uotan.app"/>
        </permissions>
        """;

    [Fact]
    public void Parse_WhitelistDocument_ReadsNestedEntries()
    {
        var entries = PrivAppPermissionsPatch.Parse(Whitelist);
        Assert.Equal(2, entries.Count);
        Assert.Equal("com.uotan.app", entries[0].Package);
        Assert.Equal("android.permission.READ_LOGS", entries[0].Permission);
        Assert.Equal(PrivAppPermissionShape.ContainerChild, entries[0].Shape);
        Assert.Equal("com.other.app", entries[1].Package);
    }

    [Fact]
    public void Parse_OverrideDocument_ReadsFlatAllowEntries()
    {
        var entries = PrivAppPermissionsPatch.Parse(Override);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(PrivAppPermissionShape.FlatAllow, entry.Shape));
        Assert.Contains(entries, entry => entry.Permission == "android.permission.RECORD_AUDIO");
    }

    [Fact]
    public void Parse_FlatPermissionDocument_IgnoresUnrelatedExceptionElement()
    {
        var entries = PrivAppPermissionsPatch.Parse(FlatPermission);
        Assert.Single(entries);
        Assert.Equal(PrivAppPermissionShape.FlatPermission, entries[0].Shape);
    }

    [Fact]
    public void Add_WhitelistDocument_AppendsToExistingContainerAndPreservesComments()
    {
        var result = PrivAppPermissionsPatch.Add(Whitelist, "com.uotan.app", "android.permission.DUMP");
        Assert.True(result.Changed);
        Assert.Contains("<!-- Global whitelist for test devices -->", result.Text, StringComparison.Ordinal);
        Assert.Contains("com.other.app", result.Text, StringComparison.Ordinal);
        var entries = PrivAppPermissionsPatch.Parse(result.Text);
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, entry => entry.Permission == "android.permission.DUMP"
            && entry.Package == "com.uotan.app" && entry.Shape == PrivAppPermissionShape.ContainerChild);
    }

    [Fact]
    public void Add_AppliedTwice_EqualsAppliedOnce()
    {
        var first = PrivAppPermissionsPatch.Add(Whitelist, "com.uotan.app", "android.permission.DUMP");
        var second = PrivAppPermissionsPatch.Add(first.Text, "com.uotan.app", "android.permission.DUMP");
        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(3, PrivAppPermissionsPatch.Parse(second.Text).Count);
    }

    [Fact]
    public void Add_ExistingEntry_ReturnsUntouchedInput()
    {
        var result = PrivAppPermissionsPatch.Add(Whitelist, "com.uotan.app", "android.permission.READ_LOGS");
        Assert.False(result.Changed);
        Assert.Equal(Whitelist, result.Text);
    }

    [Fact]
    public void Add_EmptyDocument_CreatesNestedContainer()
    {
        var result = PrivAppPermissionsPatch.Add("<permissions/>", "com.uotan.app", "android.permission.DUMP");
        Assert.True(result.Changed);
        Assert.Single(PrivAppPermissionsPatch.Parse(result.Text));
        Assert.Contains("privapp-permissions", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_OverrideDocument_KeepsFlatAllowShape()
    {
        var result = PrivAppPermissionsPatch.Add(Override, "com.uotan.app", "android.permission.DUMP");
        Assert.True(result.Changed);
        var entries = PrivAppPermissionsPatch.Parse(result.Text);
        Assert.Equal(3, entries.Count);
        Assert.All(entries, entry => Assert.Equal(PrivAppPermissionShape.FlatAllow, entry.Shape));
    }

    [Fact]
    public void Add_FlatPermissionDocument_KeepsFlatPermissionShape()
    {
        var result = PrivAppPermissionsPatch.Add(FlatPermission, "com.uotan.app", "android.permission.REBOOT");
        Assert.True(result.Changed);
        Assert.Contains("com.exempt.app", result.Text, StringComparison.Ordinal);
        Assert.Contains("<exception", result.Text, StringComparison.Ordinal);
        var entries = PrivAppPermissionsPatch.Parse(result.Text);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(PrivAppPermissionShape.FlatPermission, entry.Shape));
    }

    [Fact]
    public void Add_CrlfDocument_PreservesNewlineStyleAndDeclaration()
    {
        var text = Whitelist.ReplaceLineEndings("\r\n");
        var result = PrivAppPermissionsPatch.Add(text, "com.uotan.app", "android.permission.DUMP");
        Assert.True(result.Changed);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\r", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_WhitelistDocument_DropsPermissionAndEmptyContainer()
    {
        var result = PrivAppPermissionsPatch.Remove(Whitelist, "com.other.app", "android.permission.PACKAGE_USAGE_STATS");
        Assert.True(result.Changed);
        Assert.Equal(1, result.Matches);
        Assert.DoesNotContain("com.other.app", result.Text, StringComparison.Ordinal);
        Assert.Contains("<!-- Global whitelist for test devices -->", result.Text, StringComparison.Ordinal);
        Assert.Single(PrivAppPermissionsPatch.Parse(result.Text));
    }

    [Fact]
    public void Remove_KeepsSiblingPermissionsOfSameContainer()
    {
        var withTwo = PrivAppPermissionsPatch.Add(Whitelist, "com.uotan.app", "android.permission.DUMP").Text;
        var result = PrivAppPermissionsPatch.Remove(withTwo, "com.uotan.app", "android.permission.DUMP");
        Assert.True(result.Changed);
        Assert.Contains("privapp-permissions", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("android.permission.DUMP", result.Text, StringComparison.Ordinal);
        Assert.Contains("android.permission.READ_LOGS", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_FlatAllowDocument_DropsMatchingOverrideOnly()
    {
        var result = PrivAppPermissionsPatch.Remove(Override, "com.uotan.app", "android.permission.CAMERA");
        Assert.True(result.Changed);
        var entries = PrivAppPermissionsPatch.Parse(result.Text);
        Assert.Single(entries);
        Assert.Equal("android.permission.RECORD_AUDIO", entries[0].Permission);
    }

    [Fact]
    public void Remove_AppliedTwice_EqualsAppliedOnce()
    {
        var first = PrivAppPermissionsPatch.Remove(Override, "com.uotan.app", "android.permission.CAMERA");
        var second = PrivAppPermissionsPatch.Remove(first.Text, "com.uotan.app", "android.permission.CAMERA");
        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Equal(first.Text, second.Text);
    }

    [Fact]
    public void Remove_AbsentEntry_ReturnsUntouchedInput()
    {
        var result = PrivAppPermissionsPatch.Remove(Whitelist, "com.uotan.app", "android.permission.MISSING");
        Assert.False(result.Changed);
        Assert.Equal(0, result.Matches);
        Assert.Equal(Whitelist, result.Text);
    }

    [Fact]
    public void Parse_WrongRootElement_Rejects()
    {
        Assert.Throws<InvalidDataException>(() => PrivAppPermissionsPatch.Parse("<config><permission name=\"a\" package=\"b\"/></config>"));
    }

    [Fact]
    public void Parse_MissingRequiredAttribute_Rejects()
    {
        Assert.Throws<InvalidDataException>(() => PrivAppPermissionsPatch.Parse("<permissions><permission package=\"com.uotan.app\"/></permissions>"));
        Assert.Throws<InvalidDataException>(() => PrivAppPermissionsPatch.Parse("<permissions><privapp-permissions><permission name=\"a\"/></privapp-permissions></permissions>"));
    }

    [Fact]
    public void Parse_BlankIdentifier_Rejects()
    {
        Assert.Throws<InvalidDataException>(() => PrivAppPermissionsPatch.Parse("<permissions><permission name=\" \" package=\"com.uotan.app\"/></permissions>"));
    }

    [Fact]
    public void Parse_DoctypeDeclaration_Rejects()
    {
        Assert.Throws<InvalidDataException>(() => PrivAppPermissionsPatch.Parse(
            "<!DOCTYPE permissions [<!ELEMENT permissions ANY>]><permissions/>"));
    }

    [Fact]
    public void Parse_NotXml_Rejects()
    {
        Assert.Throws<InvalidDataException>(() => PrivAppPermissionsPatch.Parse("permissions"));
    }

    [Fact]
    public void Add_ControlCharacterIdentifier_Rejects()
    {
        Assert.Throws<InvalidDataException>(() => PrivAppPermissionsPatch.Add(Whitelist, "com.uotan\u0007.app", "android.permission.DUMP"));
    }
}