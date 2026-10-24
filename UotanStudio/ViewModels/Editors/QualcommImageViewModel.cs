using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.Executable.Qualcomm;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

/// <summary>Read-only Qualcomm image workspace with explicit trust input and cancellable analysis.
/// <para>带显式信任输入及可取消分析的只读 Qualcomm 镜像工作区。</para></summary>
public partial class QualcommImageViewModel : Document, ITool
{
    private readonly IQualcommWorkspaceAnalyzer _analyzer;
    private CancellationTokenSource? _analysisCancellation;
    private QualcommWorkspaceReport? _report;
    private bool _closed;
    private string _statusKey = "Idle";
    private string? _errorKey;

    /// <summary>Creates a workspace document using the firmware backend.
    /// <para>使用固件后端创建工作区文档。</para></summary>
    public QualcommImageViewModel() : this(new QualcommWorkspaceAnalyzer()) { }

    internal QualcommImageViewModel(IQualcommWorkspaceAnalyzer analyzer)
    {
        _analyzer = analyzer;
        _status = Label(_statusKey);
        LanguageService.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Selected local image path. <para>选中的本地镜像路径。</para></summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>Optional trust anchor path.<para>可选的可信根路径。</para></summary>
    [ObservableProperty] private string? _trustedRootPath;

    /// <summary>Whether analysis is in progress. <para>分析是否正在进行。</para></summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Whether a current report can be shown or exported. <para>当前结果是否可展示和导出。</para></summary>
    [ObservableProperty] private bool _hasResults;

    /// <summary>Localized high-level state. <para>本地化的概要状态。</para></summary>
    [ObservableProperty] private string _status;

    /// <summary>Localized user-visible error. <para>本地化的用户可见错误。</para></summary>
    [ObservableProperty] private string _errorMessage = string.Empty;

    /// <summary>Whether the error banner is visible. <para>错误提示是否可见。</para></summary>
    /// <value>Whether a localized error message is present.<para>是否存在本地化的错误信息。</para></value>
    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>Overview rows. <para>概览行。</para></summary>
    [ObservableProperty] private IReadOnlyList<QualcommRow> _overviewRows = [];

    /// <summary>Program header and segment rows. <para>程序头与段信息行。</para></summary>
    [ObservableProperty] private IReadOnlyList<QualcommRow> _segmentRows = [];

    /// <summary>Signature and trust rows. <para>签名和信任状态行。</para></summary>
    [ObservableProperty] private IReadOnlyList<QualcommRow> _verificationRows = [];

    /// <summary>Offline programmer command findings. <para>离线 programmer 命令线索。</para></summary>
    [ObservableProperty] private IReadOnlyList<QualcommRow> _programmerRows = [];

    [ObservableProperty] private IReadOnlyList<QualcommRow> _metadataRows = [];

    /// <summary>Current result tab index. <para>当前结果页签索引。</para></summary>
    [ObservableProperty] private int _selectedTabIndex;

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnTrustedRootPathChanged(string? value)
    {
        _analysisCancellation?.Cancel();
        ClearReport();
        _errorKey = null;
        ErrorMessage = string.Empty;
        _statusKey = "Idle";
        Status = Label(_statusKey);
    }

    /// <summary>Loads an image without retaining file handles after analysis.
    /// <para>加载镜像，分析结束后不保留文件句柄。</para></summary>
    /// <param name="path">Existing local image path. <para>现有本地镜像路径。</para></param>
    /// <param name="cancellationToken">Optional cancellation. <para>可选的取消令牌。</para></param>
    public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_closed)
        {
            return;
        }

        _analysisCancellation?.Cancel();
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _analysisCancellation = source;
        FilePath = path;
        ClearReport();
        _errorKey = null;
        ErrorMessage = string.Empty;
        _statusKey = "Loading";
        Status = Label(_statusKey);
        IsBusy = true;
        try
        {
            var report = await _analyzer.AnalyzeAsync(path, TrustedRootPath, source.Token);
            if (!ReferenceEquals(_analysisCancellation, source) || _closed || source.IsCancellationRequested)
            {
                return;
            }

            _report = report;
            HasResults = true;
            PublishRows();
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            if (ReferenceEquals(_analysisCancellation, source) && !_closed)
            {
                _statusKey = "Idle";
                Status = Label(_statusKey);
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_analysisCancellation, source) && !_closed)
            {
                _errorKey = "LoadError";
                ErrorMessage = Label(_errorKey);
                _statusKey = "Idle";
                Status = Label(_statusKey);
                LogService.Instance.Write(LogLevel.Error, "Qualcomm", $"Failed to analyze '{path}': {ex.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(_analysisCancellation, source))
            {
                _analysisCancellation = null;
                if (!_closed)
                {
                    IsBusy = false;
                }
            }
            source.Dispose();
        }
    }

    [RelayCommand]
    private Task AnalyzeAsync() => FilePath is { Length: > 0 } path ? LoadAsync(path) : Task.CompletedTask;

    [RelayCommand]
    private void Cancel()
    {
        _analysisCancellation?.Cancel();
    }

    /// <summary>Atomically exports the current structured offline report as JSON.
    /// <para>将当前结构化离线报告原子导出为 JSON。</para></summary>
    /// <param name="path">Destination path. <para>目标路径。</para></param>
    /// <param name="cancellationToken">Cancels writing before publication. <para>发布前取消写入。</para></param>
    public async Task ExportAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_closed || _report is not { } report)
        {
            return;
        }

        string? temporary = null;
        try
        {
            var target = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(target)!;
            temporary = TempFileHelper.CreateAdjacentPath(target);
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, report,
                    QualcommReportJsonContext.Default.QualcommWorkspaceReport, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_closed || !ReferenceEquals(_report, report))
            {
                return;
            }
            File.Move(temporary, target, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _errorKey = "ExportError";
                ErrorMessage = Label(_errorKey);
                LogService.Instance.Write(LogLevel.Error, "Qualcomm", $"Failed to export '{path}': {ex.Message}");
            }
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    TempFileHelper.SafeDelete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogService.Instance.Write(LogLevel.Error, "Qualcomm", $"Failed to remove temporary report: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Stops active work and releases language subscriptions.
    /// <para>停止正在进行的工作并释放语言事件订阅。</para></summary>
    public void Cleanup()
    {
        _closed = true;
        _analysisCancellation?.Cancel();
        LanguageService.Instance.LanguageChanged -= OnLanguageChanged;
        ClearReport();
    }

    private void ClearReport()
    {
        _report = null;
        HasResults = false;
        OverviewRows = [];
        SegmentRows = [];
        VerificationRows = [];
        ProgrammerRows = [];
        MetadataRows = [];
    }

    private void PublishRows()
    {
        if (_report is not { } report)
        {
            return;
        }
        OverviewRows = Translate(report.Overview);
        SegmentRows = Translate(report.Segments);
        VerificationRows = Translate(report.Verification);
        ProgrammerRows = Translate(report.Programmer);
        MetadataRows = Translate(report.Metadata);
        _statusKey = report.StatusKey;
        Status = Label(_statusKey);
    }

    private static IReadOnlyList<QualcommRow> Translate(IReadOnlyList<QualcommReportField> source)
        => [.. source.Select(static row => new QualcommRow(
            (row.Group is null ? "" : row.Group + " / ") + (row.LocalizedName ? Label(row.Name) : row.Name),
            row.LocalizedValue ? Label(row.Value) : row.Value))];

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Status = Label(_statusKey);
        if (_errorKey is not null)
        {
            ErrorMessage = Label(_errorKey);
        }
        PublishRows();
    }

    private static string Label(string key) => LanguageService.Instance.GetString($"Dock_Qualcomm_{key}");
}

/// <summary>One rendered name/value pair. <para>一个展示用的名称/值对。</para></summary>
/// <param name="Name">Rendered row label. <para>展示用的行标签。</para></param>
/// <param name="Value">Rendered row value. <para>展示用的行值。</para></param>
public sealed record QualcommRow(string Name, string Value);

internal sealed record QualcommReportField(string Name, string Value, bool LocalizedName = false, bool LocalizedValue = false,
    string? Group = null);

internal sealed record QualcommWorkspaceReport(
    string StatusKey,
    IReadOnlyList<QualcommReportField> Overview,
    IReadOnlyList<QualcommReportField> Segments,
    IReadOnlyList<QualcommReportField> Verification,
    IReadOnlyList<QualcommReportField> Programmer)
{
    public IReadOnlyList<QualcommReportField> Metadata { get; init; } = [];
    public QualcommAnalysisResult? Analysis { get; init; }
}

internal interface IQualcommWorkspaceAnalyzer
{
    Task<QualcommWorkspaceReport> AnalyzeAsync(string imagePath, string? trustedRootPath, CancellationToken cancellationToken);
}

[JsonSerializable(typeof(QualcommWorkspaceReport))]
internal partial class QualcommReportJsonContext : JsonSerializerContext;