using UotanStudio.Firmware.Application.Equivalence;
using UotanStudio.Services.Firmware;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Pins the unified-log rendering of the equivalence report: one fixed-English line that never carries a line
/// break or Chinese wording, an honest "not evaluated" wording for a dimension the library never checked, and
/// the four dimensions kept apart instead of collapsed into a single verdict.
/// <para>固定等价报告的统一日志呈现：固定英文单行，绝不出现换行或中文文案；库未检查的维度必须使用如实的
/// “未评估”文案；四个维度保持独立而不折叠成单一判定。</para>
/// </summary>
public sealed class FirmwareEquivalencePresenterTests
{
    private static FilesystemEquivalenceReport Report(params (string Key, string Value)[] flags)
        => FilesystemEquivalenceReport.FromMetadata(
            flags.ToDictionary(flag => flag.Key, flag => flag.Value, StringComparer.Ordinal));

    [Fact]
    public void FormatLogLine_AllDimensionsEquivalent_RendersOneEnglishLine()
        => Assert.Equal(
            "Equivalence of the built image: system.img | Byte identity: Equivalent | Inode structure: Equivalent | " +
            "File content: Equivalent | Capacity: Equivalent",
            FirmwareEquivalencePresenter.FormatLogLine("system.img", Report(
                (FilesystemEquivalenceReport.ByteEquivalenceKey, "True"),
                (FilesystemEquivalenceReport.StructureEquivalenceKey, "True"),
                (FilesystemEquivalenceReport.ContentEquivalenceKey, "True"),
                (FilesystemEquivalenceReport.CapacityEquivalenceKey, "True"))));

    [Fact]
    public void FormatLogLine_EditedImage_KeepsByteVerdictSeparateFromEquivalentDimensions()
    {
        var line = FirmwareEquivalencePresenter.FormatLogLine("system.img", Report(
            (FilesystemEquivalenceReport.ByteEquivalenceKey, "False"),
            (FilesystemEquivalenceReport.StructureEquivalenceKey, "True"),
            (FilesystemEquivalenceReport.ContentEquivalenceKey, "True"),
            (FilesystemEquivalenceReport.CapacityEquivalenceKey, "True")));

        Assert.Equal(
            "Equivalence of the built image: system.img | Byte identity: Not equivalent | Inode structure: Equivalent | " +
            "File content: Equivalent | Capacity: Equivalent",
            line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    [Fact]
    public void FormatLogLine_AllDimensionsNotEvaluated_RendersNotEvaluatedWordingForEveryDimension()
        => Assert.Equal(
            "Equivalence of the built image: system.img | Byte identity: Not evaluated by the library | " +
            "Inode structure: Not evaluated by the library | File content: Not evaluated by the library | " +
            "Capacity: Not evaluated by the library",
            FirmwareEquivalencePresenter.FormatLogLine("system.img", FilesystemEquivalenceReport.AllNotEvaluated));

    [Fact]
    public void FormatLogLine_AnyReport_StaysWithinEnglishOnOneLine()
    {
        var line = FirmwareEquivalencePresenter.FormatLogLine("system", Report(
            (FilesystemEquivalenceReport.ByteEquivalenceKey, "False"),
            (FilesystemEquivalenceReport.StructureEquivalenceKey, "maybe")));

        Assert.Equal(
            "Equivalence of the built image: system | Byte identity: Not equivalent | " +
            "Inode structure: Not evaluated by the library | File content: Not evaluated by the library | " +
            "Capacity: Not evaluated by the library",
            line);
        Assert.DoesNotMatch("[\u4e00-\u9fff]", line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    [Theory]
    [InlineData(FirmwareEquivalenceState.Equivalent, "Equivalent")]
    [InlineData(FirmwareEquivalenceState.NotEquivalent, "Not equivalent")]
    [InlineData(FirmwareEquivalenceState.NotEvaluated, "Not evaluated by the library")]
    public void StateWording_EachState_SelectsItsOwnWording(FirmwareEquivalenceState state, string expected)
        => Assert.Equal(expected, FirmwareEquivalencePresenter.StateWording(state));

    [Fact]
    public void FormatLogLine_MissingReport_ThrowsInsteadOfRenderingAnEmptyClaim()
        => Assert.Throws<ArgumentNullException>(
            () => FirmwareEquivalencePresenter.FormatLogLine("system.img", null!));

    [Fact]
    public void FormatLogLine_MissingEntryName_ThrowsInsteadOfRenderingAnAnonymousLine()
        => Assert.Throws<ArgumentNullException>(
            () => FirmwareEquivalencePresenter.FormatLogLine(null!, FilesystemEquivalenceReport.AllNotEvaluated));
}