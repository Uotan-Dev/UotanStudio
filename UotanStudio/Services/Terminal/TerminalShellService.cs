using CommunityToolkit.Mvvm.ComponentModel;

namespace UotanStudio.Services.Terminal;

/// <summary>
/// One selectable shell profile: a stable name used for identity, the executable and arguments, and
/// an optional locale key for the label shown in the terminal toolbar.
/// <para>一个可选的 Shell 配置：用于身份判断的稳定名称、可执行文件与参数，以及终端工具栏标签可选的
/// 语言资源键。</para>
/// </summary>
internal sealed class TerminalProfile : ObservableObject
{
    private readonly string? _displayNameKey;

    /// <summary>Creates a profile whose displayed label is the stable name unless a locale key is supplied.
    /// <para>创建配置：未提供语言资源键时，显示标签使用稳定名称。</para></summary>
    /// <param name="name">Stable profile name used for identity.<para>用于身份判断的稳定配置名称。</para></param>
    /// <param name="executable">Absolute executable path.<para>可执行文件的绝对路径。</para></param>
    /// <param name="arguments">Arguments passed to the shell.<para>传给 Shell 的参数。</para></param>
    /// <param name="displayNameKey">Optional locale resource key for the toolbar label.<para>工具栏标签可选的语言资源键。</para></param>
    internal TerminalProfile(string name, string executable, string[] arguments, string? displayNameKey = null)
    {
        Name = name;
        Executable = executable;
        Arguments = arguments;
        _displayNameKey = displayNameKey;
    }

    /// <summary>Gets the stable profile name.<para>获取稳定的配置名称。</para></summary>
    internal string Name { get; }

    /// <summary>Gets the absolute executable path.<para>获取可执行文件的绝对路径。</para></summary>
    internal string Executable { get; }

    /// <summary>Gets the arguments passed to the shell.<para>获取传给 Shell 的参数。</para></summary>
    internal string[] Arguments { get; }

    /// <summary>Gets the label shown in the terminal toolbar in the active language.<para>获取终端工具栏在当前语言下显示的标签。</para></summary>
    internal string DisplayName => _displayNameKey is null ? Name : LanguageService.Instance.GetString(_displayNameKey);

    internal void RefreshDisplayName() => OnPropertyChanged(nameof(DisplayName));
}

internal static class TerminalShellService
{
    internal static IReadOnlyList<TerminalProfile> GetProfiles()
    {
        List<TerminalProfile> profiles = [];
        void Add(string name, string? executable, params string[] arguments)
        {
            if (string.IsNullOrWhiteSpace(executable)) return;
            var path = FindExecutable(executable);
            if (path is not null && !profiles.Any(profile => profile.Executable == path))
                profiles.Add(new TerminalProfile(name, path, arguments));
        }

        if (OperatingSystem.IsWindows())
        {
            Add("PowerShell", "pwsh.exe", "-NoLogo");
            Add("Windows PowerShell", Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoLogo");
            Add("Command Prompt", Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d");
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            var loginShell = Environment.GetEnvironmentVariable("SHELL");
            if (!string.IsNullOrWhiteSpace(loginShell) && Path.IsPathFullyQualified(loginShell))
                Add(Path.GetFileName(loginShell), loginShell, "-l", "-i");
            if (OperatingSystem.IsMacOS()) Add("zsh", "/bin/zsh", "-l", "-i");
            Add("bash", "bash", "-l", "-i");
            Add("zsh", "zsh", "-l", "-i");
            Add("fish", "fish", "-l", "-i");
            Add("sh", "/bin/sh", "-l", "-i");
        }
        return profiles;
    }

    private static string? FindExecutable(string executable)
    {
        if (Path.IsPathFullyQualified(executable)) return File.Exists(executable) ? executable : null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var path = Path.Combine(directory.Trim('"'), executable);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}