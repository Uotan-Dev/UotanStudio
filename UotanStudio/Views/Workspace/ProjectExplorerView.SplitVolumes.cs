using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.ViewModels.Workspace;
using Ursa.Controls;

namespace UotanStudio.Views.Workspace;

public partial class ProjectExplorerView
{
    private bool _splitOperationRunning;

    private void OnImportSplitVolumes(object? sender, RoutedEventArgs e)
        => _ = RunSplitVolumesAsync(sender, reconstruct: false);

    private void OnReconstructSplitDat(object? sender, RoutedEventArgs e)
        => _ = RunSplitVolumesAsync(sender, reconstruct: true);

    private async Task RunSplitVolumesAsync(object? sender, bool reconstruct)
    {
        if (_splitOperationRunning || sender is not MenuItem item ||
            (item.DataContext as ExplorerNode ?? ProjectTree.SelectedItem as ExplorerNode) is not
            { IsDirectory: false, IsPlaceholder: false } node ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        _splitOperationRunning = true;
        var language = LanguageService.Instance;
        var title = language.GetString(reconstruct ? "Explorer_SplitReconstruct" : "Explorer_SplitImport");
        try
        {
            var set = await Task.Run(() => FirmwareService.Instance.InspectSplitDatOrTwrp(node.Path));
            if (reconstruct && !CanReconstructSplitDat(set))
            {
                throw new InvalidOperationException(language.GetString("Explorer_SplitRequiresNewDat"));
            }
            if (set.Kind is DatSplitKind.NewDat or DatSplitKind.NewDatBrotli or
                DatSplitKind.PatchDat or DatSplitKind.PatchDatBrotli && set.CompanionTransferList is null)
            {
                throw new InvalidOperationException(language.GetString("Explorer_SplitRequiresTransferList"));
            }

            var outputName = GetSplitOutputName(set, reconstruct);
            var target = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = outputName
            });
            if (target?.TryGetLocalPath() is not { } outputPath)
            {
                return;
            }
            if (File.Exists(outputPath) && !await DialogService.ConfirmAsync(owner, title,
                    string.Format(language.GetString("Explorer_SplitOverwrite"), outputPath)))
            {
                return;
            }

            using var cancellation = new CancellationTokenSource();
            var label = new TextBlock { Text = language.GetString("Explorer_SplitWorking") };
            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 18,
                IsIndeterminate = reconstruct || set.Kind is DatSplitKind.TwrpTar
            };
            var cancel = new Button
            {
                Content = language.GetString("Editor_Cancel"),
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = 88
            };
            cancel.Click += (_, _) => cancellation.Cancel();
            var panel = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
            panel.Children.Add(label);
            panel.Children.Add(bar);
            panel.Children.Add(cancel);
            var progressWindow = new Window
            {
                Title = title,
                Width = 390,
                Height = 150,
                MinWidth = 320,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = panel
            };
            progressWindow.Closing += (_, _) => cancellation.Cancel();
            var acceptingProgress = true;
            var byteProgress = new Progress<(long Processed, long Total)>(value =>
            {
                if (!acceptingProgress || cancellation.IsCancellationRequested)
                {
                    return;
                }
                bar.Value = value.Total > 0 ? Math.Clamp(100d * value.Processed / value.Total, 0, 100) : 0;
                label.Text = string.Format(language.GetString("Explorer_SplitBytes"), value.Processed, value.Total);
            });
            var fractionProgress = new Progress<double>(value =>
            {
                if (!acceptingProgress || cancellation.IsCancellationRequested)
                {
                    return;
                }
                bar.IsIndeterminate = false;
                bar.Value = Math.Clamp(value * 100d, 0, 100);
                label.Text = string.Format(language.GetString("Explorer_SplitPercent"), value);
            });

            progressWindow.Show(owner);
            FirmwareOpResult result;
            try
            {
                result = await ExecuteSplitOperationAsync(set, node.Path, outputPath, reconstruct,
                    byteProgress, fractionProgress, cancellation.Token);
            }
            finally
            {
                acceptingProgress = false;
                progressWindow.Close();
            }

            if (!result.Success)
            {
                throw new InvalidOperationException(result.Summary);
            }

            if (DataContext is ProjectExplorerViewModel model)
            {
                RefreshSplitOutput(model, outputPath);
            }
            await DialogService.ShowAsync(owner, title,
                string.Format(language.GetString("Explorer_SplitDone"), outputPath), MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            await DialogService.ShowAsync(owner, title, language.GetString("Explorer_SplitCancelled"),
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Explorer", ex.Message);
            await DialogService.ShowAsync(owner, title, UserError.Describe(ex),
                MessageBoxIcon.Error);
        }
        finally
        {
            _splitOperationRunning = false;
        }
    }

    internal static bool CanReconstructSplitDat(DatSplitSet set) =>
        set.Kind is DatSplitKind.NewDat or DatSplitKind.NewDatBrotli &&
        set.CompanionTransferList is not null;

    internal static string GetSplitOutputName(DatSplitSet set, bool reconstruct)
    {
        var fileName = Path.GetFileName(set.Parts[0]);
        var stem = set.Kind is DatSplitKind.TwrpTar or DatSplitKind.TwrpImage
            ? fileName[..^3]
            : Path.GetFileNameWithoutExtension(fileName);
        if (reconstruct)
        {
            var marker = stem.IndexOf(".new.dat", StringComparison.OrdinalIgnoreCase);
            return stem[..marker] + ".img";
        }
        return set.Kind switch
        {
            DatSplitKind.TwrpTar => stem[..^3] + "tar",
            DatSplitKind.TwrpImage => stem[..^3] + "img",
            _ => stem
        };
    }

    internal static Task<FirmwareOpResult> ExecuteSplitOperationAsync(
        DatSplitSet set, string selectedPath, string outputPath, bool reconstruct,
        IProgress<(long Processed, long Total)>? byteProgress = null,
        IProgress<double>? fractionProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (reconstruct)
        {
            if (!CanReconstructSplitDat(set))
            {
                throw new InvalidOperationException(LanguageService.Instance.GetString("Explorer_SplitRequiresNewDat"));
            }
            return FirmwareService.Instance.ReconstructSplitDatImageAsync(
                selectedPath, outputPath, fractionProgress, cancellationToken);
        }
        return FirmwareService.Instance.ImportSplitDatOrTwrpAsync(
            selectedPath, outputPath, byteProgress, cancellationToken);
    }

    private static void RefreshSplitOutput(ProjectExplorerViewModel model, string outputPath)
    {
        var parentPath = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (parentPath is null)
        {
            return;
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in model.TreeViewContent)
        {
            RefreshNode(root, parentPath, comparison);
        }
    }

    private static void RefreshNode(ExplorerNode node, string parentPath, StringComparison comparison)
    {
        if (!node.IsDirectory)
        {
            return;
        }
        if (string.Equals(node.Path, parentPath, comparison))
        {
            node.RefreshChildren();
            return;
        }
        foreach (var child in node.Children)
        {
            RefreshNode(child, parentPath, comparison);
        }
    }
}