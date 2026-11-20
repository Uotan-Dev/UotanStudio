using System.Runtime.InteropServices;

namespace UotanStudio.DD.Drives.Windows;

/// <summary>
/// Hand-written vtable COM interop for the IMAPI2 disc master, recorder and data format objects.
/// <para>手写 vtable COM 互操作，覆盖 IMAPI2 光盘主控、刻录机与数据格式对象。</para>
/// </summary>
/// <remarks>
/// Slot order matches the Windows SDK imapi2.h C++ declarations: every interface derives from IDispatch,
/// so instance slots start at 7 after IUnknown (slots 0-2) and IDispatch (slots 3-6). Calls are plain
/// vtable invocations without runtime type information, which keeps the code NativeAOT-safe. The
/// coclasses are free threaded per their registry ThreadingModel, so every helper runs inside a manual
/// MTA scope and cross-thread CancelWrite calls are valid.
/// <para>
/// 槽位顺序与 Windows SDK imapi2.h 的 C++ 声明一致：全部接口派生自 IDispatch，实例槽位从 7 开始
/// （IUnknown 占 0-2、IDispatch 占 3-6）。调用是纯 vtable 调用，不依赖运行时类型信息，保持 NativeAOT
/// 兼容。组件类按注册表 ThreadingModel 为自由线程，因此所有辅助方法都在手工 MTA 作用域内执行，
/// 跨线程调用 CancelWrite 是合法的。
/// </para>
/// </remarks>
internal static unsafe class Imapi2Interop
{
    // Class and interface identifiers from the Windows SDK imapi2.h declarations.
    private const string DiscMaster2Clsid = "2735412E-7F64-5B0F-8F00-5D77AFBE261E";
    private const string DiscRecorder2Clsid = "2735412D-7F64-5B0F-8F00-5D77AFBE261E";
    private const string DiscFormat2DataClsid = "2735412A-7F64-5B0F-8F00-5D77AFBE261E";
    private const string DiscMaster2Iid = "27354130-7F64-5B0F-8F00-5D77AFBE261E";
    private const string DiscRecorder2Iid = "27354133-7F64-5B0F-8F00-5D77AFBE261E";
    private const string DiscFormat2DataIid = "27354153-9F64-5B0F-8F00-5D77AFBE261E";
    private const string DiscFormat2DataEventsIid = "2735413C-7F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>Dispatch identifier of DDiscFormat2DataEvents.Update per imapi2.h.<para>imapi2.h 中 DDiscFormat2DataEvents.Update 的 dispatch 标识。</para></summary>
    internal const int UpdateDispatchId = 0x200;

    // IMAPI_FORMAT2_DATA_WRITE_ACTION values from imapi2.h.
    /// <summary>The engine is writing user data.<para>引擎正在写入用户数据。</para></summary>
    internal const int WriteActionWritingData = 0x4;
    /// <summary>The engine is verifying written data.<para>引擎正在校验已写入数据。</para></summary>
    internal const int WriteActionVerifying = 0x7;

    // IMAPI_FORMAT2_DATA_MEDIA_STATE values from imapi2.h.
    // IMAPI_MEDIA_PHYSICAL_TYPE boundaries from imapi2.h; -1 marks a failed media query.
    internal const int MediaTypeUnknown = -1;
    internal const int MediaTypeCdRom = 0x1;
    internal const int MediaTypeCdMax = 0x3;
    internal const int MediaTypeDvdRom = 0x4;
    internal const int MediaTypeDvdMax = 0xD;
    internal const int MediaTypeBdRom = 0x11;
    internal const int MediaTypeBdMax = 0x13;
    internal const int MediaStateOverwriteOnly = 0x1;
    internal const int MediaStateBlank = 0x2;
    internal const int MediaStateAppendable = 0x4;
    internal const int MediaStateUnsupportedMask = 0xFC00;
    internal const int MediaStateDamaged = 0x400;
    internal const int MediaStateEraseRequired = 0x800;
    internal const int MediaStateWriteProtected = 0x2000;
    internal const int MediaStateFinalized = 0x4000;
    internal const int MediaStateUnsupportedMedia = 0x8000;

    /// <summary>
    /// Per-update engine snapshot delivered by the IMAPI2 write callback.
    /// <para>IMAPI2 写入回调每次更新时交付的引擎快照。</para>
    /// </summary>
    /// <param name="Action">IMAPI_FORMAT2_DATA_WRITE_ACTION value.<para>IMAPI_FORMAT2_DATA_WRITE_ACTION 值。</para></param>
    /// <param name="StartLba">First logical block of the current write.<para>当前写入的起始逻辑块。</para></param>
    /// <param name="SectorCount">Total sectors planned for the current write.<para>当前写入规划的总扇区数。</para></param>
    /// <param name="LastWrittenLba">Last completed logical block.<para>最近完成的逻辑块。</para></param>
    /// <param name="RemainingSeconds">Engine-estimated remaining seconds.<para>引擎估算的剩余秒数。</para></param>
    internal delegate void UpdateCallback(int Action, int StartLba, int SectorCount, int LastWrittenLba, int RemainingSeconds);

    /// <summary>
    /// Vendor, product and loaded media snapshot of one recorder.
    /// <para>一个刻录机的厂商、产品与已装入介质快照。</para>
    /// </summary>
    /// <param name="Vendor">Vendor identifier; empty when unavailable.<para>厂商标识；不可用时为空。</para></param>
    /// <param name="Product">Product identifier; empty when unavailable.<para>产品标识；不可用时为空。</para></param>
    /// <param name="MediaType">IMAPI_MEDIA_PHYSICAL_TYPE value, or -1 when unknown.<para>IMAPI_MEDIA_PHYSICAL_TYPE 值；未知时为 -1。</para></param>
    /// <param name="MediaState">IMAPI_FORMAT2_DATA_MEDIA_STATE value, or -1 when unknown.<para>IMAPI_FORMAT2_DATA_MEDIA_STATE 值；未知时为 -1。</para></param>
    internal readonly record struct RecorderInfo(string Vendor, string Product, int MediaType, int MediaState);

    /// <summary>
    /// Lists optical recorder unique identifiers through IDiscMaster2.
    /// <para>通过 IDiscMaster2 列出光驱唯一标识。</para>
    /// </summary>
    /// <returns>Recorder identifiers in enumeration order; empty when the environment has none.<para>按枚举顺序的刻录机标识；环境中没有刻录机时为空。</para></returns>
    internal static string[] ListRecorders()
    {
        using var apartment = new MtaScope();
        var master = CreateInstance(DiscMaster2Clsid, DiscMaster2Iid);
        try
        {
            var vtbl = ReadVtbl(master);
            if (!IsSupportedEnvironment(vtbl, master))
            {
                return [];
            }

            var count = GetCount(vtbl, master);
            var result = new string[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = GetItem(vtbl, master, index);
            }

            return result;
        }
        finally
        {
            ReleaseInstance(master);
        }
    }

    /// <summary>
    /// Reads vendor, product and loaded media state from one recorder.
    /// <para>读取一个刻录机的厂商、产品与已装入介质状态。</para>
    /// </summary>
    /// <param name="deviceId">Recorder identifier from ListRecorders.<para>ListRecorders 返回的刻录机标识。</para></param>
    /// <returns>The recorder snapshot; media values stay -1 when the media query failed.<para>刻录机快照；介质查询失败时介质值保持 -1。</para></returns>
    internal static RecorderInfo GetRecorderInfo(string deviceId)
    {
        using var apartment = new MtaScope();
        var recorder = CreateInstance(DiscRecorder2Clsid, DiscRecorder2Iid);
        try
        {
            var recorderVtbl = ReadVtbl(recorder);
            InitializeRecorder(recorderVtbl, recorder, deviceId);
            var vendor = GetBstrProperty(recorderVtbl, recorder, RecGetVendorId);
            var product = GetBstrProperty(recorderVtbl, recorder, RecGetProductId);
            var mediaType = -1;
            var mediaState = -1;
            var format = CreateInstance(DiscFormat2DataClsid, DiscFormat2DataIid);
            try
            {
                var formatVtbl = ReadVtbl(format);
                PutRecorder(formatVtbl, format, recorder);
                mediaType = GetIntProperty(formatVtbl, format, FmtGetCurrentPhysicalMediaType);
                mediaState = GetIntProperty(formatVtbl, format, FmtGetCurrentMediaStatus);
            }
            catch (COMException)
            {
                // Tray movement or an empty drive can reject media queries; report unknown media.
                // 托盘进出或空驱动器可能拒绝介质查询；此时上报未知介质。
            }
            finally
            {
                ReleaseInstance(format);
            }

            return new RecorderInfo(vendor, product, mediaType, mediaState);
        }
        finally
        {
            ReleaseInstance(recorder);
        }
    }

    /// <summary>
    /// Records an ISO image through IDiscFormat2Data with engine progress callbacks and cooperative
    /// cancellation through CancelWrite.
    /// <para>通过 IDiscFormat2Data 刻录 ISO 镜像，提供引擎进度回调，并经 CancelWrite 支持协作取消。</para>
    /// </summary>
    /// <param name="deviceId">Recorder identifier from ListRecorders.<para>ListRecorders 返回的刻录机标识。</para></param>
    /// <param name="imagePath">Absolute ISO path.<para>ISO 绝对路径。</para></param>
    /// <param name="closeSession">Whether to finalise the session.<para>是否结束会话。</para></param>
    /// <param name="update">Optional callback invoked on engine updates.<para>可选回调，在引擎更新时调用。</para></param>
    /// <param name="cancellationToken">Cancels the write through CancelWrite.<para>经 CancelWrite 取消写入。</para></param>
    /// <exception cref="FlashException">The recorder or engine rejected the write.<para>刻录机或写入引擎拒绝写入。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.<para>已请求取消。</para></exception>
    internal static void BurnImage(
        string deviceId,
        string imagePath,
        bool closeSession,
        UpdateCallback? update,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var apartment = new MtaScope();
        var recorder = CreateInstance(DiscRecorder2Clsid, DiscRecorder2Iid);
        try
        {
            var recorderVtbl = ReadVtbl(recorder);
            InitializeRecorder(recorderVtbl, recorder, deviceId);
            var format = CreateInstance(DiscFormat2DataClsid, DiscFormat2DataIid);
            try
            {
                var formatVtbl = ReadVtbl(format);
                PutRecorder(formatVtbl, format, recorder);
                PutClientName(formatVtbl, format);
                PutForceMediaToBeClosed(formatVtbl, format, closeSession);
                using var sink = new ComEventSink(update);
                sink.Advise(format);
                var stream = CreateFileStream(imagePath);
                try
                {
                    // The engine fires Update callbacks synchronously on this thread; cancellation is
                    // forwarded to the free threaded engine through CancelWrite.
                    // 引擎在本线程同步触发 Update 回调；取消经 CancelWrite 转发给自由线程引擎。
                    using var registration = cancellationToken.UnsafeRegister(
                        static state => TryCancelWrite(state!),
                        format);
                    try
                    {
                        WriteImage(formatVtbl, format, stream);
                    }
                    catch (COMException exception) when (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("The optical recording was cancelled.", exception, cancellationToken);
                    }
                    catch (COMException exception)
                    {
                        throw new FlashException(
                            "The Windows optical recorder rejected the write (HRESULT 0x"
                            + exception.ErrorCode.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ").",
                            exception);
                    }
                }
                finally
                {
                    ReleaseInstance(stream);
                }
            }
            finally
            {
                ReleaseInstance(format);
            }
        }
        finally
        {
            ReleaseInstance(recorder);
        }
    }

    private static void TryCancelWrite(object state)
    {
        if (state is not IntPtr format || format == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var vtbl = ReadVtbl(format);
            _ = ((delegate* unmanaged<IntPtr, int>)(*(vtbl + FmtCancelWrite)))(format);
        }
        catch (Exception)
        {
            // Best effort: a failed cancel only means the write already finished or never started.
            // 尽力而为：取消失败只说明写入已完成或尚未开始。
        }
    }

    private static unsafe bool IsSupportedEnvironment(IntPtr* vtbl, IntPtr master)
    {
        short value = 0;
        var getter = (delegate* unmanaged<IntPtr, short*, int>)(*(vtbl + MasGetIsSupportedEnvironment));
        Marshal.ThrowExceptionForHR(getter(master, &value));
        return value != 0;
    }

    private static unsafe int GetCount(IntPtr* vtbl, IntPtr master)
    {
        int value = 0;
        var getter = (delegate* unmanaged<IntPtr, int*, int>)(*(vtbl + MasGetCount));
        Marshal.ThrowExceptionForHR(getter(master, &value));
        return value;
    }

    private static unsafe string GetItem(IntPtr* vtbl, IntPtr master, int index)
    {
        IntPtr bstr = IntPtr.Zero;
        var getter = (delegate* unmanaged<IntPtr, int, IntPtr*, int>)(*(vtbl + MasGetItem));
        Marshal.ThrowExceptionForHR(getter(master, index, &bstr));
        try
        {
            return Marshal.PtrToStringBSTR(bstr);
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    private static unsafe void InitializeRecorder(IntPtr* vtbl, IntPtr recorder, string deviceId)
    {
        var bstr = Marshal.StringToBSTR(deviceId);
        try
        {
            var method = (delegate* unmanaged<IntPtr, IntPtr, int>)(*(vtbl + RecInitializeDiscRecorder));
            Marshal.ThrowExceptionForHR(method(recorder, bstr));
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    private static unsafe void PutRecorder(IntPtr* vtbl, IntPtr format, IntPtr recorder)
    {
        var method = (delegate* unmanaged<IntPtr, IntPtr, int>)(*(vtbl + FmtPutRecorder));
        Marshal.ThrowExceptionForHR(method(format, recorder));
    }

    private static unsafe void PutClientName(IntPtr* vtbl, IntPtr format)
    {
        var bstr = Marshal.StringToBSTR(ClientName);
        try
        {
            var method = (delegate* unmanaged<IntPtr, IntPtr, int>)(*(vtbl + FmtPutClientName));
            Marshal.ThrowExceptionForHR(method(format, bstr));
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    private static unsafe void PutForceMediaToBeClosed(IntPtr* vtbl, IntPtr format, bool value)
    {
        var method = (delegate* unmanaged<IntPtr, short, int>)(*(vtbl + FmtPutForceMediaToBeClosed));
        Marshal.ThrowExceptionForHR(method(format, (short)(value ? -1 : 0)));
    }

    private static unsafe void WriteImage(IntPtr* vtbl, IntPtr format, IntPtr stream)
    {
        var method = (delegate* unmanaged<IntPtr, IntPtr, int>)(*(vtbl + FmtWrite));
        Marshal.ThrowExceptionForHR(method(format, stream));
    }

    private static unsafe IntPtr CreateInstance(string clsid, string iid)
    {
        var clsidGuid = new Guid(clsid);
        var iidGuid = new Guid(iid);
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsidGuid, IntPtr.Zero, ClsCtxInProcServer, ref iidGuid, out var instance));
        return instance;
    }

    private static unsafe IntPtr* ReadVtbl(IntPtr instance) => *(IntPtr**)instance;

    private static unsafe void ReleaseInstance(IntPtr instance)
    {
        if (instance == IntPtr.Zero)
        {
            return;
        }

        var vtbl = ReadVtbl(instance);
        _ = ((delegate* unmanaged<IntPtr, uint>)(*(vtbl + ReleaseSlot)))(instance);
    }

    private static unsafe string GetBstrProperty(IntPtr* vtbl, IntPtr instance, int slot)
    {
        IntPtr bstr = IntPtr.Zero;
        var getter = (delegate* unmanaged<IntPtr, IntPtr*, int>)(*(vtbl + slot));
        Marshal.ThrowExceptionForHR(getter(instance, &bstr));
        try
        {
            return Marshal.PtrToStringBSTR(bstr);
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    private static unsafe int GetIntProperty(IntPtr* vtbl, IntPtr instance, int slot)
    {
        int value = 0;
        var getter = (delegate* unmanaged<IntPtr, int*, int>)(*(vtbl + slot));
        Marshal.ThrowExceptionForHR(getter(instance, &value));
        return value;
    }

    private static unsafe IntPtr CreateFileStream(string imagePath)
    {
        Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(imagePath, StgmRead | StgmShareDenyWrite, FileAttributeNormal, fCreate: false, IntPtr.Zero, out var stream));
        return stream;
    }

    private const string ClientName = "UotanStudio";
    private const uint ClsCtxInProcServer = 1;
    private const uint StgmRead = 0x00000000;
    private const uint StgmShareDenyWrite = 0x00000020;
    private const uint FileAttributeNormal = 0x00000080;

    // IUnknown slots shared by every COM interface.
    private const int ReleaseSlot = 2;

    // IDiscMaster2 own slots (imapi2.h); frozen by Imapi2InteropSlotTests.
    internal const int MasGetItem = 8;
    internal const int MasGetCount = 9;
    internal const int MasGetIsSupportedEnvironment = 10;

    // IDiscRecorder2 own slots (imapi2.h); frozen by Imapi2InteropSlotTests.
    internal const int RecInitializeDiscRecorder = 13;
    internal const int RecGetVendorId = 15;
    internal const int RecGetProductId = 16;

    // IDiscFormat2Data own slots (imapi2.h); frozen by Imapi2InteropSlotTests.
    internal const int FmtPutRecorder = 7;
    internal const int FmtGetCurrentMediaStatus = 13;
    internal const int FmtPutForceMediaToBeClosed = 20;
    internal const int FmtGetCurrentPhysicalMediaType = 24;
    internal const int FmtPutClientName = 25;
    internal const int FmtWrite = 36;
    internal const int FmtCancelWrite = 37;

    // IConnectionPointContainer and IConnectionPoint own slots (ocidl.h).
    private const int CpcFindConnectionPoint = 4;
    private const int CpAdvise = 5;
    private const int CpUnadvise = 6;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr unkOuter, uint clsContext, ref Guid iid, out IntPtr instance);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint apartmentModel);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool fCreate, IntPtr template, out IntPtr stream);

    /// <summary>
    /// Balances a manual MTA activation for the duration of one interop call.
    /// <para>为单次互操作调用平衡一次手工 MTA 激活。</para>
    /// </summary>
    private sealed class MtaScope : IDisposable
    {
        private const uint CoinitMultithreaded = 0x0;
        private const int RpcEChangedMode = unchecked((int)0x80010106);
        private readonly bool _owned;

        public MtaScope()
        {
            var hr = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
            if (hr == RpcEChangedMode)
            {
                // The thread already initialized COM on another apartment; free threaded objects remain
                // directly callable without marshaling.
                // 线程已在其它单元初始化 COM；自由线程对象仍可直接调用而无需编组。
                return;
            }

            Marshal.ThrowExceptionForHR(hr);
            _owned = true;
        }

        public void Dispose()
        {
            if (_owned)
            {
                CoUninitialize();
            }
        }
    }

    /// <summary>
    /// Native IDispatch sink bridging DDiscFormat2DataEvents.Update into a managed callback.
    /// <para>原生 IDispatch 事件汇，把 DDiscFormat2DataEvents.Update 桥接进托管回调。</para>
    /// </summary>
    private sealed unsafe class ComEventSink : IDisposable
    {
        private const int SinkVtableSlotCount = 7;
        private const short VariantDispatch = 9;
        private const int ENotImpl = unchecked((int)0x80004001);
        private const int ENoInterface = unchecked((int)0x80004002);

        private static readonly Guid UnknownIid = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid DispatchIid = new("00020400-0000-0000-C000-000000000046");
        private static readonly Guid EventsIid = new(DiscFormat2DataEventsIid);
        private static readonly Guid EventArgsIid = new("2735413D-7F64-5B0F-8F00-5D77AFBE261E");

        // IWriteEngine2EventArgs inherited slots then IDiscFormat2DataEventArgs own slots (imapi2.h).
        private const int WeaGetStartLba = 7;
        private const int WeaGetSectorCount = 8;
        private const int WeaGetLastWrittenLba = 10;
        private const int WeaGetTotalTime = 16;
        private const int WeaGetCurrentAction = 17;

        private readonly GCHandle _handle;
        private readonly IntPtr _native;
        private uint _cookie;
        private IntPtr _connectionPoint;
        private bool _disposed;

        public ComEventSink(UpdateCallback? update)
        {
            Update = update;
            _handle = GCHandle.Alloc(this);
            var vtable = (IntPtr*)Marshal.AllocHGlobal(sizeof(IntPtr) * SinkVtableSlotCount);
            vtable[0] = (IntPtr)(delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&SinkQueryInterface;
            vtable[1] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&SinkAddRef;
            vtable[2] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&SinkRelease;
            vtable[3] = (IntPtr)(delegate* unmanaged<IntPtr, uint*, int>)&SinkGetTypeInfoCount;
            vtable[4] = (IntPtr)(delegate* unmanaged<IntPtr, uint, uint, IntPtr, int>)&SinkGetTypeInfo;
            vtable[5] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, uint, IntPtr, int>)&SinkGetIdsOfNames;
            vtable[6] = (IntPtr)(delegate* unmanaged<IntPtr, int, Guid*, uint, ushort, Dispparams*, IntPtr, IntPtr, IntPtr, int>)&SinkInvoke;
            _native = Marshal.AllocHGlobal(sizeof(IntPtr) * 3);
            *(IntPtr*)_native = (IntPtr)vtable;
            *((IntPtr*)_native + 1) = GCHandle.ToIntPtr(_handle);
            *(long*)((byte*)_native + sizeof(IntPtr) * 2) = 1;
        }

        private UpdateCallback? Update { get; }

        /// <summary>
        /// Subscribes to the data-format update connection point and stores the advise cookie.
        /// <para>订阅数据格式更新连接点并保存 advise cookie。</para>
        /// </summary>
        /// <param name="format">IDiscFormat2Data instance.<para>IDiscFormat2Data 实例。</para></param>
        public void Advise(IntPtr format)
        {
            var formatVtbl = ReadVtbl(format);
            Guid containerIid = ConnectionPointContainerGuid;
            Guid eventsIid = EventsIid;
            IntPtr container = IntPtr.Zero;
            var queryInterface = (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)(*formatVtbl);
            Marshal.ThrowExceptionForHR(queryInterface(format, &containerIid, &container));
            try
            {
                var containerVtbl = ReadVtbl(container);
                var findPoint = (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)(*(containerVtbl + CpcFindConnectionPoint));
                IntPtr point = IntPtr.Zero;
                Marshal.ThrowExceptionForHR(findPoint(container, &eventsIid, &point));
                try
                {
                    var pointVtbl = ReadVtbl(point);
                    var advise = (delegate* unmanaged<IntPtr, IntPtr, uint*, int>)(*(pointVtbl + CpAdvise));
                    uint cookie = 0;
                    Marshal.ThrowExceptionForHR(advise(point, _native, &cookie));
                    _cookie = cookie;
                    _connectionPoint = point;
                }
                catch
                {
                    ReleaseInstance(point);
                    throw;
                }
            }
            finally
            {
                ReleaseInstance(container);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_cookie != 0 && _connectionPoint != IntPtr.Zero)
            {
                var pointVtbl = ReadVtbl(_connectionPoint);
                _ = ((delegate* unmanaged<IntPtr, uint, int>)(*(pointVtbl + CpUnadvise)))(_connectionPoint, _cookie);
            }

            if (_connectionPoint != IntPtr.Zero)
            {
                ReleaseInstance(_connectionPoint);
                _connectionPoint = IntPtr.Zero;
            }

            ReleaseNative();
        }

        private void ReleaseNative()
        {
            if (AdjustRefCount(_native, -1) <= 0)
            {
                var handlePointer = *((IntPtr*)_native + 1);
                if (handlePointer != IntPtr.Zero)
                {
                    GCHandle.FromIntPtr(handlePointer).Free();
                }

                Marshal.FreeHGlobal(*(IntPtr*)_native);
                Marshal.FreeHGlobal(_native);
            }
        }

        private static readonly Guid ConnectionPointContainerGuid = new("B196B284-BAB4-101A-B69C-00AA00341D07");

        [UnmanagedCallersOnly]
        private static int SinkQueryInterface(IntPtr self, Guid* riid, IntPtr* ppvObject)
        {
            if (riid is null || ppvObject is null)
            {
                return ENoInterface;
            }

            var iid = *riid;
            if (iid == UnknownIid || iid == DispatchIid || iid == EventsIid)
            {
                _ = AdjustRefCount(self, 1);
                *ppvObject = self;
                return 0;
            }

            *ppvObject = IntPtr.Zero;
            return ENoInterface;
        }

        [UnmanagedCallersOnly]
        private static uint SinkAddRef(IntPtr self)
        {
            return unchecked((uint)AdjustRefCount(self, 1));
        }

        [UnmanagedCallersOnly]
        private static uint SinkRelease(IntPtr self)
        {
            var count = AdjustRefCount(self, -1);
            if (count > 0)
            {
                return unchecked((uint)count);
            }

            var handlePointer = *((IntPtr*)self + 1);
            if (handlePointer != IntPtr.Zero)
            {
                GCHandle.FromIntPtr(handlePointer).Free();
            }

            Marshal.FreeHGlobal(*(IntPtr*)self);
            Marshal.FreeHGlobal(self);
            return 0;
        }

        private static long AdjustRefCount(IntPtr self, int delta)
        {
            var counter = RefCountPointer(self);
            return delta > 0 ? Interlocked.Increment(ref *counter) : Interlocked.Decrement(ref *counter);
        }

        private static long* RefCountPointer(IntPtr self) => (long*)((byte*)self + sizeof(IntPtr) * 2);

        [UnmanagedCallersOnly]
        private static int SinkGetTypeInfoCount(IntPtr self, uint* pctinfo)
        {
            if (pctinfo is not null)
            {
                *pctinfo = 0;
            }

            return 0;
        }

        [UnmanagedCallersOnly]
        private static int SinkGetTypeInfo(IntPtr self, uint typeInfo, uint locale, IntPtr dispatch) => ENotImpl;

        [UnmanagedCallersOnly]
        private static int SinkGetIdsOfNames(IntPtr self, IntPtr riid, IntPtr names, uint count, uint locale, IntPtr dispIds) => ENotImpl;

        [UnmanagedCallersOnly]
        private static int SinkInvoke(
            IntPtr self,
            int dispatchId,
            Guid* riid,
            uint locale,
            ushort flags,
            Dispparams* arguments,
            IntPtr varResult,
            IntPtr excepInfo,
            IntPtr argErrors)
        {
            if (dispatchId != UpdateDispatchId || arguments is null || arguments->Rgvarg == IntPtr.Zero || arguments->CArgs < 1)
            {
                return 0;
            }

            var handlePointer = *((IntPtr*)self + 1);
            var sink = (ComEventSink?)GCHandle.FromIntPtr(handlePointer).Target;
            var callback = sink?.Update;
            if (callback is null)
            {
                return 0;
            }

            var variant = *(Variant*)arguments->Rgvarg;
            if (variant.Vt != VariantDispatch || variant.Data == IntPtr.Zero)
            {
                return 0;
            }

            ReadEventArgs(variant.Data, callback);
            return 0;
        }

        private static void ReadEventArgs(IntPtr dispatch, UpdateCallback callback)
        {
            Guid argsIid = EventArgsIid;
            IntPtr eventArgs = IntPtr.Zero;
            var dispatchVtbl = ReadVtbl(dispatch);
            var queryInterface = (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)(*dispatchVtbl);
            if (queryInterface(dispatch, &argsIid, &eventArgs) < 0 || eventArgs == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var argsVtbl = ReadVtbl(eventArgs);
                int startLba = 0;
                int sectorCount = 0;
                int lastWrittenLba = 0;
                int totalTime = 0;
                int action = 0;
                _ = ((delegate* unmanaged<IntPtr, int*, int>)(*(argsVtbl + WeaGetStartLba)))(eventArgs, &startLba);
                _ = ((delegate* unmanaged<IntPtr, int*, int>)(*(argsVtbl + WeaGetSectorCount)))(eventArgs, &sectorCount);
                _ = ((delegate* unmanaged<IntPtr, int*, int>)(*(argsVtbl + WeaGetLastWrittenLba)))(eventArgs, &lastWrittenLba);
                _ = ((delegate* unmanaged<IntPtr, int*, int>)(*(argsVtbl + WeaGetTotalTime)))(eventArgs, &totalTime);
                _ = ((delegate* unmanaged<IntPtr, int*, int>)(*(argsVtbl + WeaGetCurrentAction)))(eventArgs, &action);
                callback(action, startLba, sectorCount, lastWrittenLba, totalTime);
            }
            finally
            {
                ReleaseInstance(eventArgs);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Dispparams
        {
            public IntPtr Rgvarg;
            public IntPtr RgdispidNamedArgs;
            public uint CArgs;
            public uint CNamedArgs;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Variant
        {
            public ushort Vt;
            public ushort Reserved1;
            public ushort Reserved2;
            public ushort Reserved3;
            public IntPtr Data;
            public IntPtr Reserved4;
        }
    }
}