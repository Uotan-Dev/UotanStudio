using UotanStudio.Firmware.Application.Equivalence;
using UotanStudio.Services.Firmware;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Pins the equivalence presentation: four separate localized lines, honest "not evaluated" wording for a
/// dimension the library never checked, and no wording of its own beyond what the injected locale supplies.
/// <para>固定等价报告的呈现方式：四行独立本地化文本；库未检查的维度必须使用如实的“未评估”文案；
/// 除注入语言提供的内容外不自行产生任何文案。</para>
/// </summary>
public sealed class FirmwareEquivalencePresenterTests
{
    private static readonly Dictionary<string, string> Locale = new(StringComparer.Ordinal)
    {
        [FirmwareEquivalencePresenter.LineKey] = "{0}: {1}",
        [FirmwareEquivalencePresenter.HeaderKey] = "Equivalence of the built image: {0}",
        [FirmwareEquivalencePresenter.MoreInLogKey] = "Equivalence of {0} earlier image(s) is recorded in the log.",
        [FirmwareEquivalencePresenter.MoreInLogKey] = "Equivalence of {0} earlier image(s) is recorded in the log.",
        ["Image_EquivalenceDimensionByte"] = "Byte identity",
        ["Image_EquivalenceDimensionStructure"] = "Inode structure",
        ["Image_EquivalenceDimensionContent"] = "File content",
        ["Image_EquivalenceDimensionCapacity"] = "Capacity",
        ["Image_EquivalenceStateEquivalent"] = "Equivalent",
        ["Image_EquivalenceStateNotEquivalent"] = "Not equivalent",
        ["Image_EquivalenceStateNotEvaluated"] = "Not evaluated by the library",
    };

    private static string Lookup(string key) => Locale[key];

    [Fact]
    public void FormatLines_AllDimensionsEquivalent_RendersFourDimensionLines()
    {
        var report = FilesystemEquivalenceReport.FromMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FilesystemEquivalenceReport.ByteEquivalenceKey] = "True",
            [FilesystemEquivalenceReport.StructureEquivalenceKey] = "True",
            [FilesystemEquivalenceReport.ContentEquivalenceKey] = "True",
            [FilesystemEquivalenceReport.CapacityEquivalenceKey] = "True",
        });

        Assert.Equal(
        [
            "Byte identity: Equivalent",
            "Inode structure: Equivalent",
            "File content: Equivalent",
            "Capacity: Equivalent",
        ],
        FirmwareEquivalencePresenter.FormatLines(report, Lookup));
    }

    [Fact]
    public void FormatLines_AllDimensionsNotEvaluated_RendersNotEvaluatedWordingForEveryDimension()
        => Assert.Equal(
        [
            "Byte identity: Not evaluated by the library",
            "Inode structure: Not evaluated by the library",
            "File content: Not evaluated by the library",
            "Capacity: Not evaluated by the library",
        ],
        FirmwareEquivalencePresenter.FormatLines(FilesystemEquivalenceReport.AllNotEvaluated, Lookup));

    [Fact]
    public void FormatLines_EditedImage_KeepsEveryDimensionOnItsOwnLine()
    {
        var report = FilesystemEquivalenceReport.FromMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FilesystemEquivalenceReport.ByteEquivalenceKey] = "False",
            [FilesystemEquivalenceReport.StructureEquivalenceKey] = "True",
            [FilesystemEquivalenceReport.ContentEquivalenceKey] = "True",
        });

        Assert.Equal(
        [
            "Byte identity: Not equivalent",
            "Inode structure: Equivalent",
            "File content: Equivalent",
            "Capacity: Not evaluated by the library",
        ],
        FirmwareEquivalencePresenter.FormatLines(report, Lookup));
    }

    [Theory]
    [InlineData(FirmwareEquivalenceState.Equivalent, "Image_EquivalenceStateEquivalent")]
    [InlineData(FirmwareEquivalenceState.NotEquivalent, "Image_EquivalenceStateNotEquivalent")]
    [InlineData(FirmwareEquivalenceState.NotEvaluated, "Image_EquivalenceStateNotEvaluated")]
    public void StateResourceKey_EachState_SelectsItsOwnWording(FirmwareEquivalenceState state, string expectedKey)
        => Assert.Equal(expectedKey, FirmwareEquivalencePresenter.StateResourceKey(state));

    [Fact]
    public void FormatHeader_EntryName_InsertsEntryIntoLocalizedTemplate()
        => Assert.Equal("Equivalence of the built image: system.img",
            FirmwareEquivalencePresenter.FormatHeader(Lookup, "system.img"));

    [Fact]
    public void FormatLines_LocalizedTemplatesAreUsedVerbatim_RequestsOnlyKnownKeys()
    {
        var requested = new List<string>();

        FirmwareEquivalencePresenter.FormatLines(FilesystemEquivalenceReport.AllNotEvaluated, key =>
        {
            requested.Add(key);
            return Locale[key];
        });

        Assert.Equal(
        [
            "Image_EquivalenceLine", "Image_EquivalenceDimensionByte", "Image_EquivalenceStateNotEvaluated",
            "Image_EquivalenceLine", "Image_EquivalenceDimensionStructure", "Image_EquivalenceStateNotEvaluated",
            "Image_EquivalenceLine", "Image_EquivalenceDimensionContent", "Image_EquivalenceStateNotEvaluated",
            "Image_EquivalenceLine", "Image_EquivalenceDimensionCapacity", "Image_EquivalenceStateNotEvaluated",
        ], requested);
    }

    [Fact]
    public void FormatLines_MissingReport_ThrowsInsteadOfRenderingAnEmptyClaim()
        => Assert.Throws<ArgumentNullException>(() => FirmwareEquivalencePresenter.FormatLines(null!, Lookup));

    [Fact]
    public void FormatVisibleReport_NoEntries_ReturnsEmptyText()
        => Assert.Equal(string.Empty, FirmwareEquivalencePresenter.FormatVisibleReport([], Lookup));

    [Fact]
    public void FormatVisibleReport_SingleEntry_KeepsTheWholeReportVisible()
        => Assert.Equal("system.img report",
            FirmwareEquivalencePresenter.FormatVisibleReport(["system.img report"], Lookup));

    [Fact]
    public void FormatVisibleReport_ManyEntries_KeepsOnlyTheLastReportAndPointsAtTheLog()
    {
        var visible = FirmwareEquivalencePresenter.FormatVisibleReport(
            ["first report", "second report", "third report"], Lookup);

        Assert.Equal(
            string.Join(Environment.NewLine,
                "third report",
                "Equivalence of 2 earlier image(s) is recorded in the log."),
            visible);
    }
}