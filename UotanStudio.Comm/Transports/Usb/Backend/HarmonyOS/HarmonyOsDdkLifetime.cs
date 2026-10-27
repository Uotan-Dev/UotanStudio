using UotanStudio.Comm.Diagnostics;
using static UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS.HarmonyOSUsbDDK;

namespace UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS;

/// <summary>
/// Reference-counts the process-wide HarmonyOS USB DDK lifecycle so <c>OH_Usb_Init</c> and
/// <c>OH_Usb_Release</c> run exactly once for every set of concurrent users.
/// <para>对进程级 HarmonyOS USB DDK 生命周期做引用计数，使 <c>OH_Usb_Init</c> 与
/// <c>OH_Usb_Release</c> 对全部并发使用者只执行一次。</para>
/// Both native calls are process-scoped: per-device init/release pairs would tear the DDK down
/// under other open handles, so every owner takes one counted reference instead.
/// <para>这两个原生调用都是进程级的：按设备逐个 init/release 会在其他句柄仍打开时拆掉整个 DDK，
/// 因此每个持有者只取得一个计数引用。</para>
/// </summary>
internal static class HarmonyOsDdkLifetime
{
    private static readonly Lock Sync = new();

    private static int _referenceCount;

    /// <summary>
    /// Acquires one process-wide DDK reference, calling <c>OH_Usb_Init</c> only on the first acquisition.
    /// <para>取得一个进程级 DDK 引用；仅在首次取得时调用 <c>OH_Usb_Init</c>。</para>
    /// Later acquisitions while the count is positive succeed immediately without touching the DDK.
    /// <para>计数为正时后续取得直接成功，不再触碰 DDK。</para>
    /// </summary>
    /// <returns><see cref="USB_DDK_NO_ERROR"/> when the DDK is initialized or already held by another owner; otherwise the <c>OH_Usb_Init</c> error code.
    /// <para>DDK 已初始化或已被其他持有者持有时返回 <see cref="USB_DDK_NO_ERROR"/>；否则返回 <c>OH_Usb_Init</c> 的错误码。</para></returns>
    internal static int Acquire()
    {
        lock (Sync)
        {
            if (_referenceCount == 0)
            {
                var ret = OH_Usb_Init();
                if (ret != USB_DDK_NO_ERROR)
                {
                    UsbTrace.Log($"HarmonyOsDdkLifetime: OH_Usb_Init failed: {GetErrorMessage(ret)}");
                    return ret;
                }
            }

            _referenceCount++;
            return USB_DDK_NO_ERROR;
        }
    }

    /// <summary>
    /// Releases one reference taken by <see cref="Acquire"/>, calling <c>OH_Usb_Release</c> when the count drops to zero.
    /// <para>释放一个由 <see cref="Acquire"/> 取得的引用；计数降为零时调用 <c>OH_Usb_Release</c>。</para>
    /// An unbalanced release is clamped so the count never goes negative and the DDK is never
    /// released twice.
    /// <para>不配对的释放会被钳制，计数不会变为负数，DDK 也不会被重复释放。</para>
    /// </summary>
    internal static void Release()
    {
        lock (Sync)
        {
            if (_referenceCount <= 0)
            {
                UsbTrace.Log("HarmonyOsDdkLifetime: Release called without a matching Acquire; ignoring.");
                return;
            }

            _referenceCount--;
            if (_referenceCount == 0)
            {
                OH_Usb_Release();
            }
        }
    }
}