using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Core;

/// <summary>
/// Wraps a set of device sessions and disposes them together.
/// <para>封装一组设备会话并统一释放。</para>
/// </summary>
/// <remarks>
/// Initializes a new session collection.
/// <para>初始化新的会话集合。</para>
/// </remarks>
/// <param name="sessions">The sessions to wrap. <para>需要封装的会话集合。</para></param>
public sealed class UsbSessionCollection(IReadOnlyList<IUsbDeviceSession> sessions) : IDisposable
{

    /// <summary>
    /// Gets the wrapped sessions.
    /// <para>获取已封装的会话列表。</para>
    /// </summary>
    public IReadOnlyList<IUsbDeviceSession> Sessions { get; } = sessions;

    /// <summary>
    /// Returns an enumerator over the wrapped sessions.
    /// <para>返回封装会话的枚举器。</para>
    /// </summary>
    /// <returns>An enumerator over the sessions. <para>会话枚举器。</para></returns>
    public IEnumerator<IUsbDeviceSession> GetEnumerator() => Sessions.GetEnumerator();

    /// <summary>
    /// Disposes every wrapped session.
    /// <para>释放所有封装会话。</para>
    /// </summary>
    public void Dispose()
    {
        foreach (var session in Sessions)
        {
            session.Dispose();
        }
    }
}