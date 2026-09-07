using System.Text;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Extended DTS coverage: property value types (cells/bytes/strings/escapes), labels and
/// phandle references, comments, /memreserve/, deep hierarchies, and /delete-node/ /
/// /delete-property/ directives inside node bodies.
/// <para>DTS 扩展覆盖：属性值类型（cells/字节串/字符串/转义）、label 与 phandle 引用、
/// 注释、/memreserve/、深层层级，以及节点体内的 /delete-node/ 与 /delete-property/ 指令。</para>
/// </summary>
public class DtsCoverageTests
{
    private static DeviceTree ParseOk(string dts)
    {
        var result = DtsFormat.Parse(dts);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.NotNull(result.Tree);
        return result.Tree!;
    }

    [Fact]
    public void PropertyValues_CellsBytesStrings_Preserved()
    {
        var tree = ParseOk("""
            /dts-v1/;
            / {
                cells-prop = <0x1 0x2 0x80000000>;
                bytes-prop = [de ad be ef];
                string-prop = "hello";
                escaped-prop = "quote:\" backslash:\\ end";
                string-list = "a", "b", "c";
                empty-prop;
                mixed = "str", <0x10>, [aa];
            };
            """);

        var root = tree.Root;
        Assert.Equal([0x1u, 0x2u, 0x80000000u], root.FindProperty("cells-prop")!.GetCells());
        Assert.Equal([0xde, 0xad, 0xbe, 0xef], root.FindProperty("bytes-prop")!.ValueBytes);
        Assert.Equal("hello", root.FindProperty("string-prop")!.GetString());
        Assert.Equal("quote:\" backslash:\\ end", root.FindProperty("escaped-prop")!.GetString());
        // 字符串列表按 DT 规范以 NUL 分隔存储；GetString 取首个 NUL 前的字符串。
        // String lists are NUL-separated per the DT spec; GetString reads up to the first NUL.
        Assert.Equal("a\0b\0c\0", Encoding.UTF8.GetString(root.FindProperty("string-list")!.ValueBytes));
        Assert.True(root.FindProperty("empty-prop")!.IsEmpty);
        Assert.NotEmpty(root.FindProperty("mixed")!.ValueBytes);

        // 回环稳定性：Write → Parse → Write 输出一致。
        // Round-trip stability: Write → Parse → Write produces identical text.
        var written = DtsFormat.Write(tree);
        var reparsed = ParseOk(written);
        Assert.Equal(written, DtsFormat.Write(reparsed));
    }

    [Fact]
    public void Labels_AndPhandleReferences_AreResolved()
    {
        var tree = ParseOk("""
            /dts-v1/;
            / {
                intc: interrupt-controller@1000 {
                    #interrupt-cells = <2>;
                };
                serial@2000 {
                    interrupt-parent = <&intc>;
                };
                path-ref {
                    target = &intc;
                };
            };
            """);

        var intc = tree.Root.FindChild("interrupt-controller@1000");
        Assert.NotNull(intc);

        // 自动为 intc 分配 phandle，interrupt-parent 单元格引用被 patch 为该 phandle。
        // A phandle is assigned for intc; the interrupt-parent cell is patched to it.
        var phandle = intc.FindProperty("phandle");
        Assert.NotNull(phandle);
        var phandleValue = phandle!.GetCells()[0];
        Assert.True(phandleValue > 0);

        var serial = tree.Root.FindChild("serial@2000");
        var parentCells = serial!.FindProperty("interrupt-parent")!.GetCells();
        Assert.Single(parentCells);
        Assert.Equal(phandleValue, parentCells[0]);

        // 路径型引用（&label 作为属性值）解析为目标的完整路径字符串。
        // Path-style references (&label as a value) resolve to the target's full path.
        var pathRef = tree.Root.FindChild("path-ref")!;
        Assert.Equal("/interrupt-controller@1000\0", Encoding.UTF8.GetString(pathRef.FindProperty("target")!.ValueBytes));
    }

    [Fact]
    public void Comments_AreIgnored()
    {
        var tree = ParseOk("""
            /dts-v1/; // line comment
            /* block
               comment */
            / {
                prop = <0x1>; /* inline */ // trailing
                /* leading */ second = "x";
            };
            """);

        Assert.Equal([0x1u], tree.Root.FindProperty("prop")!.GetCells());
        Assert.Equal("x", tree.Root.FindProperty("second")!.GetString());
    }

    [Fact]
    public void MemReserve_ParsesAndWritesBack()
    {
        var tree = ParseOk("""
            /dts-v1/;
            /memreserve/ 0x1000 0x2000;
            /memreserve/ 0x30000000 0x4000;
            / { prop; };
            """);

        Assert.Equal(2, tree.MemoryReservations.Count);
        Assert.Equal(0x1000ul, tree.MemoryReservations[0].Address);
        Assert.Equal(0x2000ul, tree.MemoryReservations[0].Size);
        Assert.Equal(0x30000000ul, tree.MemoryReservations[1].Address);

        var written = DtsFormat.Write(tree);
        Assert.Contains("/memreserve/", written);
        Assert.Contains("0x30000000", written);
        Assert.Equal(2, ParseOk(written).MemoryReservations.Count);
    }

    [Fact]
    public void DeepHierarchy_AndUnitAddresses_RoundTrip()
    {
        var tree = ParseOk("""
            /dts-v1/;
            / {
                model = "deep";
                soc@0 {
                    #address-cells = <1>;
                    bus@1000 {
                        i2c@2000 {
                            eeprom@50 {
                                compatible = "at,24c02";
                                reg = <0x50>;
                            };
                        };
                    };
                };
            };
            """);

        var eeprom = tree.Root.FindChild("soc@0")!.FindChild("bus@1000")!
            .FindChild("i2c@2000")!.FindChild("eeprom@50")!;
        Assert.Equal("at,24c02", eeprom.FindProperty("compatible")!.GetString());

        var written = DtsFormat.Write(tree);
        Assert.Contains("soc@0", written);
        Assert.Contains("eeprom@50", written);
        var reparsed = ParseOk(written);
        Assert.Equal("at,24c02", reparsed.Root.FindChild("soc@0")!.FindChild("bus@1000")!
            .FindChild("i2c@2000")!.FindChild("eeprom@50")!.FindProperty("compatible")!.GetString());
    }

    [Fact]
    public void DeleteNodeDirective_RemovesNode()
    {
        // /delete-node/ 与 /delete-property/ 在节点体内生效,不再产生告警。
        // /delete-node/ and /delete-property/ take effect inside a node body without warnings.
        var result = DtsFormat.Parse("""
            /dts-v1/;
            / {
                keep-me { };
                drop-me { };
                /delete-node/ drop-me;
                keep-prop;
                drop-prop = "bye";
                /delete-property/ drop-prop;
            };
            """);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.NotNull(result.Tree);
        Assert.Null(result.Tree!.Root.FindChild("drop-me"));
        Assert.NotNull(result.Tree.Root.FindChild("keep-me"));
        Assert.Null(result.Tree.Root.FindProperty("drop-prop"));
        Assert.NotNull(result.Tree.Root.FindProperty("keep-prop"));
        Assert.DoesNotContain(result.Errors, e => e.Severity == DtsParseErrorSeverity.Warning);
    }
}