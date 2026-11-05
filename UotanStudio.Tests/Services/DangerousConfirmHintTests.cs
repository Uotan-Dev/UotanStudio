using System.Globalization;
using UotanStudio.Services;
using UotanStudio.Services.Ui;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class DangerousConfirmHintTests
{
    private static readonly string[] CallerTokens = ["CONFIRM", "FLASH", "WIPE", "FES", "EXEC"];

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void ConfirmHintTemplate_EveryBundledCulture_UsesTheCallerToken(string culture)
    {
        var template = LanguageService.Instance.GetTranslation(
            "DangerousConfirm_EnterHint", CultureInfo.GetCultureInfo(culture));

        Assert.Contains("{0}", template, StringComparison.Ordinal);

        var formatted = string.Format(CultureInfo.InvariantCulture, template, "FLASH");

        Assert.Contains("FLASH", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatConfirmHint_EachCallerToken_NamesTheExactGateWord()
    {
        foreach (var token in CallerTokens)
        {
            var hint = DialogService.FormatConfirmHint(token);

            Assert.Contains(token, hint, StringComparison.Ordinal);
            Assert.NotEqual("DangerousConfirm_EnterHint", hint);
        }
    }

    [Fact]
    public void ShouldEnableConfirm_DefaultWord_OnlyAcceptsExactTickedInput()
    {
        Assert.True(DialogService.ShouldEnableConfirm(true, "CONFIRM", "CONFIRM"));
        Assert.False(DialogService.ShouldEnableConfirm(false, "CONFIRM", "CONFIRM"));
        Assert.False(DialogService.ShouldEnableConfirm(true, "confirm", "CONFIRM"));
        Assert.False(DialogService.ShouldEnableConfirm(true, " CONFIRM", "CONFIRM"));
        Assert.False(DialogService.ShouldEnableConfirm(true, string.Empty, "CONFIRM"));
    }
}