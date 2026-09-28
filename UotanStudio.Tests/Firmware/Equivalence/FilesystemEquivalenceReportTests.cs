using System.Security.Cryptography;
using UotanStudio.Firmware.Application.Equivalence;
using UotanStudio.Firmware.Core;
using Xunit;

namespace UotanStudio.Tests.Firmware.Equivalence;

/// <summary>
/// Guards the four-dimension equivalence façade with negative controls: un-run checks must stay
/// NotEvaluated, the byte evaluator must separate byte identity from content equivalence, and a
/// capacity verdict of False must surface as NotEquivalent only for the capacity dimension.
/// <para>以负向对照守卫四维度等价门面：未执行的检查必须保持不评估，字节评估器必须把字节一致性与内容
/// 等价性分离，容量判定为 False 时必须仅让容量维度呈现不等价。</para>
/// </summary>
public sealed class FilesystemEquivalenceReportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void FromMetadata_NullMetadata_ReportsEveryDimensionNotEvaluated()
    {
        var report = FilesystemEquivalenceReport.FromMetadata(null);

        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.ContentEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.CapacityEquivalence);
    }

    [Fact]
    public void FromMetadata_EmptyMetadata_ReportsNoDimensionEquivalent()
    {
        var report = FilesystemEquivalenceReport.FromMetadata(new Dictionary<string, string>());

        Assert.NotEqual(FirmwareEquivalenceState.Equivalent, report.ByteEquivalence);
        Assert.NotEqual(FirmwareEquivalenceState.Equivalent, report.StructureEquivalence);
        Assert.NotEqual(FirmwareEquivalenceState.Equivalent, report.ContentEquivalence);
        Assert.NotEqual(FirmwareEquivalenceState.Equivalent, report.CapacityEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.ContentEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.CapacityEquivalence);
    }

    [Fact]
    public void FromOperationResult_SuccessWithoutMetadata_ReportsEveryDimensionNotEvaluated()
    {
        var report = FilesystemEquivalenceReport.FromOperationResult(FirmwareOpResult.Ok("fresh build"));

        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.ContentEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.CapacityEquivalence);
    }

    [Fact]
    public void FromOperationResult_NullResult_ReportsEveryDimensionNotEvaluated()
    {
        var report = FilesystemEquivalenceReport.FromOperationResult(null);

        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.CapacityEquivalence);
    }

    [Fact]
    public void AllNotEvaluated_FreshReport_ClaimsNoEquivalence()
    {
        var report = FilesystemEquivalenceReport.AllNotEvaluated;

        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.ContentEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.CapacityEquivalence);
        Assert.Empty(report.Details);
    }

    [Fact]
    public void FromMetadata_CapacityFalseOnly_ReportsCapacityNotEquivalentAndOthersNotEvaluated()
    {
        var report = FilesystemEquivalenceReport.FromMetadata(new Dictionary<string, string>
        {
            ["FitsSourceFilesystemSize"] = "False",
            ["SizeExcess"] = "4096",
        });

        Assert.Equal(FirmwareEquivalenceState.NotEquivalent, report.CapacityEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.ContentEquivalence);
        Assert.Equal("4096", report.Details["SizeExcess"]);
    }

    [Fact]
    public void FromMetadata_ByteFalseWithEquivalentContent_KeepsDimensionsIndependent()
    {
        var report = FilesystemEquivalenceReport.FromMetadata(new Dictionary<string, string>
        {
            ["EquivalentBytes"] = "False",
            ["EquivalentStructure"] = "True",
            ["EquivalentContent"] = "True",
            ["FitsSourceFilesystemSize"] = "True",
        });

        Assert.Equal(FirmwareEquivalenceState.NotEquivalent, report.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, report.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, report.ContentEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, report.CapacityEquivalence);
        Assert.NotEqual(report.ByteEquivalence, report.ContentEquivalence);
    }

    [Fact]
    public void FromMetadata_LowercaseFlags_ParsesBothVerdicts()
    {
        var report = FilesystemEquivalenceReport.FromMetadata(new Dictionary<string, string>
        {
            ["EquivalentBytes"] = "true",
            ["EquivalentContent"] = "FALSE",
        });

        Assert.Equal(FirmwareEquivalenceState.Equivalent, report.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEquivalent, report.ContentEquivalence);
    }

    [Fact]
    public void FromMetadata_UnparsableFlag_ReportsNotEvaluatedInsteadOfGuessing()
    {
        var report = FilesystemEquivalenceReport.FromMetadata(new Dictionary<string, string>
        {
            ["EquivalentBytes"] = "probably",
        });

        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, report.ByteEquivalence);
    }

    [Fact]
    public void EvaluateByteEquivalence_UntouchedCopy_ReportsEquivalent()
    {
        var image = BuildImage(70_000);
        var digest = Convert.ToHexString(SHA256.HashData(image));
        using var stream = new MemoryStream(image);

        Assert.Equal(FirmwareEquivalenceState.Equivalent,
            FilesystemEquivalenceReport.EvaluateByteEquivalence(stream, digest, image.Length, Token));
    }

    [Fact]
    public void EvaluateByteEquivalence_EditedImageWithEquivalentContent_ByteAndContentDimensionsDisagree()
    {
        var source = BuildImage(70_000);
        var digest = Convert.ToHexString(SHA256.HashData(source));
        var edited = (byte[])source.Clone();
        edited[1234] ^= 0xFF;
        using var stream = new MemoryStream(edited);

        var byteState = FilesystemEquivalenceReport.EvaluateByteEquivalence(stream, digest, source.Length, Token);
        var report = new FilesystemEquivalenceReport
        {
            ByteEquivalence = byteState,
            ContentEquivalence = FirmwareEquivalenceState.Equivalent,
        };

        Assert.Equal(FirmwareEquivalenceState.NotEquivalent, byteState);
        Assert.NotEqual(report.ByteEquivalence, report.ContentEquivalence);
    }

    [Fact]
    public void EvaluateByteEquivalence_SeekableLengthMismatch_ShortCircuitsWithoutDigesting()
    {
        var image = BuildImage(4096);
        var digest = Convert.ToHexString(SHA256.HashData(image));
        using var stream = new MemoryStream(image);

        var state = FilesystemEquivalenceReport.EvaluateByteEquivalence(stream, digest, image.Length + 1, Token);

        Assert.Equal(FirmwareEquivalenceState.NotEquivalent, state);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void EvaluateByteEquivalence_TruncatedStreamWithDigestOfFullImage_ReportsNotEquivalent()
    {
        var image = BuildImage(8192);
        var digest = Convert.ToHexString(SHA256.HashData(image));
        using var stream = new MemoryStream(image, 0, 4096);

        Assert.Equal(FirmwareEquivalenceState.NotEquivalent,
            FilesystemEquivalenceReport.EvaluateByteEquivalence(stream, digest, image.Length, Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void EvaluateByteEquivalence_MalformedDigest_ThrowsArgumentException(string digest)
    {
        using var stream = new MemoryStream(BuildImage(16));

        Assert.Throws<ArgumentException>(() =>
            FilesystemEquivalenceReport.EvaluateByteEquivalence(stream, digest, 16, Token));
    }

    [Fact]
    public void EvaluateByteEquivalence_CancelledDuringDigestLoop_ThrowsOperationCanceledException()
    {
        var image = BuildImage(65_536 + 8);
        var digest = Convert.ToHexString(SHA256.HashData(image));
        using var stream = new MemoryStream(image);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            FilesystemEquivalenceReport.EvaluateByteEquivalence(stream, digest, image.Length, cancellation.Token));
    }

    [Fact]
    public void EvaluateByteEquivalence_NegativeExpectedLength_ThrowsArgumentOutOfRangeException()
    {
        using var stream = new MemoryStream(BuildImage(16));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FilesystemEquivalenceReport.EvaluateByteEquivalence(stream, new string('0', 64), -1, Token));
    }

    private static byte[] BuildImage(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 137 + (i >> 8)) & 0xFF);
        }

        return data;
    }
}