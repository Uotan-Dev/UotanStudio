using System.Diagnostics;
using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Runs official HDC tasks, including host-side file/app transfer negotiation.
/// <para>执行官方 HDC 任务，包含主机侧文件与应用传输协商。</para>
/// </summary>
public static class HdcCommandClient
{
    /// <summary>Builds literal process arguments without a command shell. 构造不经命令解释器的字面参数。</summary>
    public static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            if (argument.Contains('\0')) throw new ArgumentException("HDC argument contains NUL.", nameof(arguments));
            info.ArgumentList.Add(argument);
        }
        return info;
    }

    /// <summary>Runs a bounded, cancellable task and checks both exit status and HDC failure output.
    /// <para>有界、可取消地运行任务，同时检查进程退出码与 HDC 失败输出。</para></summary>
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken cancellationToken = default, int timeoutMs = 300000)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        using var process = new Process { StartInfo = CreateStartInfo(executable, arguments) };
        timeout.Token.ThrowIfCancellationRequested();
        process.Start();
        using var registration = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        async Task<string> DrainAsync(StreamReader reader)
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (text.Length + count > HdcServerClient.MaximumOutputBytes)
                {
                    await timeout.CancelAsync().ConfigureAwait(false);
                    throw new InvalidDataException("HDC process output limit exceeded.");
                }
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }
        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            var output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException($"HDC exited with code {process.ExitCode}: {output.Trim()}");
            HdcServerClient.ThrowOnFailure(output);
            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException($"HDC task exceeded {timeoutMs} ms."); }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
