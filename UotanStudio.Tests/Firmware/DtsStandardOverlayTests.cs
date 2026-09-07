using UotanStudio.Firmware.Dt;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks standard DT overlay targets and fixups. <para>检查标准 DT overlay 目标和修正记录。</para></summary>
public class DtsStandardOverlayTests
{
    [Fact]
    public void PlainMerge_PreservesAndOverridesRootProperties()
    {
        var tree = Parse("/dts-v1/; / { compatible = \"old\"; keep; };");
        var overlay = Parse("/dts-v1/; / { compatible = \"new\"; added; };");
        DtsFormat.MergeOverlay(tree, overlay);
        Assert.Equal("new", tree.Root.FindProperty("compatible")!.GetString());
        Assert.NotNull(tree.Root.FindProperty("keep"));
        Assert.NotNull(tree.Root.FindProperty("added"));
        Assert.NotNull(overlay.Root.FindProperty("added"));
    }

    [Fact]
    public void StandardFixups_RelocateReferencesAndExportSymbols()
    {
        var tree = new DeviceTree();
        var soc = tree.Root.AddChild("soc"); soc.AddPropertyCells("phandle", 10);
        tree.Root.AddChild("__symbols__").AddPropertyString("soc", "/soc\0");
        var overlay = new DeviceTree();
        var fragment = overlay.Root.AddChild("fragment@0"); fragment.AddPropertyCells("target", uint.MaxValue);
        var contents = fragment.AddChild("__overlay__");
        var child = contents.AddChild("device"); child.AddPropertyCells("phandle", 1);
        contents.AddChild("consumer").AddPropertyCells("link", 1);
        overlay.Root.AddChild("__fixups__").AddPropertyString("soc", "/fragment@0:target:0\0");
        overlay.Root.AddChild("__local_fixups__").AddChild("fragment@0").AddChild("__overlay__")
            .AddChild("consumer").AddPropertyCells("link", 0);
        overlay.Root.AddChild("__symbols__").AddPropertyString("device", "/fragment@0/__overlay__/device\0");

        DtsFormat.MergeOverlay(tree, overlay);

        Assert.Equal(11u, tree.FindNode("/soc/device")!.FindProperty("phandle")!.GetCells()[0]);
        Assert.Equal(11u, tree.FindNode("/soc/consumer")!.FindProperty("link")!.GetCells()[0]);
        Assert.Equal("/soc/device", tree.FindNode("/__symbols__")!.FindProperty("device")!.GetString());
        Assert.Null(tree.FindNode("/fragment@0"));
        Assert.Equal(1u, overlay.FindNode("/fragment@0/__overlay__/device")!.FindProperty("phandle")!.GetCells()[0]);
    }

    [Fact]
    public void ExistingNodeHandle_IsRetainedAndLocalReferencesFollowIt()
    {
        var tree = Parse("/dts-v1/; / { soc { device { phandle = <10>; }; }; };");
        var overlay = Parse("""
            /dts-v1/; /plugin/;
            / { fragment@0 { target-path = "/soc";
                __overlay__ { local: device { phandle = <1>; }; consumer { link = <&local>; }; };
            }; };
            """);
        DtsFormat.MergeOverlay(tree, overlay);
        Assert.Equal(10u, tree.FindNode("/soc/device")!.FindProperty("phandle")!.GetCells()[0]);
        Assert.Equal(10u, tree.FindNode("/soc/consumer")!.FindProperty("link")!.GetCells()[0]);
    }

    [Fact]
    public void PluginLabelFragment_ResolvesAgainstIndependentlyParsedBase()
    {
        var tree = Parse("/dts-v1/; / { soc: soc { }; };");
        var overlay = Parse("/dts-v1/; /plugin/; &soc { status = \"okay\"; };");
        Assert.Equal("/fragment@0:target:0", overlay.FindNode("/__fixups__")!.FindProperty("soc")!.GetString());
        DtsFormat.MergeOverlay(tree, overlay);
        Assert.Equal("okay", tree.FindNode("/soc")!.FindProperty("status")!.GetString());
    }

    [Fact]
    public void PluginPathFragment_ResolvesWithoutSymbols()
    {
        var tree = Parse("/dts-v1/; / { soc { }; };");
        var overlay = Parse("/dts-v1/; /plugin/; &{/soc} { enabled; };");
        DtsFormat.MergeOverlay(tree, overlay);
        Assert.NotNull(tree.FindNode("/soc")!.FindProperty("enabled"));
    }

    [Fact]
    public void MissingTarget_DoesNotPartiallyMutateBase()
    {
        var tree = Parse("/dts-v1/; / { original; };");
        var overlay = Parse("""
            /dts-v1/; /plugin/; / {
                fragment@0 { target-path = "/"; __overlay__ { added; }; };
                fragment@1 { target-path = "/missing"; __overlay__ { failed; }; };
            };
            """);
        Assert.Throws<InvalidDataException>(() => DtsFormat.MergeOverlay(tree, overlay));
        Assert.Null(tree.Root.FindProperty("added"));
        Assert.NotNull(tree.Root.FindProperty("original"));
    }

    private static DeviceTree Parse(string text)
    {
        var result = DtsFormat.Parse(text);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        return result.Tree!;
    }
}