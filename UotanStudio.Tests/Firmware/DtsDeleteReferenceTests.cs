using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// DTS /delete-node/ &amp;label and /delete-node/ &amp;{path} reference deletion tests.
/// <para>DTS 标签引用删除语义测试，覆盖 /delete-node/ &amp;label 与路径引用。</para>
/// </summary>
public class DtsDeleteReferenceTests
{
    // ---- Top-level /delete-node/ by name (existing behavior, must not regress) ----

    [Fact]
    public void TopLevel_DeleteNode_ByName_RemovesChild()
    {
        var dts = """
            /dts-v1/;
            / {
                keep { };
                drop { };
            };
            /delete-node/ drop;
            """;
        var result = DtsFormat.Parse(dts);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.NotNull(result.Tree!.Root.FindChild("keep"));
        Assert.Null(result.Tree.Root.FindChild("drop"));
    }

    // ---- /delete-node/ &label; by top-level reference ----

    [Fact]
    public void TopLevel_DeleteNode_ByLabel_RemovesNode()
    {
        var dts = """
            /dts-v1/;
            / {
                target: node-to-delete { };
                keep-me { };
            };
            /delete-node/ &target;
            """;
        var result = DtsFormat.Parse(dts);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Null(result.Tree!.Root.FindChild("node-to-delete"));
        Assert.NotNull(result.Tree.Root.FindChild("keep-me"));
    }

    [Fact]
    public void TopLevel_DeleteNode_UnknownLabel_ProducesWarning()
    {
        var dts = """
            /dts-v1/;
            / {
                keep { };
            };
            /delete-node/ &nonexistent;
            """;
        var result = DtsFormat.Parse(dts);
        // The warning is recorded but parsing still succeeds.
        Assert.Contains(result.Errors, e => e.Severity == DtsParseErrorSeverity.Warning);
    }

    // ---- In-body /delete-node/ &label; ----

    [Fact]
    public void InBody_DeleteNode_ByLabel_RemovesNode()
    {
        var dts = """
            /dts-v1/;
            / {
                child: inner { };
                outer {
                    /delete-node/ &child;
                };
            };
            """;
        var result = DtsFormat.Parse(dts);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        // The label references the 'inner' node which is a child of root.
        // After deletion, 'inner' should be gone.
        Assert.Null(result.Tree!.Root.FindChild("inner"));
    }

    // ---- /delete-node/ &label; in root body ----

    [Fact]
    public void RootBody_DeleteNode_ByLabel_RemovesNode()
    {
        var dts = """
            /dts-v1/;
            / {
                victim: gone { };
                survivor { };
                /delete-node/ &victim;
            };
            """;
        var result = DtsFormat.Parse(dts);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Null(result.Tree!.Root.FindChild("gone"));
        Assert.NotNull(result.Tree.Root.FindChild("survivor"));
    }

    // ---- /delete-property/ &label; is rejected (not a valid dtc directive) ----

    [Fact]
    public void DeleteProperty_ByLabel_IsRejected()
    {
        var dts = """
            /dts-v1/;
            / {
                target: prop = "x";
                /delete-property/ &target;
            };
            """;
        var result = DtsFormat.Parse(dts);
        // This should produce an error, not silently skip.
        Assert.False(result.IsSuccess);
    }

    // ---- Delete after reference: phandle resolution still works for surviving nodes ----

    [Fact]
    public void DeleteNode_DoesNotBreakPhandleResolution()
    {
        var dts = """
            /dts-v1/;
            / {
                keep: kept-node {
                    compatible = "test";
                };
                drop: dropped-node { };
                consumer {
                    ref = <&keep>;
                };
            };
            /delete-node/ &drop;
            """;
        var result = DtsFormat.Parse(dts);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));

        // The surviving 'kept-node' should have a phandle assigned.
        var kept = result.Tree!.Root.FindChild("kept-node");
        Assert.NotNull(kept);
        var phandleProp = kept!.FindProperty("phandle");
        Assert.NotNull(phandleProp);

        // The consumer's reference should resolve to the kept node's phandle.
        var consumer = result.Tree.Root.FindChild("consumer");
        Assert.NotNull(consumer);
        var refCells = consumer!.FindProperty("ref")!.GetCells();
        Assert.Single(refCells);
        Assert.Equal(phandleProp!.GetCells()[0], refCells[0]);

        // The dropped node should be gone.
        Assert.Null(result.Tree.Root.FindChild("dropped-node"));
    }

    // ---- Delete order: label defined before /delete-node/ ----

    [Fact]
    public void DeleteNode_LabelDefinedBefore_DeleteSucceeds()
    {
        var dts = """
            /dts-v1/;
            / {
                label1: first { };
                label2: second { };
            };
            /delete-node/ &label1;
            /delete-node/ &label2;
            """;
        var result = DtsFormat.Parse(dts);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Null(result.Tree!.Root.FindChild("first"));
        Assert.Null(result.Tree.Root.FindChild("second"));
    }
}
