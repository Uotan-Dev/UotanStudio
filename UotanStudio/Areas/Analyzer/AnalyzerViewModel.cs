using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Core.Projects;
using UotanStudio.Core.Runtime;
using UotanStudio.Services;
using UotanStudio.ViewModels;

namespace UotanStudio.Areas.Analyzer;

/// <summary>
/// <para>固件分析视图模型：扫描当前工程目录中的部件文件，识别其固件格式，
/// 并允许对选中部件执行格式探测与结果展示。</para>
/// Firmware analyzer view model: scans the current project directory for component
/// files, detects their firmware format, and lets the user probe a selected component.
/// </summary>
public partial class AnalyzerViewModel : ViewModelBase
{
    private readonly StudioRuntime _runtime = new();

    /// <summary>
    /// <para>获取工程中的部件文件集合。</para>
    /// Gets the component files in the project.
    /// </summary>
    public ObservableCollection<AnalyzerComponent> Components { get; } = [];

    /// <summary>
    /// <para>获取或设置当前选中部件。</para>
    /// Gets or sets the currently selected component.
    /// </summary>
    public AnalyzerComponent? SelectedComponent
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                // 选中变化时清空上一次的探测摘要
                AnalysisSummary = string.Empty;
            }
        }
    }

    /// <summary>
    /// <para>获取或设置分析摘要文本。</para>
    /// Gets or sets the analysis summary text.
    /// </summary>
    [ObservableProperty]
    private string _analysisSummary = string.Empty;

    /// <summary>
    /// <para>获取或设置是否扫描中。</para>
    /// Gets or sets whether a scan is in progress.
    /// </summary>
    [ObservableProperty]
    private bool _isScanning;

    /// <summary>
    /// <para>重新扫描当前工程目录的部件文件。</para>
    /// Re-scans the current project directory for component files.
    /// </summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        var root = FirmwareProjectService.OpenPath is null
            ? null
            : FirmwareProjectService.LoadProject(FirmwareProjectService.OpenPath)?.RootDirectory;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AnalysisSummary = LanguageService.Instance.GetString("Analyzer_NoProject");
            return;
        }

        IsScanning = true;
        try
        {
            Components.Clear();
            await Task.Run(() =>
            {
                // 常见固件文件后缀，用于筛选目录中的部件
                var suffixes = new[] { ".img", ".bin", ".payload", ".fb", ".ozip", ".ofp", ".ops", ".kdz", ".dz", ".nb0", ".pac", ".cpio" };
                foreach (var file in Directory.EnumerateFiles(root, "*",
                             SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (suffixes.Any(suffix => ext == suffix))
                    {
                        var handler = _runtime.DetectFormat(file);
                        Components.Add(new AnalyzerComponent(file, handler?.FormatName));
                    }
                }
            });
            AnalysisSummary = string.Format(
                LanguageService.Instance.GetString("Analyzer_ScanSummary"), Components.Count);
            LogService.Instance.Write(LogLevel.Info, "Analyzer",
                $"Scanned project, found {Components.Count} component(s).");
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// <para>探测当前选中部件的格式并展示摘要。</para>
    /// Probes the format of the selected component and shows a summary.
    /// </summary>
    [RelayCommand]
    private async Task ProbeAsync()
    {
        if (SelectedComponent is null)
        {
            return;
        }

        var result = await _runtime.DescribeFormatAsync(SelectedComponent.FullPath);
        AnalysisSummary = result.Summary;
        LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Warn, "Analyzer",
            $"{Path.GetFileName(SelectedComponent.FullPath)}: {result.Summary}");
    }
}

/// <summary>
/// <para>部件文件模型，含路径与已识别的格式名。</para>
/// Component file model, carrying the path and the detected format name.
/// </summary>
public sealed class AnalyzerComponent(string fullPath, string? formatName) : ViewModelBase
{
    /// <summary>文件完整路径。 Full file path.</summary>
    public string FullPath { get; } = fullPath;

    /// <summary>显示名称（文件名）。 Display name (file name).</summary>
    public string Name => Path.GetFileName(FullPath);

    /// <summary>识别到的格式名；未知为非空缺省。 Detected format name; fallback when unknown.</summary>
    public string FormatName => string.IsNullOrEmpty(formatName)
        ? LanguageService.Instance.GetString("Analyzer_Unknown")
        : formatName;
}