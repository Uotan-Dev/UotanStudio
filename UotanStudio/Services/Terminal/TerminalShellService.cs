namespace UotanStudio.Services.Terminal;

internal sealed record TerminalProfile(string Name, string Executable, string[] Arguments);

internal static class TerminalShellService
{
    internal static IReadOnlyList<TerminalProfile> GetProfiles()
    {
        var profiles = new List<TerminalProfile>();
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
