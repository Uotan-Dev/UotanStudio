using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.MediaFoundation;

/// <summary>MediaFoundation interop helpers: functions, GUIDs and error codes.</summary>
/// <summary>MediaFoundation 互操作辅助：函数、GUID 与错误码。</summary>
[SupportedOSPlatform("windows")]
internal static partial class MfInterop
{
    private const string MfPlat = "mfplat.dll";

    /// <summary>MF_VERSION for Windows 8+ (0x0002 &lt;&lt; 16 | 0x0070).</summary>
    /// <summary>MF_VERSION（Windows 8+，0x0002 &lt;&lt; 16 | 0x0070）。</summary>
    public const uint MfVersion = 0x0002_0070;

    /// <summary>COINIT_MULTITHREADED.</summary>
    /// <summary>COINIT_MULTITHREADED。</summary>
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

    /// <summary>MFCreateDXGIDeviceManager — creates the shared D3D11 device manager.</summary>
    /// <summary>MFCreateDXGIDeviceManager — 创建共享的 D3D11 设备管理器。</summary>
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

    /// <summary>MFMediaType_Video ('vids').</summary>
    /// <summary>MFMediaType_Video（'vids'）。</summary>
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");

    /// <summary>MFVideoFormat_H264 (FCC 'H264').</summary>
    /// <summary>MFVideoFormat_H264（FCC 'H264'）。</summary>
    public static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");

    /// <summary>MFVideoFormat_HEVC (FCC 'HEVC').</summary>
    /// <summary>MFVideoFormat_HEVC（FCC 'HEVC'）。</summary>
    public static readonly Guid VideoFormatHevc = new("43564548-0000-0010-8000-00AA00389B71");

    /// <summary>MFVideoFormat_NV12 (FCC 'NV12').</summary>
    /// <summary>MFVideoFormat_NV12（FCC 'NV12'）。</summary>
    public static readonly Guid VideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");

    /// <summary>MFVideoFormat_ARGB32 (D3DFMT_A8R8G8B8 = 0x15).</summary>
    /// <summary>MFVideoFormat_ARGB32（D3DFMT_A8R8G8B8 = 0x15）。</summary>
    public static readonly Guid VideoFormatArgb32 = new("00000015-0000-0010-8000-00AA00389B71");

    /// <summary>MF_MT_MAJOR_TYPE.</summary>
    /// <summary>MF_MT_MAJOR_TYPE。</summary>
    public static readonly Guid MtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");

    /// <summary>MF_MT_SUBTYPE.</summary>
    /// <summary>MF_MT_SUBTYPE。</summary>
    public static readonly Guid MtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");

    /// <summary>MF_MT_FRAME_SIZE.</summary>
    /// <summary>MF_MT_FRAME_SIZE。</summary>
    public static readonly Guid MtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");

    /// <summary>MF_MT_DEFAULT_STRIDE.</summary>
    /// <summary>MF_MT_DEFAULT_STRIDE。</summary>
    public static readonly Guid MtDefaultStride = new("644b4c48-b489-4089-8cec-9f9e3caa1ee7");

    /// <summary>MFMediaType_Audio ('auds').</summary>
    /// <summary>MFMediaType_Audio（'auds'）。</summary>
    public static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");

    /// <summary>MFAudioFormat_AAC (0x1610).</summary>
    /// <summary>MFAudioFormat_AAC（0x1610）。</summary>
    public static readonly Guid AudioFormatAac = new("00001610-0000-0010-8000-00AA00389B71");

    /// <summary>MFAudioFormat_PCM (0x0001).</summary>
    /// <summary>MFAudioFormat_PCM（0x0001）。</summary>
    public static readonly Guid AudioFormatPcm = new("00000001-0000-0010-8000-00AA00389B71");

    /// <summary>MF_MT_USER_DATA (codec private data, e.g. AAC AudioSpecificConfig).</summary>
    /// <summary>MF_MT_USER_DATA（编解码器私有数据，如 AAC AudioSpecificConfig）。</summary>
    public static readonly Guid MtUserData = new("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");

    /// <summary>MF_MT_AUDIO_NUM_CHANNELS.</summary>
    /// <summary>MF_MT_AUDIO_NUM_CHANNELS。</summary>
    public static readonly Guid MtAudioNumChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");

    /// <summary>MF_MT_AUDIO_SAMPLES_PER_SECOND.</summary>
    /// <summary>MF_MT_AUDIO_SAMPLES_PER_SECOND。</summary>
    public static readonly Guid MtAudioSamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");

    /// <summary>MF_MT_AUDIO_BITS_PER_SAMPLE.</summary>
    /// <summary>MF_MT_AUDIO_BITS_PER_SAMPLE。</summary>
    public static readonly Guid MtAudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");

    /// <summary>MFT_CATEGORY_AUDIO_DECODER.</summary>
    /// <summary>MFT_CATEGORY_AUDIO_DECODER。</summary>
    public static readonly Guid CategoryAudioDecoder = new("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7");

    /// <summary>MFT_CATEGORY_VIDEO_DECODER.</summary>
    /// <summary>MFT_CATEGORY_VIDEO_DECODER。</summary>
    public static readonly Guid CategoryVideoDecoder = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");

    /// <summary>CLSID_VideoProcessorMFT (msvproc.dll).</summary>
    /// <summary>CLSID_VideoProcessorMFT（msvproc.dll）。</summary>
    public static readonly Guid ClsidVideoProcessorMft = new("88753b26-5b24-49bd-b2e7-0c445c78c982");

    /// <summary>IID_IMFMediaEventGenerator.</summary>
    /// <summary>IID_IMFMediaEventGenerator。</summary>
    public static readonly Guid IidImfMediaEventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d");

    /// <summary>IID_IUnknown.</summary>
    /// <summary>IID_IUnknown。</summary>
    public static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");

    /// <summary>MF_TRANSFORM_ASYNC.</summary>
    /// <summary>MF_TRANSFORM_ASYNC。</summary>
    public static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");

    /// <summary>MF_TRANSFORM_ASYNC_UNLOCK.</summary>
    /// <summary>MF_TRANSFORM_ASYNC_UNLOCK。</summary>
    public static readonly Guid TransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");

    /// <summary>MF_SA_D3D11_AWARE.</summary>
    /// <summary>MF_SA_D3D11_AWARE。</summary>
    public static readonly Guid SaD3D11Aware = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");

    /// <summary>IID_IMFDXGIDeviceManager.</summary>
    /// <summary>IID_IMFDXGIDeviceManager。</summary>
    public static readonly Guid IidDxgiDeviceManager = new("eb533d5d-2db6-40f8-97a9-494692014f07");

    /// <summary>IID_IMFTransform.</summary>
    /// <summary>IID_IMFTransform。</summary>
    public static readonly Guid IidImfTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    /// <summary>MFT_ENUM_FLAG_SYNCMFT | LOCALMFT | SORTANDFILTER.</summary>
    /// <summary>MFT_ENUM_FLAG_SYNCMFT | LOCALMFT | SORTANDFILTER。</summary>
    public const uint MftEnumFlags = 0x01 | 0x10 | 0x40;

    /// <summary>MFT_ENUM_FLAG_ASYNCMFT | HARDWARE | SORTANDFILTER — async hardware decoders only.</summary>
    /// <summary>MFT_ENUM_FLAG_ASYNCMFT | HARDWARE | SORTANDFILTER —— 仅异步硬件解码器。</summary>
    public const uint MftEnumFlagsAsyncHardware = 0x02 | 0x04 | 0x40;

    /// <summary>MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.</summary>
    /// <summary>MFT_OUTPUT_STREAM_PROVIDES_SAMPLES。</summary>
    public const uint MftOutputStreamProvidesSamples = 0x100;

    /// <summary>MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE.</summary>
    /// <summary>MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE。</summary>
    public const uint MftOutputDataBufferFormatChange = 0x100;

    /// <summary>MFT_MESSAGE_COMMAND_FLUSH.</summary>
    /// <summary>MFT_MESSAGE_COMMAND_FLUSH。</summary>
    public const uint MftMessageFlush = 0;

    /// <summary>MFT_MESSAGE_NOTIFY_BEGIN_STREAMING.</summary>
    /// <summary>MFT_MESSAGE_NOTIFY_BEGIN_STREAMING。</summary>
    public const uint MftMessageBeginStreaming = 0x10000000;

    /// <summary>MFT_MESSAGE_NOTIFY_START_OF_STREAM.</summary>
    /// <summary>MFT_MESSAGE_NOTIFY_START_OF_STREAM。</summary>
    public const uint MftMessageStartOfStream = 0x10000003;

    /// <summary>MFT_MESSAGE_NOTIFY_END_OF_STREAM.</summary>
    /// <summary>MFT_MESSAGE_NOTIFY_END_OF_STREAM。</summary>
    public const uint MftMessageNotifyEndOfStream = 0x10000002;

    /// <summary>MFT_MESSAGE_COMMAND_DRAIN.</summary>
    /// <summary>MFT_MESSAGE_COMMAND_DRAIN。</summary>
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
    /// <summary>Reads one vtable slot; exposed for the few calls defined outside this class.</summary>
    /// <summary>读取一个 vtable 槽位；供少数在本类之外定义的调用使用。</summary>
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

    /// <summary>IMFAttributes.SetUnknown (slot 27).</summary>
    /// <summary>IMFAttributes.SetUnknown（槽位 27）。</summary>
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

    /// <summary>IMFMediaEventGenerator.GetEvent (slot 3).</summary>
    /// <summary>IMFMediaEventGenerator.GetEvent（槽位 3）。</summary>
    public static int GetEvent(nint eventGenerator, uint eventFlags, out nint mediaEvent)
    {
        fixed (nint* e = &mediaEvent)
            return ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(eventGenerator, 3))(eventGenerator, eventFlags, e);
    }

    /// <summary>IMFMediaEvent.GetType (slot 33; out-parameter form).</summary>
    /// <summary>IMFMediaEvent.GetType（槽位 33；out 参数形式）。</summary>
    public static int EventType(nint mediaEvent, out uint eventType)
    {
        fixed (uint* t = &eventType)
            return ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(mediaEvent, 33))(mediaEvent, t);
    }

    /// <summary>IMFMediaEvent.GetUINT32 (slot 7).</summary>
    /// <summary>IMFMediaEvent.GetUINT32（槽位 7）。</summary>
    public static int EventUInt32(nint mediaEvent, in Guid key, out uint value)
    {
        fixed (Guid* k = &key) fixed (uint* v = &value)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(mediaEvent, 7))(mediaEvent, k, v);
    }

    /// <summary>IMFSample.GetBufferByIndex (slot 40).</summary>
    /// <summary>IMFSample.GetBufferByIndex（槽位 40）。</summary>
    public static int GetBufferByIndex(nint sample, uint index, out nint buffer)
    {
        fixed (nint* b = &buffer)
            return ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(sample, 40))(sample, index, b);
    }

    /// <summary>IMFSample.GetBufferCount (slot 39; out-parameter form).</summary>
    /// <summary>IMFSample.GetBufferCount（槽位 39；out 参数形式）。</summary>
    public static int BufferCount(nint sample, out uint count)
    {
        fixed (uint* c = &count)
            return ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(sample, 39))(sample, c);
    }

    /// <summary>IMFAttributes.GetUnknown (slot 17).</summary>
    /// <summary>IMFAttributes.GetUnknown（槽位 17）。</summary>
    public static int GetUnknown(nint attributes, in Guid key, in Guid iid, out nint result)
    {
        fixed (Guid* k = &key, i = &iid) fixed (nint* r = &result)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, nint*, int>)Slot(attributes, 17))(attributes, k, i, r);
    }

    /// <summary>IMFDXGIBuffer.GetResource (slot 3).</summary>
    /// <summary>IMFDXGIBuffer.GetResource（槽位 3）。</summary>
    public static int DxgiGetResource(nint dxgiBuffer, in Guid iid, out nint resource)
    {
        fixed (Guid* i = &iid) fixed (nint* r = &resource)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(dxgiBuffer, 3))(dxgiBuffer, i, r);
    }

    /// <summary>IMFDXGIBuffer.GetSubresourceIndex (slot 4; out-parameter form).</summary>
    /// <summary>IMFDXGIBuffer.GetSubresourceIndex（槽位 4；out 参数形式）。</summary>
    public static int DxgiSubresourceIndex(nint dxgiBuffer, out uint subresource)
    {
        fixed (uint* s = &subresource)
            return ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(dxgiBuffer, 4))(dxgiBuffer, s);
    }

    /// <summary>IMFDXGIDeviceManager.ResetDevice (slot 7).</summary>
    /// <summary>IMFDXGIDeviceManager.ResetDevice（槽位 7）。</summary>
    public static int DxgiResetDevice(nint deviceManager, nint device, uint token) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Slot(deviceManager, 7))(deviceManager, device, token);

    /// <summary>ID3D11Texture2D.GetDesc (slot 10), including padded texture dimensions.</summary>
    public static D3D11Interop.Texture2DDesc D3DGetTexture2DDesc(nint texture)
    {
        D3D11Interop.Texture2DDesc desc = default;
        ((delegate* unmanaged[Stdcall]<nint, D3D11Interop.Texture2DDesc*, void>)Slot(texture, 10))(texture, &desc);
        return desc;
    }

    /// <summary>ID3D11Device.CreateTexture2D (slot 5).</summary>
    public static int D3DCreateTexture2D(nint device, in D3D11Interop.Texture2DDesc desc, nint initialData, out nint texture)
    {
        fixed (D3D11Interop.Texture2DDesc* d = &desc) fixed (nint* t = &texture)
            return ((delegate* unmanaged[Stdcall]<nint, D3D11Interop.Texture2DDesc*, nint, nint*, int>)Slot(device, 5))(device, d, initialData, t);
    }

    /// <summary>ID3D11Device.QueryInterface via MfCom.QueryInterface.</summary>
    /// <summary>ID3D11Device.QueryInterface，经 MfCom.QueryInterface。</summary>
    public static int D3DQueryInterface(nint value, in Guid iid, out nint result) => QueryInterface(value, iid, out result);

    /// <summary>ID3D11DeviceContext.Map (slot 14).</summary>
    /// <summary>ID3D11DeviceContext.Map（槽位 14）。</summary>
    public static int D3DMap(nint context, nint resource, uint subresource, uint mapType, uint mapFlags, out D3D11Interop.MappedSubresource mapped)
    {
        fixed (D3D11Interop.MappedSubresource* m = &mapped)
            return ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, D3D11Interop.MappedSubresource*, int>)Slot(context, 14))(context, resource, subresource, mapType, mapFlags, m);
    }

    /// <summary>ID3D11DeviceContext.Unmap (slot 15).</summary>
    /// <summary>ID3D11DeviceContext.Unmap（槽位 15）。</summary>
    public static void D3DUnmap(nint context, nint resource, uint subresource) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(context, 15))(context, resource, subresource);

    /// <summary>ID3D11DeviceContext.CopySubresourceRegion (slot 46).</summary>
    /// <summary>ID3D11DeviceContext.CopySubresourceRegion（槽位 46）。</summary>
    public static void D3DCopySubresourceRegion(nint context, nint destination, uint destinationSubresource, uint destinationX, uint destinationY, uint destinationZ, nint source, uint sourceSubresource, nint sourceBox) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, nint, void>)Slot(context, 46))(context, destination, destinationSubresource, destinationX, destinationY, destinationZ, source, sourceSubresource, sourceBox);

    /// <summary>ID3D11VideoDevice.CreateVideoProcessorEnumerator (slot 10).</summary>
    /// <summary>ID3D11VideoDevice.CreateVideoProcessorEnumerator（槽位 10）。</summary>
    public static int D3DCreateVideoProcessorEnumerator(nint videoDevice, in D3D11VideoProcessorContentDesc desc, out nint enumerator)
    {
        fixed (D3D11VideoProcessorContentDesc* d = &desc) fixed (nint* e = &enumerator)
            return ((delegate* unmanaged[Stdcall]<nint, D3D11VideoProcessorContentDesc*, nint*, int>)Slot(videoDevice, 10))(videoDevice, d, e);
    }

    /// <summary>ID3D11VideoDevice.CreateVideoProcessor (slot 4).</summary>
    /// <summary>ID3D11VideoDevice.CreateVideoProcessor（槽位 4）。</summary>
    public static int D3DCreateVideoProcessor(nint videoDevice, nint enumerator, uint rateConversionIndex, out nint processor)
    {
        fixed (nint* p = &processor)
            return ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint*, int>)Slot(videoDevice, 4))(videoDevice, enumerator, rateConversionIndex, p);
    }

    /// <summary>ID3D11VideoDevice.CreateVideoProcessorInputView (slot 8).</summary>
    /// <summary>ID3D11VideoDevice.CreateVideoProcessorInputView（槽位 8）。</summary>
    public static int D3DCreateVideoProcessorInputView(nint videoDevice, nint resource, nint enumerator, in D3D11Interop.VideoProcessorInputViewDesc desc, out nint view)
    {
        fixed (D3D11Interop.VideoProcessorInputViewDesc* d = &desc) fixed (nint* v = &view)
            return ((delegate* unmanaged[Stdcall]<nint, nint, nint, D3D11Interop.VideoProcessorInputViewDesc*, nint*, int>)Slot(videoDevice, 8))(videoDevice, resource, enumerator, d, v);
    }

    /// <summary>ID3D11VideoDevice.CreateVideoProcessorOutputView (slot 9).</summary>
    /// <summary>ID3D11VideoDevice.CreateVideoProcessorOutputView（槽位 9）。</summary>
    public static int D3DCreateVideoProcessorOutputView(nint videoDevice, nint resource, nint enumerator, in D3D11Interop.VideoProcessorOutputViewDesc desc, out nint view)
    {
        fixed (D3D11Interop.VideoProcessorOutputViewDesc* d = &desc) fixed (nint* v = &view)
            return ((delegate* unmanaged[Stdcall]<nint, nint, nint, D3D11Interop.VideoProcessorOutputViewDesc*, nint*, int>)Slot(videoDevice, 9))(videoDevice, resource, enumerator, d, v);
    }

    /// <summary>ID3D11VideoContext.VideoProcessorBlt (slot 53).</summary>
    /// <summary>ID3D11VideoContext.VideoProcessorBlt（槽位 53）。</summary>
    public static int D3DVideoProcessorBlt(nint videoContext, nint processor, nint outputView, uint outputFrame, uint streamCount, in D3D11Interop.VideoProcessorStream stream)
    {
        fixed (D3D11Interop.VideoProcessorStream* s = &stream)
            return ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, uint, D3D11Interop.VideoProcessorStream*, int>)Slot(videoContext, 53))(videoContext, processor, outputView, outputFrame, streamCount, s);
    }

    /// <summary>ID3D11VideoContext.VideoProcessorSetStreamFrameFormat (slot 27).</summary>
    /// <summary>ID3D11VideoContext.VideoProcessorSetStreamFrameFormat（槽位 27）。</summary>
    public static void D3DVideoProcessorSetStreamFrameFormat(nint videoContext, nint processor, uint streamIndex, uint frameFormat) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, void>)Slot(videoContext, 27))(videoContext, processor, streamIndex, frameFormat);

    /// <summary>ID3D11VideoContext.VideoProcessorSetStreamColorSpace (slot 28).</summary>
    /// <summary>ID3D11VideoContext.VideoProcessorSetStreamColorSpace（槽位 28）。</summary>
    public static void D3DVideoProcessorSetStreamColorSpace(nint videoContext, nint processor, uint streamIndex, in D3D11VideoProcessorColorSpace colorSpace)
    {
        fixed (D3D11VideoProcessorColorSpace* c = &colorSpace)
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, D3D11VideoProcessorColorSpace*, void>)Slot(videoContext, 28))(videoContext, processor, streamIndex, c);
    }

    /// <summary>ID3D11VideoContext.VideoProcessorSetOutputColorSpace (slot 15).</summary>
    /// <summary>ID3D11VideoContext.VideoProcessorSetOutputColorSpace（槽位 15）。</summary>
    public static void D3DVideoProcessorSetOutputColorSpace(nint videoContext, nint processor, in D3D11VideoProcessorColorSpace colorSpace)
    {
        fixed (D3D11VideoProcessorColorSpace* c = &colorSpace)
            ((delegate* unmanaged[Stdcall]<nint, nint, D3D11VideoProcessorColorSpace*, void>)Slot(videoContext, 15))(videoContext, processor, c);
    }

    /// <summary>ID3D11VideoContext.VideoProcessorSetStreamAutoProcessingMode (slot 37).</summary>
    /// <summary>ID3D11VideoContext.VideoProcessorSetStreamAutoProcessingMode（槽位 37）。</summary>
    public static void D3DVideoProcessorSetStreamAutoProcessingMode(nint videoContext, nint processor, uint streamIndex, int enable) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, int, void>)Slot(videoContext, 37))(videoContext, processor, streamIndex, enable);
}

/// <summary>D3D11_VIDEO_PROCESSOR_CONTENT_DESC.</summary>
/// <summary>D3D11_VIDEO_PROCESSOR_CONTENT_DESC。</summary>
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

/// <summary>D3D11_VIDEO_PROCESSOR_COLOR_SPACE (d3d11.h bitfields, 4-byte layout).</summary>
/// <summary>D3D11_VIDEO_PROCESSOR_COLOR_SPACE（d3d11.h 位域，4 字节布局）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11VideoProcessorColorSpace
{
    public uint Bits;
    /// <summary>Creates the native bitfield value defined by d3d11.h.</summary>
    public static D3D11VideoProcessorColorSpace FromBits(uint bits) => new() { Bits = bits };
}