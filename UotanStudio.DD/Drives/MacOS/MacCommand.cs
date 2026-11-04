using System.ComponentModel;
using System.Diagnostics;

namespace UotanStudio.DD.Drives.MacOS;

/// <summary>
/// Runs short macOS helper commands and captures their output.
/// <para>运行简短的 macOS 辅助命令并捕获其输出。</para>
/// </summary>
internal static class MacCommand
{
    /// <summary>
    /// Runs an executable to completion.
    /// <para>运行可执行文件直至结束。</para>
    /// </summary>
    /// <param name="executable">Absolute executable path.<para>可执行文件绝对路径。</para></param>
    /// <param name="arguments">Arguments passed verbatim.<para>原样传入的参数。</para></param>
    /// <param name="cancellationToken">Kills the process when cancelled.<para>取消时终止进程。</para></param>
    /// <returns>Standard output text.<para>标准输出文本。</para></returns>
    /// <exception cref="FlashException">The executable is missing or exits with a non-zero code.
    /// <para>可执行文件缺失或以非零码退出。</para></exception>
    internal static async Task<string> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new FlashException($"macOS could not start \"{executable}\".", exception);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new FlashException(
                $"\"{executable} {string.Join(' ', arguments)}\" failed with exit code {process.ExitCode}: {error.Trim()}");
        }

        return output;
    }
}