using UotanStudio.Firmware.Application.Equivalence;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Tests.Views;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Checks that both locale dictionaries carry the equivalence wording and that a rendered report stays inside one
/// language, so the image panel never shows a Chinese sentence built from English fragments or the other way round.
/// <para>校验两份语言字典都带有等价文案，并且渲染出的报告始终落在同一种语言内，镜像面板因此绝不会用英文片段拼出
/// 中文句子，也不会反向拼接。</para>
/// </summary>
[Collection("Node editor UI")]
public sealed class FirmwareEquivalenceLocaleTests(NodeEditorUiFixture fixture)
{
    private static readonly string[] DimensionKeys =
    [
        "Image_EquivalenceDimensionByte",
        "Image_EquivalenceDimensionStructure",
        "Image_EquivalenceDimensionContent",
        "Image_EquivalenceDimensionCapacity",
        "Image_EquivalenceStateEquivalent",
        "Image_EquivalenceStateNotEquivalent",
        "Image_EquivalenceStateNotEvaluated",
        FirmwareEquivalencePresenter.LineKey,
        FirmwareEquivalencePresenter.HeaderKey,
    ];

    private static FilesystemEquivalenceReport MixedReport() => FilesystemEquivalenceReport.FromMetadata(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FilesystemEquivalenceReport.ByteEquivalenceKey] = "False",
            [FilesystemEquivalenceReport.StructureEquivalenceKey] = "True",
            [FilesystemEquivalenceReport.CapacityEquivalenceKey] = "True",
        });

    private static int ArgumentCount(string format)
    {
        var count = 0;
        for (var index = format.IndexOf('{'); index >= 0; index = format.IndexOf('{', index + 1))
        {
            if (index + 1 < format.Length && char.IsDigit(format[index + 1]))
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public async Task Resources_BothLanguages_EquivalenceKeysMatchAndFormatArgumentsAlign()
    {
        var token = TestContext.Current.CancellationToken;
        Task pending;
        using (ExecutionContext.SuppressFlow())
        {
            pending = fixture.Session.Dispatch(() =>
            {
                var english = new UotanStudio.Locale.EnUs.Common();
                var chinese = new UotanStudio.Locale.ZhCn.Common();
                var englishKeys = english.Keys.OfType<string>()
                    .Where(key => key.StartsWith("Image_Equivalence", StringComparison.Ordinal)).Order().ToArray();
                var chineseKeys = chinese.Keys.OfType<string>()
                    .Where(key => key.StartsWith("Image_Equivalence", StringComparison.Ordinal)).Order().ToArray();

                Assert.Equal(DimensionKeys.Order().ToArray(), englishKeys);
                Assert.Equal(englishKeys, chineseKeys);
                foreach (var key in englishKeys)
                {
                    var en = Assert.IsType<string>(english[key]);
                    var zh = Assert.IsType<string>(chinese[key]);
                    Assert.False(string.IsNullOrWhiteSpace(en));
                    Assert.False(string.IsNullOrWhiteSpace(zh));
                    Assert.NotEqual(en, zh);
                    Assert.Equal(ArgumentCount(en), ArgumentCount(zh));
                }
            }, token);
        }

        await pending.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    [Fact]
    public async Task FormatLines_SwitchedLanguages_ReportStaysWithinTheActiveLanguage()
    {
        var token = TestContext.Current.CancellationToken;
        Task pending;
        using (ExecutionContext.SuppressFlow())
        {
            pending = fixture.Session.Dispatch(() =>
            {
                var language = LanguageService.Instance;
                var original = language.CurrentLanguage;
                language.Initialize();
                try
                {
                    language.ApplyLanguage("en-US");
                    var englishLines = FirmwareEquivalencePresenter.FormatLines(MixedReport(), language.GetString);
                    Assert.Equal(
                    [
                        "Byte identity: Not equivalent",
                        "Inode structure: Equivalent",
                        "File content: Not evaluated by the library",
                        "Capacity: Equivalent",
                    ], englishLines);
                    Assert.All(englishLines, line => Assert.DoesNotMatch("[\u4e00-\u9fff]", line));
                    Assert.Equal("Equivalence of the built image: system.img",
                        FirmwareEquivalencePresenter.FormatHeader(language.GetString, "system.img"));

                    language.ApplyLanguage("zh-CN");
                    var chineseLines = FirmwareEquivalencePresenter.FormatLines(MixedReport(), language.GetString);
                    Assert.Equal(
                    [
                        "字节一致：不等价",
                        "结构：等价",
                        "文件内容：库未执行评估",
                        "容量：等价",
                    ], chineseLines);
                    Assert.All(chineseLines, line => Assert.DoesNotMatch("[A-Za-z]", line));
                    Assert.Equal("产出镜像的等价性：system.img",
                        FirmwareEquivalencePresenter.FormatHeader(language.GetString, "system.img"));
                }
                finally
                {
                    language.ApplyLanguage(original);
                }
            }, token);
        }

        await pending.WaitAsync(TimeSpan.FromSeconds(30), token);
    }
}