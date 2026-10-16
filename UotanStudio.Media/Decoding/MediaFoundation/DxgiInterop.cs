using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.MediaFoundation;

/// <summary>
/// DXGI interop for keyed-mutex shared textures: adapter lookup by LUID, legacy shared handles and
/// cross-device mutex access.
/// <para>面向键互锁共享纹理的 DXGI 互操作：按 LUID 查找适配器、遗留共享句柄与跨设备互锁访问。</para>
/// </summary>
/// <remarks>
/// <para>
/// Struct layouts and vtable slots follow dxgi.h (Windows SDK 10.0.26100). The keyed-mutex slots assume
/// the interface chain IDXGIKeyedMutex → IDXGIObject → IUnknown: IUnknown occupies slots 0–2 and IDXGIObject
/// occupies SetPrivateData (3), SetPrivateDataInterface (4), GetPrivateData (5) and GetParent (6), so
/// AcquireSync is slot 7 and ReleaseSync is slot 8. IDXGIResource::GetSharedHandle is slot 8 for the same
/// reason, because IDXGIDeviceSubObject::GetDevice occupies slot 7.
/// </para>
/// <para>
/// 结构体布局与 vtable 槽位遵循 dxgi.h（Windows SDK 10.0.26100）。键互锁的槽位按接口链
/// IDXGIKeyedMutex → IDXGIObject → IUnknown 计算：IUnknown 占槽位 0–2，IDXGIObject 占 SetPrivateData（3）、
/// SetPrivateDataInterface（4）、GetPrivateData（5）与 GetParent（6），因此 AcquireSync 为槽位 7、
/// ReleaseSync 为槽位 8。同理 IDXGIResource::GetSharedHandle 为槽位 8（IDXGIDeviceSubObject::GetDevice 占槽位 7）。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static unsafe partial class DxgiInterop
{
    private const string Dxgi = "dxgi.dll";

    /// <summary>
    /// IID_IDXGIFactory1.
    /// <para>IID_IDXGIFactory1。</para>
    /// </summary>
    public static readonly Guid IidDxgiFactory1 = new("770AAE78-F26F-4DBA-A829-253C83D1B387");

    /// <summary>
    /// IID_IDXGIResource.
    /// <para>IID_IDXGIResource。</para>
    /// </summary>
    public static readonly Guid IidDxgiResource = new("0451E34F-7EA8-48C0-9A8E-1E0D6B4E9D24");

    /// <summary>
    /// IID_IDXGIKeyedMutex.
    /// <para>IID_IDXGIKeyedMutex。</para>
    /// </summary>
    public static readonly Guid IidDxgiKeyedMutex = new("9D8E1289-D7B3-4E22-9574-10D6AA696D97");

    /// <summary>
    /// D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX.
    /// <para>D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX。</para>
    /// </summary>
    public const uint MiscSharedKeyedMutex = 0x100;

    /// <summary>
    /// WAIT_TIMEOUT (0x102): the mutex is held by a device that acquired a different key.
    /// <para>WAIT_TIMEOUT（0x102）：互锁被以其他键获取的设备持有。</para>
    /// </summary>
    public const int WaitTimeout = 0x102;

    /// <summary>
    /// WAIT_ABANDONED (0x80): the owning device terminated without releasing the mutex.
    /// <para>WAIT_ABANDONED（0x80）：持有设备在未释放互锁的情况下终止。</para>
    /// </summary>
    public const int WaitAbandoned = 0x80;

    [LibraryImport(Dxgi)]
    private static partial int CreateDXGIFactory1(in Guid iid, out nint factory);

    /// <summary>
    /// IDXGIFactory.EnumAdapters (slot 7: IUnknown 0–2, IDXGIObject 3–6).
    /// <para>IDXGIFactory.EnumAdapters（槽位 7：IUnknown 0–2，IDXGIObject 3–6）。</para>
    /// </summary>
    public static int EnumAdapters(nint factory, uint index, out nint adapter)
    {
        fixed (nint* a = &adapter)
            return ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint***)factory)[7])(factory, index, a);
    }

    /// <summary>
    /// IDXGIAdapter.GetDesc (slot 7: IUnknown 0–2, IDXGIObject 3–6).
    /// <para>IDXGIAdapter.GetDesc（槽位 7：IUnknown 0–2，IDXGIObject 3–6）。</para>
    /// </summary>
    /// <param name="adapter">IDXGIAdapter interface pointer. <para>IDXGIAdapter 接口指针。</para></param>
    /// <param name="desc">
    /// DXGI_ADAPTER_DESC written by the call; the slot's HRESULT is not surfaced by this wrapper.
    /// <para>由该调用写入的 DXGI_ADAPTER_DESC；此包装不返回该槽位的 HRESULT。</para>
    /// </param>
    public static void AdapterGetDesc(nint adapter, out DxgiAdapterDesc desc)
    {
        fixed (DxgiAdapterDesc* d = &desc)
            ((delegate* unmanaged[Stdcall]<nint, DxgiAdapterDesc*, void>)(*(nint***)adapter)[7])(adapter, d);
    }

    /// <summary>
    /// IDXGIResource.GetSharedHandle (slot 8). The returned handle is a process-global alias, not an
    /// owning NT handle: it must never be closed, and releasing the COM references is enough.
    /// <para>IDXGIResource.GetSharedHandle（槽位 8）。返回的句柄是进程全局别名而非持有型 NT 句柄：
    /// 绝不能 CloseHandle，释放 COM 引用即可。</para>
    /// </summary>
    public static int GetSharedHandle(nint resource, out nint sharedHandle)
    {
        fixed (nint* h = &sharedHandle)
            return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint***)resource)[8])(resource, h);
    }

    /// <summary>
    /// IDXGIKeyedMutex.AcquireSync (slot 7). With a zero timeout the call never blocks and returns
    /// <see cref="WaitTimeout"/> when another device owns the mutex.
    /// <para>IDXGIKeyedMutex.AcquireSync（槽位 7）。超时为 0 时调用不会阻塞，互锁被其他设备持有时返回
    /// <see cref="WaitTimeout"/>。</para>
    /// </summary>
    public static int AcquireSync(nint keyedMutex, ulong key, uint timeoutMilliseconds) =>
        ((delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)(*(nint***)keyedMutex)[7])(keyedMutex, key, timeoutMilliseconds);

    /// <summary>
    /// IDXGIKeyedMutex.ReleaseSync (slot 8), handing the mutex over at <paramref name="key"/>.
    /// <para>IDXGIKeyedMutex.ReleaseSync（槽位 8），以 <paramref name="key"/> 移交互锁。</para>
    /// </summary>
    public static int ReleaseSync(nint keyedMutex, ulong key) =>
        ((delegate* unmanaged[Stdcall]<nint, ulong, int>)(*(nint***)keyedMutex)[8])(keyedMutex, key);

    /// <summary>
    /// Returns the adapter whose LUID matches <paramref name="adapterLuid"/> with one reference held,
    /// or <see cref="nint.Zero"/> when no adapter matches; the caller releases the pointer.
    /// <para>返回 LUID 与 <paramref name="adapterLuid"/> 一致且持有一个引用的适配器；无匹配时返回
    /// <see cref="nint.Zero"/>，由调用方释放指针。</para>
    /// </summary>
    /// <param name="adapterLuid">
    /// LUID bytes to match, 8 bytes of low part followed by high part; a span of any other
    /// length matches no adapter.
    /// <para>待匹配的 LUID 字节，低位在前共 8 字节；其他长度的区间匹配不到任何适配器。</para>
    /// </param>
    /// <exception cref="InvalidOperationException">A DXGI call failed.
    /// <para>某个 DXGI 调用失败。</para></exception>
    public static nint FindAdapterByLuid(ReadOnlySpan<byte> adapterLuid)
    {
        Check(CreateDXGIFactory1(IidDxgiFactory1, out var factory), "CreateDXGIFactory1");
        nint match = 0;
        try
        {
            for (uint index = 0; ; index++)
            {
                var hr = EnumAdapters(factory, index, out var adapter);
                if (hr == unchecked((int)0x887A0002)) break;
                Check(hr, "EnumAdapters");
                try
                {
                    AdapterGetDesc(adapter, out DxgiAdapterDesc desc);
                    if (new ReadOnlySpan<byte>(&desc.AdapterLuidLowPart, 8).SequenceEqual(adapterLuid))
                    {
                        match = adapter;
                        break;
                    }
                }
                finally
                {
                    if (match == 0 || adapter != match) MfCom.Release(adapter);
                }
            }
        }
        finally { MfCom.Release(factory); }
        return match;
    }

    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new InvalidOperationException($"DXGI {operation} failed: 0x{hr:X8}.");
    }
}

/// <summary>
/// DXGI_ADAPTER_DESC; the 128-character description is skipped for comparisons.
/// <para>DXGI_ADAPTER_DESC；128 字符的描述字段不参与比较。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DxgiAdapterDesc
{
    public fixed ushort Description[128];
    public uint VendorId;
    public uint DeviceId;
    public uint SubSysId;
    public uint Revision;
    public uint AdapterLuidLowPart;
    public int AdapterLuidHighPart;
    public nuint DedicatedVideoMemory;
    public nuint DedicatedSystemMemory;
    public nuint SharedSystemMemory;
}