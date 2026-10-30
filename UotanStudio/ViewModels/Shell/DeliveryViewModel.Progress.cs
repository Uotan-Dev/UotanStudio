using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Delivery;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Shell;

internal sealed partial class DeliveryViewModel
{
    private readonly ProgressMeter _progressMeter = new();
    private string? _progressStage;
    [ObservableProperty] private string _progressStats = "";
    internal event EventHandler<string>? NoticeRequested;
    public string ProgressText => HasLineBreak(Status) ? string.Empty : IsRunning && double.IsFinite(Progress) && Progress >= 0 && Status.Length > 0
        ? string.Format(LanguageService.Instance.GetString("Progress_StatusPercent"), Status, Math.Clamp(Progress, 0, 100)) : Status;

    private static bool HasLineBreak(string value) => value.IndexOfAny(['\r', '\n', '\u0085', '\u2028', '\u2029']) >= 0;

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(ProgressText));
        if (HasLineBreak(value))
        {
            NoticeRequested?.Invoke(this, value);
        }
    }
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
        var detail = value.Stage == "Delivery_ProgressSparse" && !HasLineBreak(value.Detail ?? string.Empty)
            ? null : value.Detail;
        Status = language.GetString(value.Stage ?? "Delivery_OutputProgress")
            + (string.IsNullOrEmpty(detail) ? "" : " · " + detail);
        if (HasLineBreak(value.Message) && !HasLineBreak(detail ?? string.Empty))
        {
            NoticeRequested?.Invoke(this, value.Message);
        }
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