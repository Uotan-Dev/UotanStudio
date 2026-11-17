using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace UotanStudio.Services;

/// <summary>
/// Relaunches the application with <c>runas</c> so file associations are registered in an elevated process,
/// then reads the structured outcome the helper wrote to a temporary result file.
/// <para>以 <c>runas</c> 重启应用，使文件关联在提权进程中注册，然后读取助手写入临时结果文件的结构化结果。</para>
/// </summary>
public static class FileAssociationElevationRunner
{
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan ElevatedTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs the elevated helper for the given extensions and returns its outcome.
    /// <para>针对给定扩展名运行提权助手，并返回其结果。</para>
    /// </summary>
    /// <param name="extensions">Selected extensions to register.<para>待注册的选定扩展名。</para></param>
    /// <returns>The helper's result, or a failure result describing why elevation could not complete.
    /// <para>助手的结果；提权未能完成时返回描述原因的失败结果。</para></returns>
    public static FileAssociationResult Run(IReadOnlyList<string> extensions)
    {
        if (!OperatingSystem.IsWindows())
        {
            return FileAssociationResult.Failed("Elevation is only available on Windows.");
        }

        return RunWindows(extensions);
    }

    [SupportedOSPlatform("windows")]
    private static FileAssociationResult RunWindows(IReadOnlyList<string> extensions)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath) ||
            string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return FileAssociationResult.Failed(
                "The application cannot relaunch itself with administrator rights; start it as administrator and retry.");
        }

        var resultPath = Path.Combine(Path.GetTempPath(), "UotanStudio.FileAssociation",
            Path.GetRandomFileName() + ".result");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            startInfo.ArgumentList.Add(FileAssociationElevatedCommand.RegisterSwitch);
            startInfo.ArgumentList.Add(FileAssociationElevatedCommand.ResultSwitch);
            startInfo.ArgumentList.Add(resultPath);
            foreach (var extension in extensions)
            {
                startInfo.ArgumentList.Add(extension);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return FileAssociationResult.Failed("The elevated helper could not be started.");
            }

            if (!process.WaitForExit((int)ElevatedTimeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
                return FileAssociationResult.Failed(
                    $"The elevated helper did not finish within {ElevatedTimeout.TotalSeconds:0} seconds and was terminated.");
            }

            return FileAssociationElevatedCommand.ReadResult(resultPath)
                ?? (process.ExitCode == 0
                    ? FileAssociationResult.Succeeded(extensions.Count)
                    : FileAssociationResult.Failed(
                        $"The elevated helper exited with code {process.ExitCode} without reporting a result."));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return FileAssociationResult.Failed("The administrator prompt was cancelled; associations were not changed.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
            or UnauthorizedAccessException)
        {
            return FileAssociationResult.Failed($"The elevated helper could not complete: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(resultPath))
                {
                    File.Delete(resultPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}