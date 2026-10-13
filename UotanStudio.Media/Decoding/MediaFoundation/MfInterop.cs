using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.MediaFoundation;

/// <summary>
/// MediaFoundation interop helpers: functions, GUIDs and error codes.
/// <para>MediaFoundation 互操作辅助：函数、GUID 与错误码。</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class MfInterop
{
    private const string MfPlat = "mfplat.dll";

    /// <summary>
    /// MF_VERSION for Windows 8+ (0x0002 &lt;&lt; 16 | 0x0070).
    /// <para>MF_VERSION（Windows 8+，0x0002 &lt;&lt; 16 | 0x0070）。</para>
    /// </summary>
    public const uint MfVersion = 0x0002_0070;

    /// <summary>
    /// COINIT_MULTITHREADED.
    /// <para>COINIT_MULTITHREADED。</para>
    /// </summary>
    public const uint CoInitMultithreaded = 0x0;

    [LibraryImport(MfPlat)]
    public static partial int MFStartup(uint version, uint dwFlags);

    [LibraryImport(MfPlat)]
    public static partial int MFShutdown();

    [LibraryImport(MfPlat)]
    public static partial int MFCreateMediaType(out IntPtr ppMediaType);

    [LibraryImport(MfPlat)]
    public static partial int MFCreateSample(out IntPtr ppSample);

    [LibraryImport(MfPlat)]
    public static partial int MFCreateMemoryBuffer(uint cbMaxLength, out IntPtr ppBuffer);

    [LibraryImport(MfPlat)]
    public static partial int MFCreateAlignedMemoryBuffer(uint cbMaxLength, uint cbAligment, out IntPtr ppBuffer);

    /// <summary>
    /// MFCreateDXGIDeviceManager — creates the shared D3D11 device manager.
    /// <para>MFCreateDXGIDeviceManager——创建共享的 D3D11 设备管理器。</para>
    /// </summary>
    /// <param name="resetToken">Reset token to be handed to <c>IMFDXGIDeviceManager.ResetDevice</c>. <para>需交给 <c>IMFDXGIDeviceManager.ResetDevice</c> 使用的重置令牌。</para></param>
    /// <param name="ppDeviceManager">IMFDXGIDeviceManager pointer with one reference held on success. <para>成功时持有一个引用的 IMFDXGIDeviceManager 指针。</para></param>
    [LibraryImport(MfPlat)]
    public static partial int MFCreateDXGIDeviceManager(out uint resetToken, out IntPtr ppDeviceManager);

    [LibraryImport(MfPlat)]
    public static partial int MFTEnumEx(
        Guid guidCategory,
        uint enumFlags,
        in MftRegisterTypeInfo inputType,
        IntPtr pOutputType,
        out IntPtr ppMFTActivate,
        out uint pnumMFTActivate);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    /// <summary>
    /// MFMediaType_Video ('vids').
    /// <para>MFMediaType_Video（'vids'）。</para>
    /// </summary>
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");

    /// <summary>
    /// MFVideoFormat_H264 (FCC 'H264').
    /// <para>MFVideoFormat_H264（FCC 'H264'）。</para>
    /// </summary>
    public static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");

    /// <summary>
    /// MFVideoFormat_HEVC (FCC 'HEVC').
    /// <para>MFVideoFormat_HEVC（FCC 'HEVC'）。</para>
    /// </summary>
    public static readonly Guid VideoFormatHevc = new("43564548-0000-0010-8000-00AA00389B71");

    /// <summary>
    /// MFVideoFormat_NV12 (FCC 'NV12').
    /// <para>MFVideoFormat_NV12（FCC 'NV12'）。</para>
    /// </summary>
    public static readonly Guid VideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");

    /// <summary>
    /// MFVideoFormat_ARGB32 (D3DFMT_A8R8G8B8 = 0x15).
    /// <para>MFVideoFormat_ARGB32（D3DFMT_A8R8G8B8 = 0x15）。</para>
    /// </summary>
    public static readonly Guid VideoFormatArgb32 = new("00000015-0000-0010-8000-00AA00389B71");

    /// <summary>
    /// MF_MT_MAJOR_TYPE.
    /// <para>MF_MT_MAJOR_TYPE。</para>
    /// </summary>
    public static readonly Guid MtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");

    /// <summary>
    /// MF_MT_SUBTYPE.
    /// <para>MF_MT_SUBTYPE。</para>
    /// </summary>
    public static readonly Guid MtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");

    /// <summary>
    /// MF_MT_FRAME_SIZE.
    /// <para>MF_MT_FRAME_SIZE。</para>
    /// </summary>
    public static readonly Guid MtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");

    /// <summary>
    /// MF_MT_DEFAULT_STRIDE.
    /// <para>MF_MT_DEFAULT_STRIDE。</para>
    /// </summary>
    public static readonly Guid MtDefaultStride = new("644b4c48-b489-4089-8cec-9f9e3caa1ee7");

    /// <summary>
    /// MF_MT_VIDEO_NOMINAL_RANGE (MFNominalRange: 1 = 0-255, 2 = 16-235).
    /// <para>MF_MT_VIDEO_NOMINAL_RANGE（MFNominalRange：1 = 0-255，2 = 16-235）。</para>
    /// </summary>
    public static readonly Guid MtVideoNominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");

    /// <summary>
    /// MF_MT_YUV_MATRIX (MFYUVMatrix: 1 = BT.709, 2 = BT.601).
    /// <para>MF_MT_YUV_MATRIX（MFYUVMatrix：1 = BT.709，2 = BT.601）。</para>
    /// </summary>
    public static readonly Guid MtYuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");

    /// <summary>
    /// MFMediaType_Audio ('auds').
    /// <para>MFMediaType_Audio（'auds'）。</para>
    /// </summary>
    public static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");

    /// <summary>
    /// MFAudioFormat_AAC (0x1610).
    /// <para>MFAudioFormat_AAC（0x1610）。</para>
    /// </summary>
    public static readonly Guid AudioFormatAac = new("00001610-0000-0010-8000-00AA00389B71");

    /// <summary>
    /// MFAudioFormat_PCM (0x0001).
    /// <para>MFAudioFormat_PCM（0x0001）。</para>
    /// </summary>
    public static readonly Guid AudioFormatPcm = new("00000001-0000-0010-8000-00AA00389B71");

    /// <summary>
    /// MF_MT_USER_DATA (codec private data, e.g. AAC AudioSpecificConfig).
    /// <para>MF_MT_USER_DATA（编解码器私有数据，如 AAC AudioSpecificConfig）。</para>
    /// </summary>
    public static readonly Guid MtUserData = new("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");

    /// <summary>
    /// MF_MT_AUDIO_NUM_CHANNELS.
    /// <para>MF_MT_AUDIO_NUM_CHANNELS。</para>
    /// </summary>
    public static readonly Guid MtAudioNumChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");

    /// <summary>
    /// MF_MT_AUDIO_SAMPLES_PER_SECOND.
    /// <para>MF_MT_AUDIO_SAMPLES_PER_SECOND。</para>
    /// </summary>
    public static readonly Guid MtAudioSamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");

    /// <summary>
    /// MF_MT_AUDIO_BITS_PER_SAMPLE.
    /// <para>MF_MT_AUDIO_BITS_PER_SAMPLE。</para>
    /// </summary>
    public static readonly Guid MtAudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");

    /// <summary>
    /// MFT_CATEGORY_AUDIO_DECODER.
    /// <para>MFT_CATEGORY_AUDIO_DECODER。</para>
    /// </summary>
    public static readonly Guid CategoryAudioDecoder = new("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7");

    /// <summary>
    /// MFT_CATEGORY_VIDEO_DECODER.
    /// <para>MFT_CATEGORY_VIDEO_DECODER。</para>
    /// </summary>
    public static readonly Guid CategoryVideoDecoder = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");

    /// <summary>
    /// CLSID_VideoProcessorMFT (msvproc.dll).
    /// <para>CLSID_VideoProcessorMFT（msvproc.dll）。</para>
    /// </summary>
    public static readonly Guid ClsidVideoProcessorMft = new("88753b26-5b24-49bd-b2e7-0c445c78c982");

    /// <summary>
    /// IID_IMFMediaEventGenerator.
    /// <para>IID_IMFMediaEventGenerator。</para>
    /// </summary>
    public static readonly Guid IidImfMediaEventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d");

    /// <summary>
    /// IID_IUnknown.
    /// <para>IID_IUnknown。</para>
    /// </summary>
    public static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");

    /// <summary>
    /// MF_TRANSFORM_ASYNC.
    /// <para>MF_TRANSFORM_ASYNC。</para>
    /// </summary>
    public static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");

    /// <summary>
    /// MF_TRANSFORM_ASYNC_UNLOCK.
    /// <para>MF_TRANSFORM_ASYNC_UNLOCK。</para>
    /// </summary>
    public static readonly Guid TransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");

    /// <summary>
    /// MF_SA_D3D11_AWARE.
    /// <para>MF_SA_D3D11_AWARE。</para>
    /// </summary>
    public static readonly Guid SaD3D11Aware = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");

    /// <summary>
    /// IID_IMFDXGIDeviceManager.
    /// <para>IID_IMFDXGIDeviceManager。</para>
    /// </summary>
    public static readonly Guid IidDxgiDeviceManager = new("eb533d5d-2db6-40f8-97a9-494692014f07");

    /// <summary>
    /// IID_IMFTransform.
    /// <para>IID_IMFTransform。</para>
    /// </summary>
    public static readonly Guid IidImfTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    /// <summary>
    /// MFT_ENUM_FLAG_SYNCMFT | LOCALMFT | SORTANDFILTER.
    /// <para>MFT_ENUM_FLAG_SYNCMFT | LOCALMFT | SORTANDFILTER。</para>
    /// </summary>
    public const uint MftEnumFlags = 0x01 | 0x10 | 0x40;

    /// <summary>
    /// MFT_ENUM_FLAG_ASYNCMFT | HARDWARE | SORTANDFILTER — async hardware decoders only.
    /// <para>MFT_ENUM_FLAG_ASYNCMFT | HARDWARE | SORTANDFILTER —— 仅异步硬件解码器。</para>
    /// </summary>
    public const uint MftEnumFlagsAsyncHardware = 0x02 | 0x04 | 0x40;

    /// <summary>
    /// MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.
    /// <para>MFT_OUTPUT_STREAM_PROVIDES_SAMPLES。</para>
    /// </summary>
    public const uint MftOutputStreamProvidesSamples = 0x100;

    /// <summary>
    /// MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE.
    /// <para>MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE。</para>
    /// </summary>
    public const uint MftOutputDataBufferFormatChange = 0x100;

    /// <summary>
    /// MFT_MESSAGE_COMMAND_FLUSH.
    /// <para>MFT_MESSAGE_COMMAND_FLUSH。</para>
    /// </summary>
    public const uint MftMessageFlush = 0;

    /// <summary>
    /// MFT_MESSAGE_NOTIFY_BEGIN_STREAMING.
    /// <para>MFT_MESSAGE_NOTIFY_BEGIN_STREAMING。</para>
    /// </summary>
    public const uint MftMessageBeginStreaming = 0x10000000;

    /// <summary>
    /// MFT_MESSAGE_NOTIFY_START_OF_STREAM.
    /// <para>MFT_MESSAGE_NOTIFY_START_OF_STREAM。</para>
    /// </summary>
    public const uint MftMessageStartOfStream = 0x10000003;

    /// <summary>
    /// MFT_MESSAGE_NOTIFY_END_OF_STREAM.
    /// <para>MFT_MESSAGE_NOTIFY_END_OF_STREAM。</para>
    /// </summary>
    public const uint MftMessageNotifyEndOfStream = 0x10000002;

    /// <summary>
    /// MFT_MESSAGE_COMMAND_DRAIN.
    /// <para>MFT_MESSAGE_COMMAND_DRAIN。</para>
    /// </summary>
    public const uint MftMessageCommandDrain = 0x1;

    public const int SOk = 0;
    public const int SFalse = 1;
    public const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    public const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    public const int MfENotAccepting = unchecked((int)0xC00D36B5);
    public const int MfEAttributeNotFound = unchecked((int)0xC00D36E6);
    public const int MfENoMoreTypes = unchecked((int)0xC00D36B9);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftRegisterTypeInfo(Guid majorType, Guid subtype)
{
    public Guid MajorType = majorType;
    public Guid Subtype = subtype;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputDataBuffer
{
    public uint StreamId;
    public nint Sample;
    public uint Status;
    public nint Events;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputStreamInfo
{
    public uint Flags;
    public uint Size;
    public uint Alignment;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct MfVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public short Boolean;
    [FieldOffset(8)] public uint UInt32;
}

internal static unsafe class MfCom
{
    private static void* Slot(nint value, int index) => (*(void***)value)[index];
    /// <summary>
    /// Reads one vtable slot; exposed for the few calls defined outside this class.
    /// <para>读取一个 vtable 槽位；供少数在本类之外定义的调用使用。</para>
    /// </summary>
    internal static void* PublicSlot(nint value, int index) => Slot(value, index);
    public static int QueryInterface(nint value, in Guid iid, out nint result)
    {
        fixed (Guid* key = &iid) fixed (nint* p = &result)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(value, 0))(value, key, p);
    }
    public static int SetCodecLowLatency(nint value, in Guid key, bool useUInt32)
    {
        var variant = useUInt32 ? new MfVariant { Type = 19, UInt32 = 1 }
            : new MfVariant { Type = 11, Boolean = -1 };
        fixed (Guid* k = &key)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, MfVariant*, int>)Slot(value, 9))(value, k, &variant);
    }
    public static int GetAttributes(nint value, out nint attributes)
    {
        fixed (nint* p = &attributes)
            return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(value, 8))(value, p);
    }
    public static int SetUInt32(nint value, in Guid key, uint data)
    {
        fixed (Guid* k = &key)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(value, 21))(value, k, data);
    }
    public static int SetBytes(nint value, in Guid key, ReadOnlySpan<byte> data)
    {
        fixed (Guid* k = &key) fixed (byte* d = data)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, byte*, uint, int>)Slot(value, 26))(value, k, d, (uint)data.Length);
    }

    /// <summary>
    /// IMFAttributes.SetUnknown (slot 27).
    /// <para>IMFAttributes.SetUnknown（槽位 27）。</para>
    /// </summary>
    public static int SetUnknown(nint value, in Guid key, nint unknown)
    {
        fixed (Guid* k = &key)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint, int>)Slot(value, 27))(value, k, unknown);
    }
    public static void Release(nint value)
    {
        if (value != 0) _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(value, 2))(value);
    }
    public static int SetGuid(nint value, in Guid key, in Guid data)
    {
        fixed (Guid* k = &key, d = &data)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(value, 24))(value, k, d);
    }
    public static int GetGuid(nint value, in Guid key, out Guid data)
    {
        fixed (Guid* k = &key, d = &data)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(value, 10))(value, k, d);
    }
    public static int GetUInt32(nint value, in Guid key, out uint data)
    {
        fixed (Guid* k = &key) fixed (uint* d = &data)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(value, 7))(value, k, d);
    }
    public static int GetUInt64(nint value, in Guid key, out ulong data)
    {
        fixed (Guid* k = &key) fixed (ulong* d = &data)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(value, 8))(value, k, d);
    }
    public static int SetUInt64(nint value, in Guid key, ulong data)
    {
        fixed (Guid* k = &key)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(value, 22))(value, k, data);
    }
    public static int Activate(nint value, in Guid iid, out nint instance)
    {
        fixed (Guid* k = &iid) fixed (nint* d = &instance)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(value, 33))(value, k, d);
    }
    public static int Message(nint value, uint message, nint parameter = 0) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Slot(value, 23))(value, message, (nuint)parameter);
    public static int SetType(nint value, nint type, bool input) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(value, input ? 15 : 16))(value, 0, type, 0);
    public static int OutputType(nint value, uint index, out nint type)
    {
        fixed (nint* t = &type)
            return ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(value, 14))(value, 0, index, t);
    }
    public static int OutputInfo(nint value, out MftOutputStreamInfo info)
    {
        fixed (MftOutputStreamInfo* p = &info)
            return ((delegate* unmanaged[Stdcall]<nint, uint, MftOutputStreamInfo*, int>)Slot(value, 7))(value, 0, p);
    }
    public static int Input(nint value, nint sample) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(value, 24))(value, 0, sample, 0);
    public static int Output(nint value, ref MftOutputDataBuffer buffer)
    {
        uint status;
        fixed (MftOutputDataBuffer* p = &buffer)
            return ((delegate* unmanaged[Stdcall]<nint, uint, uint, MftOutputDataBuffer*, uint*, int>)Slot(value, 25))(value, 0, 1, p, &status);
    }
    public static int AddBuffer(nint value, nint buffer) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(value, 42))(value, buffer);
    public static int ContiguousBuffer(nint value, out nint buffer)
    {
        fixed (nint* p = &buffer)
            return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(value, 41))(value, p);
    }
    public static int SetTime(nint value, long time) =>
        ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(value, 36))(value, time);
    public static int GetTime(nint value, out long time)
    {
        fixed (long* p = &time)
            return ((delegate* unmanaged[Stdcall]<nint, long*, int>)Slot(value, 35))(value, p);
    }
    public static int Lock(nint value, out nint data, out uint length)
    {
        uint capacity;
        fixed (nint* p = &data) fixed (uint* n = &length)
            return ((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, int>)Slot(value, 3))(value, p, &capacity, n);
    }
    public static int Unlock(nint value) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(value, 4))(value);
    public static int SetLength(nint value, uint length) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(value, 6))(value, length);

    /// <summary>
    /// IMFMediaEventGenerator.GetEvent (slot 3).
    /// <para>IMFMediaEventGenerator.GetEvent（槽位 3）。</para>
    /// </summary>
    public static int GetEvent(nint eventGenerator, uint eventFlags, out nint mediaEvent)
    {
        fixed (nint* e = &mediaEvent)
            return ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(eventGenerator, 3))(eventGenerator, eventFlags, e);
    }

    /// <summary>
    /// IMFMediaEvent.GetType (slot 33; out-parameter form).
    /// <para>IMFMediaEvent.GetType（槽位 33；out 参数形式）。</para>
    /// </summary>
    /// <param name="mediaEvent">IMFMediaEvent interface pointer. <para>IMFMediaEvent 接口指针。</para></param>
    /// <param name="eventType">Event type code, e.g. <see cref="D3D11Interop.MeTransformNeedInput"/>. <para>事件类型码，例如 <see cref="D3D11Interop.MeTransformNeedInput"/>。</para></param>
    public static int EventType(nint mediaEvent, out uint eventType)
    {
        fixed (uint* t = &eventType)
            return ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(mediaEvent, 33))(mediaEvent, t);
    }

    /// <summary>
    /// IMFMediaEvent.GetUINT32 (slot 7).
    /// <para>IMFMediaEvent.GetUINT32（槽位 7）。</para>
    /// </summary>
    /// <param name="mediaEvent">IMFMediaEvent interface pointer. <para>IMFMediaEvent 接口指针。</para></param>
    /// <param name="key">Attribute GUID read from the event payload. <para>从事件负载读取的属性 GUID。</para></param>
    /// <param name="value">UINT32 attribute value read from the event. <para>从事件读出的 UINT32 属性值。</para></param>
    public static int EventUInt32(nint mediaEvent, in Guid key, out uint value)
    {
        fixed (Guid* k = &key) fixed (uint* v = &value)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(mediaEvent, 7))(mediaEvent, k, v);
    }

    /// <summary>
    /// IMFSample.GetBufferByIndex (slot 40).
    /// <para>IMFSample.GetBufferByIndex（槽位 40）。</para>
    /// </summary>
    public static int GetBufferByIndex(nint sample, uint index, out nint buffer)
    {
        fixed (nint* b = &buffer)
            return ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(sample, 40))(sample, index, b);
    }

    /// <summary>
    /// IMFSample.GetBufferCount (slot 39; out-parameter form).
    /// <para>IMFSample.GetBufferCount（槽位 39；out 参数形式）。</para>
    /// </summary>
    /// <param name="sample">IMFSample interface pointer. <para>IMFSample 接口指针。</para></param>
    /// <param name="count">Number of buffers attached to the sample; 0 means the sample carries no data. <para>样例挂载的缓冲区数量；0 表示样例没有数据。</para></param>
    public static int BufferCount(nint sample, out uint count)
    {
        fixed (uint* c = &count)
            return ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(sample, 39))(sample, c);
    }

    /// <summary>
    /// IMFAttributes.GetUnknown (slot 17).
    /// <para>IMFAttributes.GetUnknown（槽位 17）。</para>
    /// </summary>
    public static int GetUnknown(nint attributes, in Guid key, in Guid iid, out nint result)
    {
        fixed (Guid* k = &key, i = &iid) fixed (nint* r = &result)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, nint*, int>)Slot(attributes, 17))(attributes, k, i, r);
    }

    /// <summary>
    /// IMFDXGIBuffer.GetResource (slot 3).
    /// <para>IMFDXGIBuffer.GetResource（槽位 3）。</para>
    /// </summary>
    /// <param name="dxgiBuffer">IMFDXGIBuffer interface pointer. <para>IMFDXGIBuffer 接口指针。</para></param>
    /// <param name="iid">Interface GUID of the requested resource, e.g. <see cref="D3D11Interop.IidTexture2D"/>. <para>所需资源的接口 GUID，如 <see cref="D3D11Interop.IidTexture2D"/>。</para></param>
    /// <param name="resource">Resource interface pointer with one reference held on success; released by the caller. <para>成功时持有一个引用的资源接口指针，由调用方释放。</para></param>
    public static int DxgiGetResource(nint dxgiBuffer, in Guid iid, out nint resource)
    {
        fixed (Guid* i = &iid) fixed (nint* r = &resource)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(dxgiBuffer, 3))(dxgiBuffer, i, r);
    }

    /// <summary>
    /// IMFDXGIBuffer.GetSubresourceIndex (slot 4; out-parameter form).
    /// <para>IMFDXGIBuffer.GetSubresourceIndex（槽位 4；out 参数形式）。</para>
    /// </summary>
    /// <param name="dxgiBuffer">IMFDXGIBuffer interface pointer. <para>IMFDXGIBuffer 接口指针。</para></param>
    /// <param name="subresource">Texture-array slice holding the sample planes, validated against the texture array size by the caller. <para>承载样例平面的纹理数组切片，调用方会按纹理数组大小校验该值。</para></param>
    public static int DxgiSubresourceIndex(nint dxgiBuffer, out uint subresource)
    {
        fixed (uint* s = &subresource)
            return ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(dxgiBuffer, 4))(dxgiBuffer, s);
    }

    /// <summary>
    /// IMFDXGIDeviceManager.ResetDevice (slot 7).
    /// <para>IMFDXGIDeviceManager.ResetDevice（槽位 7）。</para>
    /// </summary>
    /// <param name="deviceManager">IMFDXGIDeviceManager interface pointer. <para>IMFDXGIDeviceManager 接口指针。</para></param>
    /// <param name="device">ID3D11Device pointer the manager binds to Media Foundation. <para>交给 Media Foundation 使用的 ID3D11Device 指针。</para></param>
    /// <param name="token">Reset token returned by <see cref="MfInterop.MFCreateDXGIDeviceManager"/>. <para><see cref="MfInterop.MFCreateDXGIDeviceManager"/> 返回的重置令牌。</para></param>
    public static int DxgiResetDevice(nint deviceManager, nint device, uint token) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Slot(deviceManager, 7))(deviceManager, device, token);

    /// <summary>
    /// ID3D11Texture2D.GetDesc (slot 10), including padded texture dimensions.
    /// <para>ID3D11Texture2D.GetDesc（槽位 10），返回含填充后的纹理尺寸。</para>
    /// </summary>
    /// <param name="texture">ID3D11Texture2D interface pointer. <para>ID3D11Texture2D 接口指针。</para></param>
    public static D3D11Interop.Texture2DDesc D3DGetTexture2DDesc(nint texture)
    {
        D3D11Interop.Texture2DDesc desc = default;
        ((delegate* unmanaged[Stdcall]<nint, D3D11Interop.Texture2DDesc*, void>)Slot(texture, 10))(texture, &desc);
        return desc;
    }

    /// <summary>
    /// ID3D11Device.CreateTexture2D (slot 5).
    /// <para>ID3D11Device.CreateTexture2D（槽位 5）。</para>
    /// </summary>
    /// <param name="device">ID3D11Device interface pointer. <para>ID3D11Device 接口指针。</para></param>
    /// <param name="desc">
    /// Texture layout: dimensions, <c>DXGI_FORMAT</c>, usage, bind and CPU-access flags.
    /// <para>纹理布局：尺寸、<c>DXGI_FORMAT</c>、usage、bind 与 CPU 访问标志。</para>
    /// </param>
    /// <param name="initialData">Pointer to <c>D3D11_SUBRESOURCE_DATA</c>; 0 creates an uninitialized texture. <para>指向 <c>D3D11_SUBRESOURCE_DATA</c> 的指针；0 表示创建未初始化纹理。</para></param>
    /// <param name="texture">Created ID3D11Texture2D pointer with one reference held on success. <para>成功时持有 1 个引用的新建 ID3D11Texture2D 指针。</para></param>
    public static int D3DCreateTexture2D(nint device, in D3D11Interop.Texture2DDesc desc, nint initialData, out nint texture)
    {
        fixed (D3D11Interop.Texture2DDesc* d = &desc) fixed (nint* t = &texture)
            return ((delegate* unmanaged[Stdcall]<nint, D3D11Interop.Texture2DDesc*, nint, nint*, int>)Slot(device, 5))(device, d, initialData, t);
    }

    /// <summary>
    /// ID3D11Device.QueryInterface via MfCom.QueryInterface.
    /// <para>ID3D11Device.QueryInterface，经 MfCom.QueryInterface 转发。</para>
    /// </summary>
    /// <param name="value">ID3D11Device interface pointer. <para>ID3D11Device 接口指针。</para></param>
    /// <param name="iid">Interface GUID requested from the device. <para>向设备请求的接口 GUID。</para></param>
    /// <param name="result">Queried interface pointer with one reference held on success. <para>成功时持有 1 个引用的查询结果接口指针。</para></param>
    public static int D3DQueryInterface(nint value, in Guid iid, out nint result) => QueryInterface(value, iid, out result);

    /// <summary>
    /// ID3D11DeviceContext.Map (slot 14).
    /// <para>ID3D11DeviceContext.Map（槽位 14）。</para>
    /// </summary>
    /// <param name="context">ID3D11DeviceContext interface pointer. <para>ID3D11DeviceContext 接口指针。</para></param>
    /// <param name="resource">Staging resource to map; only <c>D3D11_USAGE_STAGING</c> textures are readable from the CPU. <para>待映射的 staging 资源；只有 <c>D3D11_USAGE_STAGING</c> 纹理可被 CPU 读取。</para></param>
    /// <param name="subresource">Subresource index within the resource, 0 for single-slice textures. <para>资源内的子资源索引，单切片纹理为 0。</para></param>
    /// <param name="mapType"><c>D3D11_MAP</c> value; the read-back paths pass 1 (<c>D3D11_MAP_READ</c>). <para><c>D3D11_MAP</c> 值，读回路径传 1（<c>D3D11_MAP_READ</c>）。</para></param>
    /// <param name="mapFlags"><c>D3D11_MAP_FLAG_*</c> bitmask; 0 for the current read-back paths. <para><c>D3D11_MAP_FLAG_*</c> 位掩码，当前读回路径传 0。</para></param>
    /// <param name="mapped">
    /// Data pointer plus row and depth pitch; valid until the matching Unmap call.
    /// <para>数据指针与行距、深度间距；在配对的 Unmap 调用之前有效。</para>
    /// </param>
    public static int D3DMap(nint context, nint resource, uint subresource, uint mapType, uint mapFlags, out D3D11Interop.MappedSubresource mapped)
    {
        fixed (D3D11Interop.MappedSubresource* m = &mapped)
            return ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, D3D11Interop.MappedSubresource*, int>)Slot(context, 14))(context, resource, subresource, mapType, mapFlags, m);
    }

    /// <summary>
    /// ID3D11DeviceContext.Unmap (slot 15).
    /// <para>ID3D11DeviceContext.Unmap（槽位 15）。</para>
    /// </summary>
    /// <param name="context">ID3D11DeviceContext interface pointer that performed the map. <para>执行本次映射的 ID3D11DeviceContext 接口指针。</para></param>
    /// <param name="resource">Resource passed to the matching Map call. <para>与配对 Map 调用相同的资源。</para></param>
    /// <param name="subresource">Subresource index handed back; the read-back paths use 0. <para>交还的子资源索引，读回路径使用 0。</para></param>
    public static void D3DUnmap(nint context, nint resource, uint subresource) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(context, 15))(context, resource, subresource);

    /// <summary>
    /// ID3D11DeviceContext.CopySubresourceRegion (slot 46).
    /// <para>ID3D11DeviceContext.CopySubresourceRegion（槽位 46）。</para>
    /// </summary>
    /// <param name="context">ID3D11DeviceContext interface pointer. <para>ID3D11DeviceContext 接口指针。</para></param>
    /// <param name="destination">Destination resource. <para>目标资源。</para></param>
    /// <param name="destinationSubresource">Destination subresource index. <para>目标子资源索引。</para></param>
    /// <param name="destinationX">Texel offset on the destination X axis. <para>目标 X 轴的纹素偏移。</para></param>
    /// <param name="destinationY">Texel offset on the destination Y axis. <para>目标 Y 轴的纹素偏移。</para></param>
    /// <param name="destinationZ">Texel offset on the destination Z axis or array slice. <para>目标 Z 轴或数组切片的纹素偏移。</para></param>
    /// <param name="source">Source resource. <para>源资源。</para></param>
    /// <param name="sourceSubresource">Source subresource index, typically the decoder buffer slice. <para>源子资源索引，通常为解码器缓冲区切片。</para></param>
    /// <param name="sourceBox">
    /// Pointer to a <c>D3D11_BOX</c> limiting the copied region; 0 copies the whole subresource.
    /// <para>限定拷贝区域的 <c>D3D11_BOX</c> 指针；0 表示复制整个子资源。</para>
    /// </param>
    public static void D3DCopySubresourceRegion(nint context, nint destination, uint destinationSubresource, uint destinationX, uint destinationY, uint destinationZ, nint source, uint sourceSubresource, nint sourceBox) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, nint, void>)Slot(context, 46))(context, destination, destinationSubresource, destinationX, destinationY, destinationZ, source, sourceSubresource, sourceBox);

    /// <summary>
    /// ID3D11VideoDevice.CreateVideoProcessorEnumerator (slot 10).
    /// <para>ID3D11VideoDevice.CreateVideoProcessorEnumerator（槽位 10）。</para>
    /// </summary>
    /// <param name="videoDevice">ID3D11VideoDevice interface pointer. <para>ID3D11VideoDevice 接口指针。</para></param>
    /// <param name="desc">
    /// Requested input/output sizes, frame rates and usage
    /// (<c>D3D11_VIDEO_PROCESSOR_CONTENT_DESC</c>).
    /// <para>请求的输入/输出尺寸、帧率与用途（<c>D3D11_VIDEO_PROCESSOR_CONTENT_DESC</c>）。</para>
    /// </param>
    /// <param name="enumerator">Created enumerator with one reference held on success; it also describes the supported caps. <para>成功时持有 1 个引用的新建枚举器，同时描述支持的能力。</para></param>
    public static int D3DCreateVideoProcessorEnumerator(nint videoDevice, in D3D11VideoProcessorContentDesc desc, out nint enumerator)
    {
        fixed (D3D11VideoProcessorContentDesc* d = &desc) fixed (nint* e = &enumerator)
            return ((delegate* unmanaged[Stdcall]<nint, D3D11VideoProcessorContentDesc*, nint*, int>)Slot(videoDevice, 10))(videoDevice, d, e);
    }

    /// <summary>
    /// ID3D11VideoDevice.CreateVideoProcessor (slot 4).
    /// <para>ID3D11VideoDevice.CreateVideoProcessor（槽位 4）。</para>
    /// </summary>
    /// <param name="videoDevice">ID3D11VideoDevice interface pointer. <para>ID3D11VideoDevice 接口指针。</para></param>
    /// <param name="enumerator">Enumerator describing the requested content. <para>描述所请求内容的枚举器。</para></param>
    /// <param name="rateConversionIndex">Rate-conversion quality index from the enumerator caps; 0 selects the first mode. <para>取自枚举器能力表的帧率转换质量索引；0 选择首个模式。</para></param>
    /// <param name="processor">Created ID3D11VideoProcessor with one reference held on success. <para>成功时持有 1 个引用的新建 ID3D11VideoProcessor。</para></param>
    public static int D3DCreateVideoProcessor(nint videoDevice, nint enumerator, uint rateConversionIndex, out nint processor)
    {
        fixed (nint* p = &processor)
            return ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint*, int>)Slot(videoDevice, 4))(videoDevice, enumerator, rateConversionIndex, p);
    }

    /// <summary>
    /// ID3D11VideoDevice.CreateVideoProcessorInputView (slot 8).
    /// <para>ID3D11VideoDevice.CreateVideoProcessorInputView（槽位 8）。</para>
    /// </summary>
    /// <param name="videoDevice">ID3D11VideoDevice interface pointer. <para>ID3D11VideoDevice 接口指针。</para></param>
    /// <param name="resource">Decoder texture holding the source NV12 planes. <para>承载源 NV12 平面的解码器纹理。</para></param>
    /// <param name="enumerator">Enumerator the view is validated against. <para>用于校验该视图的枚举器。</para></param>
    /// <param name="desc">
    /// View dimension and the texture mip slice / array slice to read.
    /// <para>视图维度与要读取的纹理 mip 切片/数组切片。</para>
    /// </param>
    /// <param name="view">Created input view with one reference held on success; released by the caller. <para>成功时持有 1 个引用的新建输入视图，由调用方释放。</para></param>
    public static int D3DCreateVideoProcessorInputView(nint videoDevice, nint resource, nint enumerator, in D3D11Interop.VideoProcessorInputViewDesc desc, out nint view)
    {
        fixed (D3D11Interop.VideoProcessorInputViewDesc* d = &desc) fixed (nint* v = &view)
            return ((delegate* unmanaged[Stdcall]<nint, nint, nint, D3D11Interop.VideoProcessorInputViewDesc*, nint*, int>)Slot(videoDevice, 8))(videoDevice, resource, enumerator, d, v);
    }

    /// <summary>
    /// ID3D11VideoDevice.CreateVideoProcessorOutputView (slot 9).
    /// <para>ID3D11VideoDevice.CreateVideoProcessorOutputView（槽位 9）。</para>
    /// </summary>
    /// <param name="videoDevice">ID3D11VideoDevice interface pointer. <para>ID3D11VideoDevice 接口指针。</para></param>
    /// <param name="resource">Destination texture, e.g. the BGRA output or a shared export-slot texture. <para>目标纹理，如 BGRA 输出纹理或共享导出槽纹理。</para></param>
    /// <param name="enumerator">Enumerator the view is validated against. <para>用于校验该视图的枚举器。</para></param>
    /// <param name="desc">
    /// View dimension and the mip slice / array range written by the blit.
    /// <para>视图维度与 blit 写入的 mip 切片/数组范围。</para>
    /// </param>
    /// <param name="view">Created output view with one reference held on success. <para>成功时持有 1 个引用的新建输出视图。</para></param>
    public static int D3DCreateVideoProcessorOutputView(nint videoDevice, nint resource, nint enumerator, in D3D11Interop.VideoProcessorOutputViewDesc desc, out nint view)
    {
        fixed (D3D11Interop.VideoProcessorOutputViewDesc* d = &desc) fixed (nint* v = &view)
            return ((delegate* unmanaged[Stdcall]<nint, nint, nint, D3D11Interop.VideoProcessorOutputViewDesc*, nint*, int>)Slot(videoDevice, 9))(videoDevice, resource, enumerator, d, v);
    }

    /// <summary>
    /// ID3D11VideoContext.VideoProcessorBlt (slot 53).
    /// <para>ID3D11VideoContext.VideoProcessorBlt（槽位 53）。</para>
    /// </summary>
    /// <param name="videoContext">ID3D11VideoContext interface pointer. <para>ID3D11VideoContext 接口指针。</para></param>
    /// <param name="processor">Video processor configured with the stream color spaces. <para>已按流色彩空间配置的处理器。</para></param>
    /// <param name="outputView">Output view receiving the converted frame. <para>接收转换后帧的输出视图。</para></param>
    /// <param name="outputFrame">Presentation index assigned to the produced frame. <para>为产出帧指定的呈现索引。</para></param>
    /// <param name="streamCount">Number of <c>D3D11_VIDEO_PROCESSOR_STREAM</c> entries read from <paramref name="stream"/>; the decode path passes 1. <para>从 <paramref name="stream"/> 读取的 <c>D3D11_VIDEO_PROCESSOR_STREAM</c> 条目数；解码路径传 1。</para></param>
    /// <param name="stream">
    /// First stream descriptor: enabled flag and the input view pointer.
    /// <para>首个流描述符：启用标志与输入视图指针。</para>
    /// </param>
    public static int D3DVideoProcessorBlt(nint videoContext, nint processor, nint outputView, uint outputFrame, uint streamCount, in D3D11Interop.VideoProcessorStream stream)
    {
        fixed (D3D11Interop.VideoProcessorStream* s = &stream)
            return ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, uint, D3D11Interop.VideoProcessorStream*, int>)Slot(videoContext, 53))(videoContext, processor, outputView, outputFrame, streamCount, s);
    }

    /// <summary>
    /// ID3D11VideoContext.VideoProcessorSetStreamFrameFormat (slot 27).
    /// <para>ID3D11VideoContext.VideoProcessorSetStreamFrameFormat（槽位 27）。</para>
    /// </summary>
    /// <param name="videoContext">ID3D11VideoContext interface pointer. <para>ID3D11VideoContext 接口指针。</para></param>
    /// <param name="processor">Video processor whose stream is configured. <para>待配置流的视频处理器。</para></param>
    /// <param name="streamIndex">Zero-based input stream index. <para>从 0 开始的输入流索引。</para></param>
    /// <param name="frameFormat"><c>D3D11_VIDEO_FRAME_FORMAT</c> value; <see cref="D3D11Interop.VideoFrameFormatProgressive"/> (0) is passed for decoded progressive frames. <para><c>D3D11_VIDEO_FRAME_FORMAT</c> 值；解码的逐行帧传入 <see cref="D3D11Interop.VideoFrameFormatProgressive"/>（0）。</para></param>
    public static void D3DVideoProcessorSetStreamFrameFormat(nint videoContext, nint processor, uint streamIndex, uint frameFormat) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, void>)Slot(videoContext, 27))(videoContext, processor, streamIndex, frameFormat);

    /// <summary>
    /// ID3D11VideoContext.VideoProcessorSetStreamColorSpace (slot 28).
    /// <para>ID3D11VideoContext.VideoProcessorSetStreamColorSpace（槽位 28）。</para>
    /// </summary>
    /// <param name="videoContext">ID3D11VideoContext interface pointer. <para>ID3D11VideoContext 接口指针。</para></param>
    /// <param name="processor">Video processor whose stream is configured. <para>待配置流的视频处理器。</para></param>
    /// <param name="streamIndex">Zero-based input stream index. <para>从 0 开始的输入流索引。</para></param>
    /// <param name="colorSpace">
    /// Packed <c>D3D11_VIDEO_PROCESSOR_COLOR_SPACE</c> describing the source YUV matrix and
    /// nominal range.
    /// <para>描述源 YUV 矩阵与标称范围的打包 <c>D3D11_VIDEO_PROCESSOR_COLOR_SPACE</c>。</para>
    /// </param>
    public static void D3DVideoProcessorSetStreamColorSpace(nint videoContext, nint processor, uint streamIndex, in D3D11VideoProcessorColorSpace colorSpace)
    {
        fixed (D3D11VideoProcessorColorSpace* c = &colorSpace)
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, D3D11VideoProcessorColorSpace*, void>)Slot(videoContext, 28))(videoContext, processor, streamIndex, c);
    }

    /// <summary>
    /// ID3D11VideoContext.VideoProcessorSetOutputColorSpace (slot 15).
    /// <para>ID3D11VideoContext.VideoProcessorSetOutputColorSpace（槽位 15）。</para>
    /// </summary>
    /// <param name="videoContext">ID3D11VideoContext interface pointer. <para>ID3D11VideoContext 接口指针。</para></param>
    /// <param name="processor">Video processor whose output is configured. <para>待配置输出的视频处理器。</para></param>
    /// <param name="colorSpace">
    /// Packed color space for the destination surface; the decode path selects the full
    /// 0-255 nominal range.
    /// <para>目标表面的打包色彩空间；解码路径选择 0-255 的完整标称范围。</para>
    /// </param>
    public static void D3DVideoProcessorSetOutputColorSpace(nint videoContext, nint processor, in D3D11VideoProcessorColorSpace colorSpace)
    {
        fixed (D3D11VideoProcessorColorSpace* c = &colorSpace)
            ((delegate* unmanaged[Stdcall]<nint, nint, D3D11VideoProcessorColorSpace*, void>)Slot(videoContext, 15))(videoContext, processor, c);
    }

    /// <summary>
    /// ID3D11VideoContext.VideoProcessorSetStreamAutoProcessingMode (slot 37).
    /// <para>ID3D11VideoContext.VideoProcessorSetStreamAutoProcessingMode（槽位 37）。</para>
    /// </summary>
    /// <param name="videoContext">ID3D11VideoContext interface pointer. <para>ID3D11VideoContext 接口指针。</para></param>
    /// <param name="processor">Video processor whose stream is configured. <para>待配置流的视频处理器。</para></param>
    /// <param name="streamIndex">Zero-based input stream index. <para>从 0 开始的输入流索引。</para></param>
    /// <param name="enable">Win32 <c>BOOL</c> as an int; the decode path passes 0 so the explicit frame format and color space settings are used. <para>以 int 表示的 Win32 <c>BOOL</c>；解码路径传 0，使显式设置的帧格式与色彩空间生效。</para></param>
    public static void D3DVideoProcessorSetStreamAutoProcessingMode(nint videoContext, nint processor, uint streamIndex, int enable) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, int, void>)Slot(videoContext, 37))(videoContext, processor, streamIndex, enable);
}

/// <summary>
/// D3D11_VIDEO_PROCESSOR_CONTENT_DESC.
/// <para>D3D11_VIDEO_PROCESSOR_CONTENT_DESC。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11VideoProcessorContentDesc
{
    public uint InputFrameFormat;
    public uint InputFrameRateNumerator;
    public uint InputFrameRateDenominator;
    public uint InputWidth;
    public uint InputHeight;
    public uint OutputFrameRateNumerator;
    public uint OutputFrameRateDenominator;
    public uint OutputWidth;
    public uint OutputHeight;
    public uint Usage;
}

/// <summary>
/// D3D11_VIDEO_PROCESSOR_COLOR_SPACE (d3d11.h bitfields, 4-byte layout).
/// <para>D3D11_VIDEO_PROCESSOR_COLOR_SPACE（d3d11.h 位域，4 字节布局）。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11VideoProcessorColorSpace
{
    public uint Bits;
    /// <summary>
    /// Creates the native bitfield value defined by d3d11.h.
    /// <para>按 d3d11.h 定义构造原生位域值。</para>
    /// </summary>
    /// <param name="bits">
    /// Packed <c>D3D11_VIDEO_PROCESSOR_COLOR_SPACE</c> bits; bit 2 carries the YCbCr matrix
    /// (set = BT.709) and bits 4-5 the nominal range (2 = 0-255, 1 = 16-235).
    /// <para>打包的 <c>D3D11_VIDEO_PROCESSOR_COLOR_SPACE</c> 位；bit 2 为 YCbCr 矩阵（置位表示 BT.709），
    /// bit 4-5 为标称范围（2 = 0-255，1 = 16-235）。</para>
    /// </param>
    public static D3D11VideoProcessorColorSpace FromBits(uint bits) => new() { Bits = bits };
}