using System.Text;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.PuffDiff;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Operations;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// PUFFDIFF application tests. The internal <see cref="OperationProcessor"/> PUFFDIFF branch is
/// not directly reachable from the test assembly, so the tests exercise the public
/// <see cref="PuffDiffApplier"/> used by it: create a minimal PUFFDIFF patch (bsdiff re-encoded
/// with raw deflate) and assert the applied result equals the target data.
/// <para>PUFFDIFF 应用测试。内部 <see cref="OperationProcessor"/> 的 PUFFDIFF 分支无法从测试程序集
/// 直接访问，因此测试通过它使用的公开 <see cref="PuffDiffApplier"/> 进行：构造最小 PUFFDIFF 补丁
/// （bsdiff 再经原始 deflate 重编码），断言应用结果与目标数据一致。</para>
/// </summary>
public class PuffDiffTests
{
    [Fact]
    public void CreateThenApply_RoundTripsLargeBuffer()
    {
        var oldData = BuildData(0x10000, seed: 1);
        var newData = BuildData(0x10000, seed: 2);

        var patch = PuffDiffApplier.CreatePatch(oldData, newData);
        Assert.NotEmpty(patch);

        var restored = PuffDiffApplier.Apply(patch, oldData);
        Assert.Equal(newData, restored);
    }

    [Fact]
    public void Apply_SmallEdit_ProducesNewData()
    {
        var oldData = Encoding.UTF8.GetBytes(
            "the quick brown fox jumps over the lazy dog. the quick brown fox jumps over the lazy dog.");
        var newData = Encoding.UTF8.GetBytes(
            "the quick brown fox jumps over the lazy dog. the SLOW brown fox jumps over the lazy dog.");

        var patch = PuffDiffApplier.CreatePatch(oldData, newData);
        var restored = PuffDiffApplier.Apply(patch, oldData);

        Assert.Equal(newData, restored);
    }

    [Fact]
    public void DecompressPatch_InflatesDeflatedPatch()
    {
        var oldData = Encoding.UTF8.GetBytes("old partition data old partition data");
        var newData = Encoding.UTF8.GetBytes("new partition data new partition data");

        var patch = PuffDiffApplier.CreatePatch(oldData, newData);
        var standardBsdiff = PuffDiffApplier.DecompressPatch(patch);

        Assert.NotEmpty(standardBsdiff);
        Assert.NotEqual(patch, standardBsdiff);
    }

    private static byte[] BuildData(int length, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        rng.NextBytes(data);
        for (var i = 0; i < length; i += 256)
        {
            for (var j = 0; j < 64; j++)
            {
                data[i + j] = (byte)(j * 3);
            }
        }

        return data;
    }
}