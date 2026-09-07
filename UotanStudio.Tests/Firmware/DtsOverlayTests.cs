using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// DTS overlay 合并测试:/delete-node/ 与 /delete-property/ 生效、顶层 &amp;label overlay 片段
/// 合并,以及 DtsFormat.MergeOverlay 独立合并 API。
/// <para>DTS overlay merge tests: /delete-node/ and /delete-property/ take effect, top-level
/// &amp;label overlay fragments merge into the base tree, and the standalone
/// DtsFormat.MergeOverlay API merges two independently parsed trees.</para>
/// </summary>
public class DtsOverlayTests
{
    private static DeviceTree ParseOk(string dts)
    {
        var result = DtsFormat.Parse(dts);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.NotNull(result.Tree);
        return result.Tree!;
    }

    [Fact]
    public void DeleteDirectives_InsideNodeBody_RemoveChildrenAndProperties()
    {
        var tree = ParseOk("""
            /dts-v1/;
            / {
                keep { };
                drop-me@0 { };
                /delete-node/ drop-me@0;
                keep-prop;
                drop-prop;
                /delete-property/ drop-prop;
            };
            """);

        var root = tree.Root;
        Assert.NotNull(root.FindChild("keep"));
        Assert.Null(root.FindChild("drop-me@0"));
        Assert.NotNull(root.FindProperty("keep-prop"));
        Assert.Null(root.FindProperty("drop-prop"));
    }

    [Fact]
    public void TopLevelDeleteNode_RemovesChildFromRoot()
    {
        var tree = ParseOk("""
            /dts-v1/;
            /delete-node/ gone;
            /delete-property/ gone-prop;
            / {
                gone { };
                stay { };
                gone-prop;
            };
            """);

        Assert.Null(tree.Root.FindChild("gone"));
        Assert.NotNull(tree.Root.FindChild("stay"));
        Assert.Null(tree.Root.FindProperty("gone-prop"));
    }

    [Fact]
    public void OverlayFragment_MergesPropertiesAndChildren_AndOverridesProperties()
    {
        var tree = ParseOk("""
            /dts-v1/;
            &soc {
                new-prop = "from-overlay";
                old-prop = "overridden";
                extra {
                    value = <0x2a>;
                };
            };
            / {
                soc: soc {
                    old-prop = "original";
                    i2c@0 {
                        compatible = "base,i2c";
                    };
                };
            };
            """);

        var soc = tree.Root.FindChild("soc");
        Assert.NotNull(soc);
        Assert.Equal("overridden", soc!.FindProperty("old-prop")!.GetString());
        Assert.Equal("from-overlay", soc.FindProperty("new-prop")!.GetString());
        var extra = soc.FindChild("extra");
        Assert.NotNull(extra);
        Assert.Equal([0x2au], extra!.FindProperty("value")!.GetCells());
        var i2c = soc.FindChild("i2c@0");
        Assert.NotNull(i2c);
        Assert.Equal("base,i2c", i2c!.FindProperty("compatible")!.GetString());
    }

    [Fact]
    public void OverlayFragment_DeleteNode_RemovesFromBaseTree()
    {
        var tree = ParseOk("""
            /dts-v1/;
            &soc {
                /delete-node/ i2c@0;
                /delete-property/ old-prop;
                new-prop;
            };
            / {
                soc: soc {
                    old-prop = "x";
                    i2c@0 { };
                    keep-me { };
                };
            };
            """);

        var soc = tree.Root.FindChild("soc");
        Assert.NotNull(soc);
        Assert.Null(soc!.FindChild("i2c@0"));
        Assert.NotNull(soc.FindChild("keep-me"));
        Assert.Null(soc.FindProperty("old-prop"));
        Assert.NotNull(soc.FindProperty("new-prop"));
    }

    [Fact]
    public void OverlayAfterRoot_MergesIntoBaseTree()
    {
        // 顶层 &amp;label 片段也可以出现在根节点之后(常见 DTS overlay 布局)。
        // Top-level &amp;label fragments may also follow the root node (the common DTS overlay layout).
        var tree = ParseOk("""
            /dts-v1/;
            / {
                soc: soc {
                    prop = "base";
                };
            };
            &soc {
                prop = "overlay";
                added { };
            };
            """);

        var soc = tree.Root.FindChild("soc");
        Assert.NotNull(soc);
        Assert.Equal("overlay", soc!.FindProperty("prop")!.GetString());
        Assert.NotNull(soc.FindChild("added"));
    }

    [Fact]
    public void MergeOverlay_IndependentTrees_AreMergedByPath()
    {
        var baseTree = ParseOk("""
            /dts-v1/;
            / {
                soc {
                    prop = "base";
                    child { };
                };
            };
            """);
        var overlay = ParseOk("""
            /dts-v1/;
            / {
                soc {
                    prop = "overlay";
                    extra { };
                };
                new-root-node {
                    val = <0x1>;
                };
            };
            """);

        var merged = DtsFormat.MergeOverlay(baseTree, overlay);

        var soc = merged.Root.FindChild("soc");
        Assert.Equal("overlay", soc!.FindProperty("prop")!.GetString());
        Assert.NotNull(soc.FindChild("child"));
        Assert.NotNull(soc.FindChild("extra"));
        Assert.Equal([0x1u], merged.Root.FindChild("new-root-node")!.FindProperty("val")!.GetCells());
    }

    [Fact]
    public void OverlayParseWriteParse_RoundTrip_PreservesMergeResult()
    {
        var tree = ParseOk("""
            /dts-v1/;
            &soc {
                new-prop = <0x1 0x2>;
                /delete-property/ old-prop;
                /delete-node/ i2c@0;
                extra { value = "x"; };
            };
            / {
                soc: soc {
                    old-prop;
                    i2c@0 { };
                };
            };
            """);

        var written = DtsFormat.Write(tree);
        var reparsed = ParseOk(written);

        var soc = reparsed.Root.FindChild("soc");
        Assert.NotNull(soc);
        Assert.Null(soc!.FindProperty("old-prop"));
        Assert.Equal([0x1u, 0x2u], soc.FindProperty("new-prop")!.GetCells());
        Assert.NotNull(soc.FindChild("extra"));
        Assert.Null(soc.FindChild("i2c@0"));
    }
}