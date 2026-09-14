using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using UotanStudio.Services;
using UotanStudio.Services.Logging;

namespace UotanStudio.ViewModels;

internal interface ILogViewModel : INotifyPropertyChanged
{
    Workspace.AdbLogViewModel? LiveModel { get; }
    Editors.LogEditorViewModel? FileModel { get; }
    IReadOnlyList<ILogLine> Entries { get; }
    event NotifyCollectionChangedEventHandler? EntriesChanged;
    IReadOnlyList<LogLevel?> Levels { get; }
    int SelectedLevelIndex { get; set; }
    string SearchText { get; set; }
    bool UseRegex { get; set; }
    bool MatchCase { get; set; }
    string? SearchError { get; }
    bool HasSearchError { get; }
    ICommand ClearSearchCommand { get; }
    bool AutoScroll { get; set; }
    bool IsPaused { get; }
    string? ErrorMessage { get; set; }
}