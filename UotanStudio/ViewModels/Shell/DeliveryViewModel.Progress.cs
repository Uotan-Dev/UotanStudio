using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;
using UotanStudio.Services.Delivery;

namespace UotanStudio.ViewModels.Shell;

internal sealed partial class DeliveryViewModel
{
    private readonly ProgressMeter _progressMeter = new();
    private string? _progressStage;
    [ObservableProperty] private string _progressStats = "";
    public string ProgressText => IsRunning && double.IsFinite(Progress) && Progress >= 0 && Status.Length > 0
        ? string.Format(LanguageService.Instance.GetString("Progress_StatusPercent"), Status, Math.Clamp(Progress, 0, 100)) : Status;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(ProgressText));
    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ProgressText));
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(ProgressText));

    internal void ResetProgressStatistics()
    {
        _progressStage = null;
        _progressMeter.Reset();
        ProgressStats = "";
    }

    internal void ReportProgress(DeliveryProgress value)
    {
        var stage = value.Stage ?? value.Message;
        if (_progressStage != stage)
        {
            ResetProgressStatistics();
            _progressStage = stage;
            if (value.Bytes is { } bytes) _progressMeter.Report(0, bytes.Total);
            if (value.Items is { } items) _progressMeter.ReportItems(0, items.Total);
        }
        var language = LanguageService.Instance;
        Status = value.Stage is null ? value.Message : language.GetString(value.Stage)
            + (string.IsNullOrEmpty(value.Detail) ? "" : " · " + value.Detail);
        if (double.IsFinite(value.Percent)) Progress = Math.Max(Progress, Math.Clamp(value.Percent, 0, 100));
        if (value.Bytes is { } sample)
        {
            _progressMeter.Report(sample.Processed, sample.Total);
            ProgressStats = _progressMeter.FormatStats(language.GetString(sample.Total <= 0
                ? "Progress_ProcessedStats" : _progressMeter.EstimatedRemaining is null ? "Progress_StatsNoEta" : "Progress_Stats"));
        }
        else if (value.Items is { } items)
        {
            _progressMeter.ReportItems(items.Processed, items.Total);
            ProgressStats = _progressMeter.FormatStats(language.GetString(
                _progressMeter.EstimatedRemaining is null ? "Progress_ItemsStatsNoEta" : "Progress_ItemsStats"));
        }
        else ProgressStats = "";
    }
}