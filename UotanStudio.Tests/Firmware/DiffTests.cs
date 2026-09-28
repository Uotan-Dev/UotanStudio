using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.Engines;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// bsdiff delta engine round-trip tests: generate a patch between two small files containing
/// repeated blocks, apply it back and assert the output is byte-identical to the target; the same
/// file must also be able to generate a patch that applies successfully.
/// <para>bsdiff 差分引擎往返测试：对两个含重复块的小文件生成补丁，应用后断言输出与目标逐字节一致；
/// 相同文件生成的补丁也必须能成功应用。</para>
/// </summary>
public class DiffTests
{
    [Fact]
    public async Task GenerateThenApply_RoundTripsToTarget()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"uotan-diff_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var oldPath = Path.Combine(tmp, "old.bin");
            var newPath = Path.Combine(tmp, "new.bin");
            var patchPath = Path.Combine(tmp, "diff.patch");
            var outputPath = Path.Combine(tmp, "out.bin");

            var oldData = BuildRepeatedData(16 * 1024, seed: 1);
            var newData = BuildRepeatedData(16 * 1024, seed: 2);
            File.WriteAllBytes(oldPath, oldData);
            File.WriteAllBytes(newPath, newData);

            var generated = await DeltaEngine.GenerateDeltaAsync(
                oldPath, newPath, patchPath, TestContext.Current.CancellationToken);
            Assert.True(generated.Success, generated.ErrorMessage);
            Assert.Equal(oldData.Length, generated.SourceLength);
            Assert.True(File.Exists(patchPath));
            Assert.True(generated.DiffLength > 0);

            var applied = await DeltaEngine.ApplyDeltaAsync(
                oldPath, patchPath, outputPath, TestContext.Current.CancellationToken);
            Assert.True(applied.Success, applied.ErrorMessage);

            Assert.Equal(newData, File.ReadAllBytes(outputPath));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateSameFile_ThenApply_Succeeds()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"uotan-diff_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var filePath = Path.Combine(tmp, "same.bin");
            var patchPath = Path.Combine(tmp, "same.patch");
            var outputPath = Path.Combine(tmp, "out.bin");

            var data = BuildRepeatedData(8 * 1024, seed: 7);
            File.WriteAllBytes(filePath, data);

            var generated = await DeltaEngine.GenerateDeltaAsync(
                filePath, filePath, patchPath, TestContext.Current.CancellationToken);
            Assert.True(generated.Success, generated.ErrorMessage);
            Assert.True(File.Exists(patchPath));

            var applied = await DeltaEngine.ApplyDeltaAsync(
                filePath, patchPath, outputPath, TestContext.Current.CancellationToken);
            Assert.True(applied.Success, applied.ErrorMessage);

            Assert.Equal(data, File.ReadAllBytes(outputPath));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    private static byte[] BuildRepeatedData(int length, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        rng.NextBytes(data);
        for (var i = 0; i < length; i += 256)
        {
            for (var j = 0; j < 64; j++)
            {
                data[i + j] = (byte)(j * 3 + seed);
            }
        }

        return data;
    }
}