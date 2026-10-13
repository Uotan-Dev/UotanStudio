using System.Diagnostics;

namespace UotanStudio.Services;

/// <summary>
/// Opens external links such as repositories and author profiles with the system default handler.
/// <para>用系统默认处理程序打开仓库、作者主页等外部链接。</para>
/// </summary>
public static class ExternalLinkService
{
    /// <summary>
    /// Opens the given absolute URL; empty, invalid or unlaunchable addresses are logged and reported as
    /// false instead of throwing.
    /// <para>打开给定的绝对 URL；地址为空、无效或启动失败时写入日志并返回 false，不抛出异常。</para>
    /// </summary>
    /// <param name="url">The absolute URL to open. <para>要打开的绝对 URL。</para></param>
    /// <returns>Whether the default handler was started. <para>是否已启动默认处理程序。</para></returns>
    public static bool TryOpen(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "ExternalLink", $"Failed to open '{url}': {ex.Message}");
            return false;
        }
    }
}