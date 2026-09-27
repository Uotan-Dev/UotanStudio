using Avalonia;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Services;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class SettingsStartupTests(NodeEditorUiFixture fixture)
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public async Task Constructor_AlreadyAppliedLanguage_PreservesResourcesAndToolbox(string culture)
    {
        await fixture.Session.Dispatch(() =>
        {
            var language = LanguageService.Instance;
            var previous = language.CurrentLanguage;
            var recipient = new object();
            var notifications = 0;
            try
            {
                language.Initialize();
                language.ApplyLanguage(culture);
                using var nodes = new NodeEditorViewModel();
                var categories = nodes.ToolboxCategories.ToArray();
                var resources = Application.Current!.Resources.MergedDictionaries.ToArray();
                WeakReferenceMessenger.Default.Register<LanguageStateChangedMessage>(recipient,
                    (_, _) => notifications++);
                var input = new AppSettings
                {
                    CultureName = culture,
                    ThemeMode = ThemeMode.Dark,
                    AutoSave = true,
                    McpExposePort = 19001
                };

                using var settings = new SettingsViewModel(input, applyLanguage: false);

                Assert.Equal(culture, language.CurrentLanguage);
                Assert.Equal(culture, settings.SelectedLanguage.CultureName);
                Assert.Equal(ThemeMode.Dark, settings.SelectedThemeMode);
                Assert.True(settings.AutoSave);
                Assert.Equal(19001, settings.McpExposePort);
                Assert.Equal(0, notifications);
                Assert.Equal(resources, Application.Current.Resources.MergedDictionaries);
                Assert.Equal(categories, nodes.ToolboxCategories);
            }
            finally
            {
                WeakReferenceMessenger.Default.UnregisterAll(recipient);
                language.ApplyLanguage(previous);
            }
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("en-US", "zh-CN")]
    [InlineData("zh-CN", "en-US")]
    public async Task Constructor_LanguageNotApplied_ChangesResourcesAndNotifies(string initial, string requested)
    {
        await fixture.Session.Dispatch(() =>
        {
            var language = LanguageService.Instance;
            var previous = language.CurrentLanguage;
            var recipient = new object();
            var notifications = 0;
            try
            {
                language.Initialize();
                language.ApplyLanguage(initial);
                WeakReferenceMessenger.Default.Register<LanguageStateChangedMessage>(recipient,
                    (_, _) => notifications++);

                using var settings = new SettingsViewModel(new AppSettings { CultureName = requested }, applyLanguage: true);

                Assert.Equal(requested, language.CurrentLanguage);
                Assert.Equal(requested, settings.SelectedLanguage.CultureName);
                Assert.Equal(1, notifications);
            }
            finally
            {
                WeakReferenceMessenger.Default.UnregisterAll(recipient);
                language.ApplyLanguage(previous);
            }
        }, TestContext.Current.CancellationToken);
    }
}