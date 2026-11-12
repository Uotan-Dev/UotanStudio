using System.Collections.Concurrent;

namespace UotanStudio.Comm.Transports.Usb.Core;

/// <summary>
/// Remembers the backend's own error text for the most recent refused open of each device path, so the exception the
/// host finally sees can name the native reason instead of guessing.
/// <para>按设备路径记住最近一次被拒绝打开时后端给出的错误原文，使宿主最终看到的异常能写出原生原因，而不是猜测。</para>
/// </summary>
/// <remarks>
/// A failed open produces no session, so the only place that knew the IOReturn or LIBUSB error is the backend object
/// that failed; the opener runs afterwards and would otherwise be limited to the generic "another process may hold the
/// port" wording. Entries are bounded and keyed by path only because the device may already be gone by the time the
/// message is composed.
/// <para>打开失败不会留下会话，唯一知道 IOReturn 或 LIBUSB 错误的地方就是失败的后端对象；开启器在其之后运行，
/// 否则只能使用“可能被其他进程占用”这类泛化文案。记录有界，并且只按路径索引，因为拼装消息时设备可能已经消失。</para>
/// </remarks>
internal static class UsbOpenFailures
{
    /// <summary>Maximum retained paths; old entries are dropped first. <para>保留的路径上限；先丢弃最旧的条目。</para></summary>
    private const int MaximumEntries = 64;

    private static readonly ConcurrentDictionary<string, string> Details = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> Order = new();

    /// <summary>
    /// Retains the failure text for one device path.
    /// <para>为一条设备路径保留失败原文。</para>
    /// </summary>
    /// <param name="devicePath">The exact path the backend tried to open; blank paths are ignored.
    /// <para>后端尝试打开的精确路径；空白路径被忽略。</para></param>
    /// <param name="detail">The backend's own error text. <para>后端自己的错误原文。</para></param>
    internal static void Record(string? devicePath, string detail)
    {
        if (string.IsNullOrWhiteSpace(devicePath) || string.IsNullOrWhiteSpace(detail))
        {
            return;
        }

        Details[devicePath] = detail;
        Order.Enqueue(devicePath);
        while (Details.Count > MaximumEntries && Order.TryDequeue(out var oldest))
        {
            Details.TryRemove(oldest, out _);
        }
    }

    /// <summary>
    /// Returns the retained failure text for one device path.
    /// <para>返回某条设备路径保留的失败原文。</para>
    /// </summary>
    /// <param name="devicePath">The exact device path. <para>精确设备路径。</para></param>
    /// <returns>The recorded text, or <see langword="null"/> when nothing was recorded for that path.
    /// <para>已记录的原文；该路径没有记录时为 <see langword="null"/>。</para></returns>
    internal static string? TryGet(string? devicePath)
        => string.IsNullOrWhiteSpace(devicePath) ? null
            : Details.TryGetValue(devicePath, out var detail) ? detail : null;
}