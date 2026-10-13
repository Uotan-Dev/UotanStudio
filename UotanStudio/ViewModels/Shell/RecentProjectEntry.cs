using CommunityToolkit.Mvvm.Input;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// A recent-project menu entry carrying the solution path, display name and open command.
/// <para>「最近打开」菜单条目：承载解决方案路径、显示名与打开命令。</para>
/// </summary>
public sealed class RecentProjectEntry
{
    /// <summary>
    /// Full path of the solution file.
    /// <para>解决方案文件的完整路径。</para>
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Solution file name shown in the menu.
    /// <para>菜单中显示的解决方案文件名。</para>
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Command that opens the solution.
    /// <para>打开该解决方案的命令。</para>
    /// </summary>
    public IAsyncRelayCommand OpenCommand { get; }

    /// <summary>
    /// Creates a recent-project menu entry.
    /// <para>构造最近项目条目。</para>
    /// </summary>
    /// <param name="path">The full solution path. <para>解决方案完整路径。</para></param>
    /// <param name="openCommand">The command that opens the solution. <para>打开解决方案的命令。</para></param>
    public RecentProjectEntry(string path, IAsyncRelayCommand openCommand)
    {
        Path = path;
        DisplayName = System.IO.Path.GetFileName(path);
        OpenCommand = openCommand;
    }
}