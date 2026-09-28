using UotanStudio.Firmware.DeviceTree.Dt;
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
        return Assert.Single(prop!.GetCells());
    }

    [Fact]
    public void Multiplicative_BindsTighterThanAdditive()
    {
        Assert.Equal(7u, ParseSingleCell("1 + 2 * 3"));
    }

    [Fact]
    public void Additive_BindsTighterThanShift()
    {
        Assert.Equal(6u, ParseSingleCell("1 + 2 << 1"));
    }

    [Fact]
    public void Shift_BindsTighterThanRelational()
    {
        Assert.Equal(1u, ParseSingleCell("1 << 4 <= 20"));
    }

    [Fact]
    public void Relational_BindsTighterThanEquality()
    {
        Assert.Equal(1u, ParseSingleCell("1 <= 2 == 1"));
    }

    [Fact]
    public void BitwiseOr_InCell()
    {
        Assert.Equal(0x11u, ParseSingleCell("0x10 | 0x01"));
    }

    [Fact]
    public void BitwiseXor_InCell()
    {
        Assert.Equal(0xf0u, ParseSingleCell("0xff ^ 0x0f"));
    }

    [Fact]
    public void Parentheses_OverridePrecedence()
    {
        Assert.Equal(9u, ParseSingleCell("(1 + 2) * 3"));
    }

    [Fact]
    public void NestedParentheses()
    {
        Assert.Equal(21u, ParseSingleCell("(1 + 2) * (3 + 4)"));
    }

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
        var dts = "/dts-v1/;\n/ { prop = <1 + 2 3 * 4>; };\n";
        var tree = DtsFormat.Parse(dts);
        Assert.True(tree.IsSuccess);
        var cells = tree.Tree!.Root.FindProperty("prop")!.GetCells();
        Assert.Equal([3u, 12u], cells);
    }

    [Fact]
    public void LogicalOr_TrueCase()
    {
        Assert.Equal(1u, ParseSingleCell("0 || 1"));
    }

    [Fact]
    public void LogicalOr_FalseCase()
    {
        Assert.Equal(0u, ParseSingleCell("0 || 0"));
    }

    [Fact]
    public void LogicalAnd_TrueCase()
    {
        Assert.Equal(1u, ParseSingleCell("1 && 1"));
    }

    [Fact]
    public void LogicalAnd_FalseCase()
    {
        Assert.Equal(0u, ParseSingleCell("1 && 0"));
    }

    [Fact]
    public void UnaryMinus()
    {
        Assert.Equal(0xFFFFFFFFu, ParseSingleCell("-1"));
    }

    [Fact]
    public void UnaryNot()
    {
        Assert.Equal(1u, ParseSingleCell("!0"));
    }

    [Fact]
    public void UnaryBitwiseNot()
    {
        Assert.Equal(0xFFFFFFFFu, ParseSingleCell("~0"));
    }

    [Fact]
    public void Modulo()
    {
        Assert.Equal(1u, ParseSingleCell("10 % 3"));
    }

    [Fact]
    public void Division()
    {
        Assert.Equal(3u, ParseSingleCell("10 / 3"));
    }

    [Fact]
    public void ComplexExpression()
    {
        Assert.Equal(79u, ParseSingleCell("(0xff & 0x0f) | (0x10 << 2)"));
    }
}