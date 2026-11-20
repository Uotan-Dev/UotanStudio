namespace UotanStudio.Comm.Transports.Usb.Core;

/// <summary>
/// Announces the exact device-interface path carried by an OS arrival broadcast, without waiting for an
/// inventory pass. A MediaTek preloader accepts the boot handshake for only a few seconds after arrival, and a
/// full USB walk costs more than that, so the broadcast path is the only signal early enough to act on.
/// <para>公布操作系统到达广播携带的精确设备接口路径，而不等待清单遍历。联发科 preloader 只在到达后的数秒内
/// 接受 boot 握手，而整棵 USB 遍历比这更慢；因此广播路径是唯一足够早、可用于行动的信号。</para>
/// </summary>
/// <remarks>
/// Subscribers must treat the path as a hint: the device may already be gone, and the identity is re-verified
/// on the opened handle. Handlers run on the OS message thread, so they must return immediately and queue any
/// real work.
/// <para>订阅方必须把路径当作提示：设备可能已经消失，且身份会在已打开的句柄上重新核实。处理程序运行在
/// 操作系统消息线程上，必须立即返回并把实际工作排队。</para>
/// </remarks>
internal static class UsbArrivalBroadcast
{
    /// <summary>Raised for every OS arrival broadcast that named an interface path.
    /// <para>每个给出接口路径的操作系统到达广播都会触发。</para></summary>
    internal static event Action<string>? PathArrived;

    /// <summary>Publishes one arrival path to every subscriber, isolating subscriber failures.
    /// <para>把一条到达路径发布给所有订阅方，并隔离订阅方失败。</para></summary>
    /// <param name="path">The device-interface path from the broadcast.
    /// <para>广播携带的设备接口路径。</para></param>
    internal static void Publish(string path)
    {
        var handlers = PathArrived;
        if (handlers is null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(path);
            }
            catch (Exception)
            {
            }
        }
    }
}