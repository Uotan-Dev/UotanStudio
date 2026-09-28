using UotanStudio.Firmware.DeviceTree.Dt;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// DTS source parse → write round-trip tests via the ported DtsFormat.
/// <para>借助移植的 DtsFormat 做 DTS 源解析 → 写入往返测试。</para>
/// </summary>
public class DtsRoundTripTests
{
    private const string MinimalDts = "/dts-v1/; / { compatible = \"x\"; child@0 { reg = <0x1 0x2>; }; };";

    [Fact]
    public void ParseMinimal_Succeeds_AndYieldsTree()
    {
        var result = DtsFormat.Parse(MinimalDts);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.NotNull(result.Tree);
    }

    [Fact]
    public void WriteMinimal_ContainsNodeAndPropertyNames()
    {
        var result = DtsFormat.Parse(MinimalDts);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Tree);

        var output = DtsFormat.Write(result.Tree);

        Assert.Contains("compatible", output);
        Assert.Contains("child@0", output);
    }
}