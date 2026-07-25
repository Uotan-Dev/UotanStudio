using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UotanStudio.Abstractions.Devices;
using UotanStudio.Abstractions.Logging;

namespace UotanStudio.Services.Devices
{
    /// <summary>
    /// <para>外部工具进程辅助类，统一封装 ADB/Fastboot 等可执行文件的调用。</para>
    /// External-tool process helper, uniformly wrapping invocations of ADB/Fastboot executables.
    /// </summary>
    public static class ProcessRunner
    {
        /// <summary>
        /// <para>异步运行外部命令并捕获输出。</para>
        /// Asynchronously runs an external command and captures output.
        /// </summary>
        public static async Task<DeviceCommandResult> RunAsync(
            string fileName,
            string arguments,
            IUnifiedLog log,
            string sourceTag = "DeviceMgr",
            int timeoutMs = 30000,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            log.Write(LogLevel.Debug, sourceTag, $"> {fileName} {arguments}");

            try
            {
                using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                if (!proc.Start()) return DeviceCommandResult.Failed("Failed to start process", -1);

                var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);

                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                var code = proc.ExitCode;

                if (stdout.Length > 0)
                    log.Write(LogLevel.Trace, sourceTag, stdout.TrimEnd());
                if (stderr.Length > 0)
                    log.Write(code == 0 ? LogLevel.Trace : LogLevel.Warn, sourceTag, stderr.TrimEnd());

                return new DeviceCommandResult
                {
                    Success = code == 0,
                    Output = stdout,
                    Error = stderr,
                    ExitCode = code,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                log.Write(LogLevel.Error, sourceTag, $"Executable not found or failed to launch: {fileName} — {ex.Message}");
                return DeviceCommandResult.Failed($"Executable not found: {fileName}", -1);
            }
            catch (System.OperationCanceledException)
            {
                log.Write(LogLevel.Warn, sourceTag, $"Command timed out after {timeoutMs}ms: {fileName} {arguments}");
                return DeviceCommandResult.Failed($"Command timed out after {timeoutMs}ms", -1);
            }
        }
    }
}
