using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.Services;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Views.Editors;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class QualcommImageViewHeadlessTests(NodeEditorUiFixture fixture)
{
    [Theory]
    [InlineData("en-US", 1100, "Signing metadata", "Declared anti-rollback version")]
    [InlineData("zh-CN", 480, "签名元数据", "声明的反回滚版本")]
    public async Task View_MetadataLoaded_RendersLocalizedTabAndUpdatesLanguage(
        string language, int width, string tabName, string fieldName)
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("metadata.mbn", QualcommMetadataFixture.CreateMbn(6, 0, 120));
        var token = TestContext.Current.CancellationToken;
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(async () =>
            {
                var previousLanguage = LanguageService.Instance.CurrentLanguage;
                LanguageService.Instance.ApplyLanguage(language);
                var model = new QualcommImageViewModel();
                var view = new QualcommImageView { DataContext = model };
                var window = new Window { Content = view, Width = width, Height = 650 };
                try
                {
                    await model.LoadAsync(path, token);
                    model.SelectedTabIndex = 2;
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
                    Assert.Equal(tabName, Assert.IsType<TabItem>(tabs.SelectedItem).Header);
                    Assert.Contains(model.MetadataRows, row => row.Name.EndsWith(fieldName, StringComparison.Ordinal) && row.Value == "3");
                    var grid = view.GetVisualDescendants().OfType<DataGrid>().Single(item =>
                        item.IsEffectivelyVisible && ReferenceEquals(item.ItemsSource, model.MetadataRows));
                    Assert.True(grid.Bounds.Width > 0 && grid.Bounds.Width <= width);
                    Assert.DoesNotContain(model.MetadataRows, row => row.Name.Contains("Dock_Qualcomm_", StringComparison.Ordinal));
                    LanguageService.Instance.ApplyLanguage(language == "en-US" ? "zh-CN" : "en-US");
                    Assert.Contains(model.MetadataRows, row => row.Name.EndsWith(
                        language == "en-US" ? "声明的反回滚版本" : "Declared anti-rollback version", StringComparison.Ordinal));
                }
                finally
                {
                    window.Close();
                    model.Cleanup();
                    LanguageService.Instance.ApplyLanguage(previousLanguage);
                }
            }, token);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(60), token);
    }

    [Theory]
    [InlineData("en-US", "Qualcomm image analysis")]
    [InlineData("zh-CN", "Qualcomm 镜像分析")]
    public async Task View_InitializedInLanguage_RendersLocalizedHeaderAndActions(string language, string title)
    {
        var token = TestContext.Current.CancellationToken;
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var previousLanguage = LanguageService.Instance.CurrentLanguage;
                LanguageService.Instance.ApplyLanguage(language);
                var model = new QualcommImageViewModel();
                var view = new QualcommImageView { DataContext = model };
                var window = new Window { Content = view, Width = 850, Height = 600 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                        block => block.Text == title && block.IsEffectivelyVisible);
                    Assert.Contains(view.GetVisualDescendants().OfType<Button>(),
                        button => button.IsEffectivelyVisible && button.Command == model.AnalyzeCommand);
                }
                finally
                {
                    window.Close();
                    model.Cleanup();
                    LanguageService.Instance.ApplyLanguage(previousLanguage);
                }
                return Task.CompletedTask;
            }, token);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), token);
    }
}