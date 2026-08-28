using CommunityToolkit.Mvvm.Input;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// <para>「最近打开」菜单条目：承载解决方案路径、显示名与打开命令。</para>
/// A recent-project menu entry carrying the solution path, display name and open command.
/// </summary>
public sealed class RecentProjectEntry
{
    /// <summary>
    /// <para>解决方案文件的完整路径。</para>
    /// Full path of the solution file.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// <para>菜单中显示的解决方案文件名。</para>
    /// Solution file name shown in the menu.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// <para>打开该解决方案的命令。</para>
    /// Command that opens the solution.
    /// </summary>
    public IAsyncRelayCommand OpenCommand { get; }

    /// <summary>
    /// <para>构造最近项目条目。</para>
    /// Creates a recent-project menu entry.
    /// </summary>
    /// <param name="path"><para>解决方案完整路径。</para>The full solution path.</param>
    /// <param name="openCommand"><para>打开解决方案的命令。</para>The command that opens the solution.</param>
    public RecentProjectEntry(string path, IAsyncRelayCommand openCommand)
    {
        Path = path;
        DisplayName = System.IO.Path.GetFileName(path);
        OpenCommand = openCommand;
    }
}