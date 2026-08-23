using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.ViewModels;

namespace UotanStudio.Areas.Output;

/// <summary>
/// <para>输出通道枚举。</para>
/// Output channel enum.
/// </summary>
public enum OutputChannel
{
    /// <summary>构建。 Build.</summary>
    Build,
    /// <summary>烧录。 Flash.</summary>
    Flash,
    /// <summary>设备。 Device.</summary>
    Device,
    /// <summary>全部。 All.</summary>
    All
}

/// <summary>
/// <para>分通道输出视图模型：订阅统一日志服务并按通道（构建/烧录/设备/全部）隔离与显示。</para>
/// Channeled output view model: subscribes to the unified log service and isolates
/// entries by channel (Build/Flash/Device/All).
/// </summary>
public partial class OutputViewModel : ViewModelBase, IDisposable
{
    private const int MaxEntries = 2000;
    private readonly Action? _unsubscribe;

    /// <summary>
    /// <para>构建通道日志条目。</para>
    /// Build channel log entries.
    /// </summary>
    public ObservableCollection<OutputEntry> BuildEntries { get; } = [];

    /// <summary>
    /// <para>烧录通道日志条目。</para>
    /// Flash channel log entries.
    /// </summary>
    public ObservableCollection<OutputEntry> FlashEntries { get; } = [];

    /// <summary>
    /// <para>设备通道日志条目。</para>
    /// Device channel log entries.
    /// </summary>
    public ObservableCollection<OutputEntry> DeviceEntries { get; } = [];

    /// <summary>
    /// <para>全部通道日志条目。</para>
    /// All-channel log entries.
    /// </summary>
    public ObservableCollection<OutputEntry> AllEntries { get; } = [];

    /// <summary>
    /// <para>获取当前选中通道。</para>
    /// Gets the currently selected channel.
    /// </summary>
    [ObservableProperty]
    private OutputChannel _selectedChannel = OutputChannel.All;

    partial void OnSelectedChannelChanged(OutputChannel value)
    {
        OnPropertyChanged(nameof(ActiveEntries));
    }

    /// <summary>
    /// <para>获取当前选中通道对应的条目集合。</para>
    /// Gets the entry collection for the currently selected channel.
    /// </summary>
    public ObservableCollection<OutputEntry> ActiveEntries => SelectedChannel switch
    {
        OutputChannel.Build => BuildEntries,
        OutputChannel.Flash => FlashEntries,
        OutputChannel.Device => DeviceEntries,
        _ => AllEntries
    };

    /// <summary>
    /// <para>初始化输出视图模型并订阅日志服务。</para>
    /// Initializes the output view model and subscribes to the log service.
    /// </summary>
    public OutputViewModel()
    {
        foreach (var entry in LogService.Instance.Entries)
        {
            AddEntry(new OutputEntry(entry.Timestamp, entry.Level, entry.Source, entry.Message));
        }

        _unsubscribe = LogService.Instance.Subscribe((_, entry) =>
        {
            var outEntry = new OutputEntry(entry.Timestamp, entry.Level, entry.Source, entry.Message);
            Dispatcher.UIThread.Post(() => AddEntry(outEntry));
        });
    }

    /// <summary>
    /// <para>清空所有通道的日志。</para>
    /// Clears all channels' logs.
    /// </summary>
    [RelayCommand]
    private void ClearAll()
    {
        BuildEntries.Clear();
        FlashEntries.Clear();
        DeviceEntries.Clear();
        AllEntries.Clear();
        LogService.Instance.Clear();
    }

    private void AddEntry(OutputEntry entry)
    {
        AddCapped(AllEntries, entry);
        switch (entry.Source)
        {
            case "Build":
            case "BuildService":
            case "FirmwareService":
                AddCapped(BuildEntries, entry);
                break;
            case "Flash":
            case "FlashService":
            case "DeviceManager":
                AddCapped(FlashEntries, entry);
                break;
            case "Device":
            case "DeviceMonitor":
            case "Usb":
                AddCapped(DeviceEntries, entry);
                break;
            default:
                break;
        }
    }

    private static void AddCapped(ObservableCollection<OutputEntry> list, OutputEntry entry)
    {
        list.Add(entry);
        while (list.Count > MaxEntries)
        {
            list.RemoveAt(0);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _unsubscribe?.Invoke();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// <para>输出日志条目模型。</para>
/// Output log entry model.
/// </summary>
/// <remarks>
/// <para>初始化输出日志条目。</para>
/// Initializes an output log entry.
/// </remarks>
public sealed class OutputEntry(DateTime timestamp, LogLevel level, string source, string message)
{

    /// <summary>时间戳。 Timestamp.</summary>
    public DateTime Timestamp { get; } = timestamp;

    /// <summary>日志级别。 Log level.</summary>
    public LogLevel Level { get; } = level;

    /// <summary>来源。 Source.</summary>
    public string Source { get; } = source;

    /// <summary>文本。 Message text.</summary>
    public string Message { get; } = message;

    /// <summary>紧凑显示文本（HH:mm:ss 来源 消息）。 Dense display text.</summary>
    public string Line => $"{Timestamp:HH:mm:ss} [{Source}] {Message}";
}