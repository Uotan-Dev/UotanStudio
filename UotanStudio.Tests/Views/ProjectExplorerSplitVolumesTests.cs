using Avalonia.Controls;
using Avalonia.Threading;
using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Services;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Workspace;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class ProjectExplorerSplitVolumesTests(NodeEditorUiFixture fixture)
{
    [Theory]
    [InlineData("en-US", "Split DAT / TWRP volumes", "Import as one artifact...", "Reconstruct full DAT image...")]
    [InlineData("zh-CN", "分卷 DAT / TWRP", "合并导入为单一文件...", "重建完整 DAT 镜像...")]
    public async Task ContextMenu_FileNode_ShowsLocalizedExplicitCommands(
        string language, string heading, string import, string reconstruct)
    {
        var token = TestContext.Current.CancellationToken;
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var previous = LanguageService.Instance.CurrentLanguage;
                LanguageService.Instance.ApplyLanguage(language);
                var model = new ProjectExplorerViewModel();
                var node = new ExplorerNode(Path.Combine(Path.GetTempPath(), "system.new.dat.000"), false);
                model.TreeViewContent.Add(node);
                var view = new ProjectExplorerView { DataContext = model };
                var window = new Window { Content = view, Width = 600, Height = 500 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var tree = view.FindControl<TreeView>("ProjectTree")!;
                    var container = Assert.IsType<TreeViewItem>(tree.ContainerFromItem(node));
                    var menu = Assert.IsType<ContextMenu>(container.ContextMenu);
                    menu.DataContext = node;
                    menu.Open(container);
                    var group = Assert.Single(menu.Items.OfType<MenuItem>(),
                        item => Equals(item.Header, heading));
                    Assert.True(group.IsVisible);
                    Assert.Equal([import, reconstruct], group.Items.OfType<MenuItem>()
                        .Select(item => item.Header?.ToString()).ToArray());
                    menu.Close();
                }
                finally
                {
                    window.Close();
                    model.Dispose();
                    LanguageService.Instance.ApplyLanguage(previous);
                }
                return Task.CompletedTask;
            }, token);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), token);
    }

    [Theory]
    [InlineData(DatSplitKind.NewDat, "system.new.dat.000", "system.new.dat", "system.img", true)]
    [InlineData(DatSplitKind.NewDatBrotli, "system.new.dat.br.000", "system.new.dat.br", "system.img", true)]
    [InlineData(DatSplitKind.PatchDat, "system.patch.dat.000", "system.patch.dat", "", false)]
    [InlineData(DatSplitKind.PatchDatBrotli, "system.patch.dat.br.000", "system.patch.dat.br", "", false)]
    [InlineData(DatSplitKind.TwrpTar, "data.ext4.win000", "data.ext4.tar", "", false)]
    [InlineData(DatSplitKind.TwrpImage, "boot.emmc.win000", "boot.emmc.img", "", false)]
    public void OutputPlan_InspectedKind_UsesArtifactNameAndRestrictsReconstruction(
        DatSplitKind kind, string sourceName, string artifactName, string imageName, bool reconstructable)
    {
        var set = new DatSplitSet(kind, [sourceName], 10,
            kind is DatSplitKind.TwrpTar or DatSplitKind.TwrpImage ? null : "system.transfer.list");

        Assert.Equal(artifactName, ProjectExplorerView.GetSplitOutputName(set, false));
        Assert.Equal(reconstructable, ProjectExplorerView.CanReconstructSplitDat(set));
        if (reconstructable)
        {
            Assert.Equal(imageName, ProjectExplorerView.GetSplitOutputName(set, true));
        }
    }

    [Fact]
    public async Task ExecuteSplitOperationAsync_PatchVolumes_ImportsOnlyAndPreservesImage()
    {
        var token = TestContext.Current.CancellationToken;
        using var temp = new Comm.TestDoubles.TempDirectory();
        var selected = Path.Combine(temp.Path, "system.patch.dat.000");
        await File.WriteAllBytesAsync(selected, [1, 2], token);
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "system.patch.dat.001"), [3, 4], token);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "system.transfer.list"),
            "4\n1\n0\n0\nnew 2,0,1\n", token);
        var set = UotanStudio.Services.Firmware.FirmwareService.Instance.InspectSplitDatOrTwrp(selected, token);
        var image = Path.Combine(temp.Path, "system.img");
        await File.WriteAllBytesAsync(image, [0xCA], token);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProjectExplorerView.ExecuteSplitOperationAsync(set, selected, image, true, cancellationToken: token));
        Assert.Equal([0xCA], await File.ReadAllBytesAsync(image, token));

        var artifact = Path.Combine(temp.Path, "system.patch.dat");
        var result = await ProjectExplorerView.ExecuteSplitOperationAsync(
            set, selected, artifact, false, cancellationToken: token);
        Assert.True(result.Success, result.Summary);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(artifact, token));
    }

    [Fact]
    public async Task ExecuteSplitOperationAsync_FullNewDat_ReconstructsImageAndHonorsCancellation()
    {
        var token = TestContext.Current.CancellationToken;
        using var temp = new Comm.TestDoubles.TempDirectory();
        var original = Enumerable.Range(0, DatReader.BlockSize).Select(i => (byte)i).ToArray();
        var source = Path.Combine(temp.Path, "source.img");
        await File.WriteAllBytesAsync(source, original, token);
        var data = Path.Combine(temp.Path, "system.new.dat");
        DatWriter.Write(source, data, Path.Combine(temp.Path, "system.transfer.list"), new DatWriteOptions(),
            cancellationToken: TestContext.Current.CancellationToken);
        var payload = await File.ReadAllBytesAsync(data, token);
        File.Delete(data);
        var selected = data + ".000";
        await File.WriteAllBytesAsync(selected, payload[..(payload.Length / 2)], token);
        await File.WriteAllBytesAsync(data + ".001", payload[(payload.Length / 2)..], token);
        var set = UotanStudio.Services.Firmware.FirmwareService.Instance.InspectSplitDatOrTwrp(selected, token);
        var output = Path.Combine(temp.Path, "system.img");
        await File.WriteAllBytesAsync(output, [0xCA], token);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProjectExplorerView.ExecuteSplitOperationAsync(set, selected, output, true,
                cancellationToken: canceled.Token));
        Assert.Equal([0xCA], await File.ReadAllBytesAsync(output, token));

        var result = await ProjectExplorerView.ExecuteSplitOperationAsync(
            set, selected, output, true, cancellationToken: token);
        Assert.True(result.Success, result.Summary);
        Assert.Equal(original, await File.ReadAllBytesAsync(output, token));

        await File.WriteAllBytesAsync(data + ".001", [0xAA], token);
        var failed = await ProjectExplorerView.ExecuteSplitOperationAsync(
            set, selected, output, true, cancellationToken: token);
        Assert.False(failed.Success);
        Assert.Equal(original, await File.ReadAllBytesAsync(output, token));
    }
}