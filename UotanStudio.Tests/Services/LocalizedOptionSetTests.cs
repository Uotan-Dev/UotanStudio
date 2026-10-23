using System.Globalization;
using UotanStudio.Services;
using UotanStudio.ViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

[Collection("Workspace state")]
public sealed class LocalizedOptionSetTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");

    [Fact]
    public void Options_ResolveUsbBackendText_AndRefreshAfterLanguageSwitch()
    {
        var language = LanguageService.Instance;
        var original = language.CurrentLanguage;
        try
        {
            language.ApplyLanguage("en-US");
            var set = new LocalizedOptionSet(
                "Settings_UsbAuto", "Settings_UsbNative", "Settings_UsbLibusb");

            Assert.Collection(
                set.Options,
                option => Assert.Equal(language.GetTranslation("Settings_UsbAuto", English), option.DisplayName),
                option => Assert.Equal(language.GetTranslation("Settings_UsbNative", English), option.DisplayName),
                option => Assert.Equal(language.GetTranslation("Settings_UsbLibusb", English), option.DisplayName));

            language.ApplyLanguage("zh-CN");

            Assert.Collection(
                set.Options,
                option => Assert.Equal(language.GetTranslation("Settings_UsbAuto", Chinese), option.DisplayName),
                option => Assert.Equal(language.GetTranslation("Settings_UsbNative", Chinese), option.DisplayName),
                option => Assert.Equal(language.GetTranslation("Settings_UsbLibusb", Chinese), option.DisplayName));
        }
        finally
        {
            language.ApplyLanguage(original);
        }
    }

    [Fact]
    public void Options_RaiseDisplayNameChange_ForEveryEntry_WhenLanguageChanges()
    {
        var language = LanguageService.Instance;
        var original = language.CurrentLanguage;
        try
        {
            var set = LocalizedOptionSet.FromOptions(
                LocalizedOption.FromResource("Settings_UsbAuto"),
                LocalizedOption.FromText("512"));
            var changed = new List<string?>();
            foreach (var option in set.Options)
            {
                option.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
            }

            language.ApplyLanguage(original == "zh-CN" ? "en-US" : "zh-CN");

            Assert.Equal([nameof(LocalizedOption.DisplayName), nameof(LocalizedOption.DisplayName)], changed);
        }
        finally
        {
            language.ApplyLanguage(original);
        }
    }

    [Fact]
    public void FixedEntry_KeepsItsLiteralText_WhenLanguageChanges()
    {
        var language = LanguageService.Instance;
        var original = language.CurrentLanguage;
        try
        {
            var set = LocalizedOptionSet.FromOptions(LocalizedOption.FromText("512"));

            language.ApplyLanguage(original == "zh-CN" ? "en-US" : "zh-CN");

            Assert.Equal("512", Assert.Single(set.Options).DisplayName);
        }
        finally
        {
            language.ApplyLanguage(original);
        }
    }
}