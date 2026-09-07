using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using UotanStudio.Firmware;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Diff format handler tests: a real bsdiff patch produced by <see cref="DeltaEngine"/> must be recognized
/// by <see cref="FormatDetector"/> (after <see cref="FirmwareFormatRegistry.RegisterAll"/>) and expose
/// non-empty header metadata; a bare 'BSDIFF40' header is detected by content even without an extension,
/// while unrelated data must not be misrouted.
/// <para>Diff 格式处理器测试：经 <see cref="DeltaEngine"/> 产出的真实 bsdiff 补丁在
/// <see cref="FirmwareFormatRegistry.RegisterAll"/> 后须能被 <see cref="FormatDetector"/> 识别并暴露非空的
/// 头部元数据；无扩展名时裸 'BSDIFF40' 头部也能按内容识别，而无关数据不得被误路由。</para>
/// </summary>
public class DiffFormatHandlerTests
{
    private const string DiffFormatName = "bsdiff Binary Patch";

    [Fact]
    public async Task Detect_GeneratedPatch_ReturnsDiffHandler()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var oldPath = temp.WriteBytes("old.bin", BuildRepeatedData(16 * 1024, seed: 1));
        var newPath = temp.WriteBytes("new.bin", BuildRepeatedData(16 * 1024, seed: 2));
        var patchPath = Path.Combine(temp.Path, "diff.bsdiff");

        var generated = await DeltaEngine.GenerateDeltaAsync(oldPath, newPath, patchPath, TestContext.Current.CancellationToken);
        Assert.True(generated.Success, generated.ErrorMessage);

        var handler = FormatDetector.Detect(patchPath);

        Assert.NotNull(handler);
        Assert.Equal(DiffFormatName, handler!.FormatName);
    }

    [Fact]
    public async Task ReadMetadata_GeneratedPatch_ReturnsNonEmptyMetadata()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var oldPath = temp.WriteBytes("old.bin", BuildRepeatedData(16 * 1024, seed: 1));
        var newData = BuildRepeatedData(16 * 1024, seed: 2);
        var newPath = temp.WriteBytes("new.bin", newData);
        var patchPath = Path.Combine(temp.Path, "diff.bsdiff");

        var generated = await DeltaEngine.GenerateDeltaAsync(oldPath, newPath, patchPath, TestContext.Current.CancellationToken);
        Assert.True(generated.Success, generated.ErrorMessage);

        var handler = FormatDetector.Detect(patchPath);
        Assert.NotNull(handler);
        Assert.Equal(DiffFormatName, handler!.FormatName);

        var result = await handler.ReadMetadataAsync(patchPath, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.NotEmpty(result.Metadata);
        Assert.Equal("bsdiff (BSDIFF40)", result.Metadata["PatchType"]);
        Assert.Equal(newData.Length.ToString(CultureInfo.InvariantCulture), result.Metadata["NewFileSize"]);
        Assert.Equal(new FileInfo(patchPath).Length.ToString(CultureInfo.InvariantCulture), result.Metadata["FileLength"]);
    }

    [Fact]
    public void Detect_BsdiffHeaderWithoutExtension_IsDiff()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("patch", BuildPatchHeader(control: 64, diff: 128, newSize: 1024));

        var handler = FormatDetector.Detect(path);

        Assert.NotNull(handler);
        Assert.Equal(DiffFormatName, handler!.FormatName);
    }

    [Fact]
    public void Detect_RandomData_NotDiff()
    {
        FirmwareFormatRegistry.RegisterAll();
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("random.bsdiff", [1, 2, 3, 4, 5, 6, 7, 8]);

        var handler = FormatDetector.Detect(path);

        Assert.NotEqual(DiffFormatName, handler?.FormatName);
    }

    private static byte[] BuildPatchHeader(long control, long diff, long newSize)
    {
        var header = new byte[32];
        Encoding.ASCII.GetBytes("BSDIFF40").CopyTo(header, 0);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8, 8), control);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16, 8), diff);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24, 8), newSize);
        return header;
    }

    private static byte[] BuildRepeatedData(int length, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        rng.NextBytes(data);
        // Add repetitive blocks so the bsdiff algorithm has matches to exploit.
        // 添加重复块，使 bsdiff 算法有可用的匹配。
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