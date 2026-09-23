using System.Diagnostics;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Native;

/// <summary>Runs installed filesystem tools without a shell; Windows callers may select a WSL distribution.</summary>
public sealed class NativeFileSystemTools(string? wslDistribution = null)
{
    private const int MaximumOutputChars = 16 * 1024;

    public string PathForTool(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        if (string.IsNullOrWhiteSpace(wslDistribution))
        {
            throw new PlatformNotSupportedException("Native filesystem tools on Windows require an explicit WSL distribution.");
        }

        var result = RunAsync("wslpath", [fullPath.Replace('\\', '/')], cancellationToken).GetAwaiter().GetResult();
        RequireSuccess("wslpath", result);
        var translated = result.Output.Trim();
        if (!translated.StartsWith('/'))
        {
            throw new IOException("WSL did not return an absolute path.");
        }

        return translated;
    }

    public async Task<NativeToolResult> RunAsync(string tool, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(wslDistribution))
        {
            throw new PlatformNotSupportedException("Native filesystem tools on Windows require an explicit WSL distribution.");
        }

        var info = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "wsl.exe" : tool,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            info.ArgumentList.Add("-d");
            info.ArgumentList.Add(wslDistribution!);
            info.ArgumentList.Add("--");
            info.ArgumentList.Add(tool);
        }
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        if (!process.Start())
        {
            throw new IOException($"Could not start {tool}.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var output = new StringBuilder();
        var stdout = CaptureAsync(process.StandardOutput, output, cancellationToken);
        var stderr = CaptureAsync(process.StandardError, output, cancellationToken);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new NativeToolResult(process.ExitCode, output.ToString());
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public static void RequireSuccess(string tool, NativeToolResult result)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidDataException($"{tool} exited with code {result.ExitCode}: {result.Output}");
        }
    }

    private static async Task CaptureAsync(StreamReader reader, StringBuilder output, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }
            lock (output)
            {
                if (output.Length < MaximumOutputChars)
                {
                    output.Append(buffer, 0, Math.Min(count, MaximumOutputChars - output.Length));
                }
            }
        }
    }
}

public readonly record struct NativeToolResult(int ExitCode, string Output);