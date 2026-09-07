using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// DTS integer expression conformance tests, verified against dtc v1.7.2 precedence.
/// <para>DTS 整数表达式一致性测试，按 dtc v1.7.2 优先级验证。</para>
/// </summary>
public class DtsExpressionConformanceTests
{
    private static uint ParseSingleCell(string expr)
    {
        var dts = $"/dts-v1/;\n/ {{ test-prop = <{expr}>; }};\n";
        var result = DtsFormat.Parse(dts);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.NotNull(result.Tree);
        var prop = result.Tree!.Root.FindProperty("test-prop");
        Assert.NotNull(prop);
        var cells = prop!.GetCells();
        Assert.True(cells.Length == 1, $"Expected single cell but got {cells.Length} for expression '{expr}'");
        return cells[0];
    }

    // ---- Precedence ----

    [Fact]
    public void Multiplicative_BindsTighterThanAdditive()
    {
        // 1 + 2 * 3 = 7 (not 9)
        Assert.Equal(7u, ParseSingleCell("1 + 2 * 3"));
    }

    [Fact]
    public void Additive_BindsTighterThanShift()
    {
        // 1 + 2 << 1 = 3 << 1 = 6 (not 1 + 4 = 5)
        Assert.Equal(6u, ParseSingleCell("1 + 2 << 1"));
    }

    [Fact]
    public void Shift_BindsTighterThanRelational()
    {
        // (1 << 4) <= 20 → 16 <= 20 → 1
        // Uses '<=' to avoid conflict with cell-list '>' delimiter.
        Assert.Equal(1u, ParseSingleCell("1 << 4 <= 20"));
    }

    [Fact]
    public void Relational_BindsTighterThanEquality()
    {
        // (1 <= 2) == 1 → 1 == 1 → 1
        // Uses '<=' to avoid conflict with cell-list '>' delimiter.
        Assert.Equal(1u, ParseSingleCell("1 <= 2 == 1"));
    }

    // ---- Bitwise AND vs phandle ----

    [Fact]
    public void BitwiseOr_InCell()
    {
        // 0x10 | 0x01 = 0x11
        Assert.Equal(0x11u, ParseSingleCell("0x10 | 0x01"));
    }

    [Fact]
    public void BitwiseXor_InCell()
    {
        // 0xff ^ 0x0f = 0xf0
        Assert.Equal(0xf0u, ParseSingleCell("0xff ^ 0x0f"));
    }

    // ---- Parentheses ----

    [Fact]
    public void Parentheses_OverridePrecedence()
    {
        // (1 + 2) * 3 = 9
        Assert.Equal(9u, ParseSingleCell("(1 + 2) * 3"));
    }

    [Fact]
    public void NestedParentheses()
    {
        // ((1 + 2) * (3 + 4)) = 3 * 7 = 21
        Assert.Equal(21u, ParseSingleCell("(1 + 2) * (3 + 4)"));
    }

    // ---- Multiple cells in one <...> ----

    [Fact]
    public void MultipleCells_SpaceSeparated()
    {
        var dts = "/dts-v1/;\n/ { prop = <1 2 3>; };\n";
        var tree = DtsFormat.Parse(dts);
        Assert.True(tree.IsSuccess);
        Assert.Equal([1u, 2u, 3u], tree.Tree!.Root.FindProperty("prop")!.GetCells());
    }

    [Fact]
    public void ExpressionWithMultipleCells()
    {
        // Each value is a separate cell
        var dts = "/dts-v1/;\n/ { prop = <1 + 2 3 * 4>; };\n";
        var tree = DtsFormat.Parse(dts);
        Assert.True(tree.IsSuccess);
        var cells = tree.Tree!.Root.FindProperty("prop")!.GetCells();
        Assert.Equal([3u, 12u], cells);
    }

    // ---- Logical operators ----

    [Fact]
    public void LogicalOr_TrueCase()
    {
        // 0 || 1 = 1
        Assert.Equal(1u, ParseSingleCell("0 || 1"));
    }

    [Fact]
    public void LogicalOr_FalseCase()
    {
        // 0 || 0 = 0
        Assert.Equal(0u, ParseSingleCell("0 || 0"));
    }

    [Fact]
    public void LogicalAnd_TrueCase()
    {
        // 1 && 1 = 1
        Assert.Equal(1u, ParseSingleCell("1 && 1"));
    }

    [Fact]
    public void LogicalAnd_FalseCase()
    {
        // 1 && 0 = 0
        Assert.Equal(0u, ParseSingleCell("1 && 0"));
    }

    // ---- Unary ----

    [Fact]
    public void UnaryMinus()
    {
        // -1 in a 32-bit cell = 0xFFFFFFFF
        Assert.Equal(0xFFFFFFFFu, ParseSingleCell("-1"));
    }

    [Fact]
    public void UnaryNot()
    {
        // !0 = 1
        Assert.Equal(1u, ParseSingleCell("!0"));
    }

    [Fact]
    public void UnaryBitwiseNot()
    {
        // ~0 = 0xFFFFFFFF
        Assert.Equal(0xFFFFFFFFu, ParseSingleCell("~0"));
    }

    // ---- Modulo ----

    [Fact]
    public void Modulo()
    {
        // 10 % 3 = 1
        Assert.Equal(1u, ParseSingleCell("10 % 3"));
    }

    // ---- Division ----

    [Fact]
    public void Division()
    {
        // 10 / 3 = 3 (integer division)
        Assert.Equal(3u, ParseSingleCell("10 / 3"));
    }

    // ---- Combined ----

    [Fact]
    public void ComplexExpression()
    {
        // (0xff & 0x0f) | (0x10 << 2) = 0x0f | 0x40 = 0x4f = 79
        Assert.Equal(79u, ParseSingleCell("(0xff & 0x0f) | (0x10 << 2)"));
    }
}
