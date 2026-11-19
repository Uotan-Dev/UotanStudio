using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace UotanStudio.DD.Drives.Windows;

/// <summary>
/// Drives optical-disc enumeration and recording through the IMAPI2 COM automation objects built into Windows.
/// <para>通过 Windows 内置的 IMAPI2 COM 自动化对象完成光驱枚举与刻录。</para>
/// </summary>
/// <remarks>
/// ISO-9660 images are data streams with 2048-byte sectors and must be written through
/// <c>IMAPI2.MsftDiscFormat2Data</c>, the same writer the Windows disc image burning tool uses. The
/// <c>MsftDiscFormat2RawCD</c> writer is only for DAO-96 raw (2352-byte sector) images on blank CD-R/RW.
/// <para>
/// ISO-9660 镜像是每扇区 2048 字节的数据流，必须通过 <c>IMAPI2.MsftDiscFormat2Data</c> 写入，
/// 这也是 Windows 光盘镜像刻录工具使用的写入器。<c>MsftDiscFormat2RawCD</c> 只用于空白 CD-R/RW 上的
/// DAO-96 raw（2352 字节扇区）镜像。
/// </para>
/// </remarks>
internal static class WindowsOpticalCommand
{
    private const string PowerShell = "powershell.exe";

    /// <summary>
    /// Builds the PowerShell script that lists optical recorders, their unique identifiers and media state.
    /// <para>构建用于列出光驱、唯一标识与介质状态的 PowerShell 脚本。</para>
    /// </summary>
    /// <returns>The script text.<para>脚本文本。</para></returns>
    internal static string BuildListScript()
    {
        var s = new StringBuilder();
        s.AppendLine("$ErrorActionPreference = 'Stop'");
        s.AppendLine("$master = New-Object -ComObject IMAPI2.MsftDiscMaster2");
        s.AppendLine("if (-not $master.IsSupportedEnvironment) { throw 'IMAPI2 is not supported in this environment.' }");
        s.AppendLine("for ($i = 0; $i -lt $master.Count; $i++) {");
        s.AppendLine("  $id = [string]$master.Item($i)");
        s.AppendLine("  $recorder = New-Object -ComObject IMAPI2.MsftDiscRecorder2");
        s.AppendLine("  $recorder.InitializeDiscRecorder($id)");
        s.AppendLine("  $vendor = [string]$recorder.VendorId");
        s.AppendLine("  $product = [string]$recorder.ProductId");
        s.AppendLine("  $desc = ($vendor.Trim() + ' ' + $product.Trim()).Trim()");
        s.AppendLine("  if (-not $desc) { $desc = 'Optical Recorder ' + $i }");
        s.AppendLine("  $mediaType = -1");
        s.AppendLine("  $mediaState = -1");
        s.AppendLine("  try {");
        s.AppendLine("    $writer = New-Object -ComObject IMAPI2.MsftDiscFormat2Data");
        s.AppendLine("    $writer.Recorder = $recorder");
        s.AppendLine("    $mediaType = [int]$writer.CurrentPhysicalMediaType");
        s.AppendLine("    $mediaState = [int]$writer.CurrentMediaStatus");
        s.AppendLine("    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($writer)");
        s.AppendLine("  } catch { }");
        s.AppendLine("  Write-Output ('RECORDER' + [char]9 + $id + [char]9 + $desc + [char]9 + $mediaType + [char]9 + $mediaState)");
        s.AppendLine("  [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($recorder)");
        s.AppendLine("}");
        s.AppendLine("[void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($master)");
        return s.ToString();
    }

    /// <summary>
    /// Builds the PowerShell script that records an ISO image to one optical recorder through IMAPI2.
    /// <para>构建通过 IMAPI2 把 ISO 镜像刻录到指定光驱的 PowerShell 脚本。</para>
    /// </summary>
    /// <param name="deviceId">Unique IMAPI2 recorder identifier.<para>IMAPI2 刻录机唯一标识。</para></param>
    /// <param name="imagePath">Absolute path of the ISO image.<para>ISO 镜像的绝对路径。</para></param>
    /// <param name="closeSession">Whether to finalise the disc session.<para>是否结束光盘会话。</para></param>
    /// <returns>The script text.<para>脚本文本。</para></returns>
    internal static string BuildBurnScript(string deviceId, string imagePath, bool closeSession)
    {
        var s = new StringBuilder();
        s.AppendLine("$ErrorActionPreference = 'Stop'");
        s.AppendLine("Add-Type -TypeDefinition @'");
        s.AppendLine("using System;");
        s.AppendLine("using System.Runtime.InteropServices;");
        s.AppendLine("using System.Runtime.InteropServices.ComTypes;");
        s.AppendLine("public static class UotanOpticalStream {");
        s.AppendLine("  [DllImport(\"shell32.dll\", CharSet = CharSet.Unicode, PreserveSig = false)]");
        s.AppendLine("  public static extern void SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IntPtr template, out IStream stream);");
        s.AppendLine("}");
        s.AppendLine("'@");
        s.AppendLine("$recorder = New-Object -ComObject IMAPI2.MsftDiscRecorder2");
        s.AppendLine("$recorder.InitializeDiscRecorder(" + Quote(deviceId) + ")");
        s.AppendLine("$writer = New-Object -ComObject IMAPI2.MsftDiscFormat2Data");
        s.AppendLine("$writer.Recorder = $recorder");
        s.AppendLine("$writer.ClientName = 'UotanStudio'");
        s.AppendLine("$writer.ForceMediaToBeClosed = " + (closeSession ? "$true" : "$false"));
        s.AppendLine("$stream = $null");
        s.AppendLine("[UotanOpticalStream]::SHCreateStreamOnFileEx(" + Quote(imagePath) + ", 0, 0, $false, [IntPtr]::Zero, [ref]$stream)");
        s.AppendLine("try {");
        s.AppendLine("  $writer.Write($stream)");
        s.AppendLine("  Write-Output 'BURN_OK'");
        s.AppendLine("} catch {");
        s.AppendLine("  try { $writer.CancelWrite() } catch { }");
        s.AppendLine("  throw");
        s.AppendLine("} finally {");
        s.AppendLine("  if ($stream -ne $null) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($stream) }");
        s.AppendLine("  [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($writer)");
        s.AppendLine("  [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($recorder)");
        s.AppendLine("}");
        return s.ToString();
    }

    /// <summary>
    /// Runs a PowerShell script and captures its standard output.
    /// <para>运行 PowerShell 脚本并捕获其标准输出。</para>
    /// </summary>
    /// <param name="script">Script text.<para>脚本文本。</para></param>
    /// <param name="cancellationToken">Kills the process when cancelled.<para>取消时终止进程。</para></param>
    /// <returns>Standard output, one record per line.<para>标准输出，每条记录占一行。</para></returns>
    /// <exception cref="FlashException">PowerShell could not start or the script exited non-zero.
    /// <para>PowerShell 无法启动或脚本以非零码退出。</para></exception>
    internal static async Task<string> RunAsync(string script, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PowerShell,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            throw new FlashException("Windows could not start PowerShell for optical disc access.", exception);
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
                "The Windows optical disc command failed with exit code "
                + process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": " + error.Trim());
        }

        return output;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}