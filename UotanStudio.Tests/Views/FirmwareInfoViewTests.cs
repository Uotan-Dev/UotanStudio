using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.Services;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Views.Controls;
using UotanStudio.Views.Editors;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class FirmwareInfoViewTests(NodeEditorUiFixture fixture)
{
    [Theory]
    [InlineData(360)]
    [InlineData(1000)]
    public async Task LoadAsync_MetadataPanel_LocalizesAndWrapsSelectableValues(int width)
    {
        using var temp = new TempDirectory();
        var bytes = new byte[256 + 24];
        "AVB0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(20), 24);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(104), 24);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(256), 99);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(264), 8);
        var path = temp.WriteBytes("vbmeta.img", bytes);
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.Session.Dispatch(async () =>
            {
                var language = LanguageService.Instance;
                var originalLanguage = language.CurrentLanguage;
                language.ApplyLanguage("en-US");
                var document = new FirmwareInfoViewModel();
                var view = new FirmwareInfoView { DataContext = document };
                var window = new Window { Content = view, Width = width, Height = 750 };
                try
                {
                    await document.LoadAsync(path, cancellationToken: TestContext.Current.CancellationToken);
                    Assert.False(document.HasError, document.ErrorMessage);
                    Assert.Contains(document.MetadataGroups, group => group.Name == "Descriptor 1");
                    var originalValues = document.Metadata.Select(row => row.Value).ToArray();
                    language.ApplyLanguage("zh-CN");
                    Assert.Contains(document.MetadataGroups, group => group.Name == "描述符 1");
                    Assert.Contains(document.Metadata, row => row.Name == "签名算法" && row.Value == "NONE");
                    Assert.Contains(document.Metadata, row => row.Name == "验证状态" && row.Value.Contains("未执行", StringComparison.Ordinal));
                    Assert.DoesNotContain(document.Metadata, row => row.Name.StartsWith("FirmwareField_", StringComparison.Ordinal));

                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var rows = view.GetVisualDescendants().OfType<ResponsiveFormRow>().ToArray();
                    Assert.NotEmpty(rows);
                    foreach (var row in rows)
                    {
                        var value = Assert.IsType<SelectableTextBlock>(row.Children[1]);
                        Assert.True(value.Bounds.Width > 0);
                        Assert.True(value.Bounds.Right <= row.Bounds.Width + 1);
                        if (width == 360)
                        {
                            Assert.True(value.Bounds.Top >= row.Children[0].Bounds.Bottom);
                        }
                    }
                    language.ApplyLanguage("en-US");
                    Assert.Equal(originalValues, document.Metadata.Select(row => row.Value));
                    await document.LoadAsync(temp.WriteBytes("invalid.img", new byte[256]), cancellationToken: TestContext.Current.CancellationToken);
                    Assert.True(document.HasError);
                    Assert.Empty(document.MetadataGroups);
                    Assert.Empty(document.Metadata);
                }
                finally
                {
                    document.Cleanup();
                    window.Close();
                    language.ApplyLanguage(originalLanguage);
                }
            }, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cleanup_DuringLoad_DoesNotRepopulateClosedDocument()
    {
        using var temp = new TempDirectory();
        var bytes = new byte[256];
        "AVB0"u8.CopyTo(bytes);
        var document = new FirmwareInfoViewModel();
        var loading = document.LoadAsync(temp.WriteBytes("vbmeta.img", bytes), cancellationToken: TestContext.Current.CancellationToken);
        document.Cleanup();

        await loading;

        Assert.Empty(document.Metadata);
        Assert.Empty(document.MetadataGroups);
        Assert.False(document.IsBusy);
        Assert.False(document.IsRecognized);
        Assert.False(document.HasError);
    }
}