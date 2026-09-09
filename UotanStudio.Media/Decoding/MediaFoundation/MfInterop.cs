using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.MediaFoundation;

// Source-faithful unmanaged calls replace runtime COM marshalling.
// Slots below follow the Windows SDK mfobjects.h / mftransform.h ABI.
// No runtime COM wrappers; NativeAOT uses the same unmanaged ABI.

/// <summary>MediaFoundation interop helpers: functions, GUIDs and error codes.</summary>
/// <summary>MediaFoundation 互操作辅助：函数、GUID 与错误码。</summary>
[SupportedOSPlatform("windows")]
internal static partial class MfInterop
{
    private const string MfPlat = "mfplat.dll";

    // --- MF version / startup -------------------------------------------------

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

    // --- Well-known GUIDs -----------------------------------------------------

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

    /// <summary>MFT_CATEGORY_VIDEO_DECODER.</summary>
    /// <summary>MFT_CATEGORY_VIDEO_DECODER。</summary>
    public static readonly Guid CategoryVideoDecoder = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");

    /// <summary>IID_IMFTransform.</summary>
    /// <summary>IID_IMFTransform。</summary>
    public static readonly Guid IidImfTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    // --- Enum flags -----------------------------------------------------------

    /// <summary>MFT_ENUM_FLAG_SYNCMFT | LOCALMFT | SORTANDFILTER.</summary>
    /// <summary>MFT_ENUM_FLAG_SYNCMFT | LOCALMFT | SORTANDFILTER。</summary>
    public const uint MftEnumFlags = 0x01 | 0x10 | 0x40;

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

    // --- Error codes ----------------------------------------------------------

    public const int SOk = 0;
    public const int SFalse = 1;
    public const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    public const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    public const int MfENotAccepting = unchecked((int)0xC00D36B5);
    public const int MfEAttributeNotFound = unchecked((int)0xC00D36E6);
    public const int MfENoMoreTypes = unchecked((int)0xC00D36B9);
}

// Unmanaged layouts and vtable calls follow mfobjects.h / mftransform.h.
// No runtime COM marshalling: these calls also work in NativeAOT.
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
    public static int QueryInterface(nint value, in Guid iid, out nint result)
    {
        fixed (Guid* key = &iid) fixed (nint* p = &result)
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(value, 0))(value, key, p);
    }
    public static int SetCodecLowLatency(nint value, in Guid key, bool useUInt32)
    {
        // Microsoft's H.264 decoder uniquely requires VT_UI4 for AVLowLatencyMode.
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
    public static int Message(nint value, uint message) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Slot(value, 23))(value, message, 0);
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
}
