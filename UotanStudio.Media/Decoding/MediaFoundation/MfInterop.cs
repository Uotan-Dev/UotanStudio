using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.MediaFoundation;

// ---------------------------------------------------------------------------
// Minimal MediaFoundation COM interop for push-style H.264/H.265 decoding.
// Vtable order matters: IMFMediaType / IMFSample / IMFActivate all inherit
// IMFAttributes, so they must be declared as derived interfaces; unused
// methods are still declared (with correct signatures) to keep slot alignment.
// 最小化的 MediaFoundation COM 互操作，用于推送式 H.264/H.265 解码。vtable 顺序
// 至关重要：IMFMediaType / IMFSample / IMFActivate 均继承 IMFAttributes，必须以
// 接口继承方式声明；未使用的方法仍按正确签名声明，以保持槽位对齐。
// ---------------------------------------------------------------------------

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
    public static readonly Guid IidImfTransform = new("bf94c121-36b4-4554-9a0b-91d73cbcd82c");

    // --- Enum flags -----------------------------------------------------------

    /// <summary>MFT_ENUM_FLAG_SYNCMFT | LOCALMFT | SORTANDFILTER.</summary>
    /// <summary>MFT_ENUM_FLAG_SYNCMFT | LOCALMFT | SORTANDFILTER。</summary>
    public const uint MftEnumFlags = 0x01 | 0x10 | 0x40;

    /// <summary>MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.</summary>
    /// <summary>MFT_OUTPUT_STREAM_PROVIDES_SAMPLES。</summary>
    public const uint MftOutputStreamProvidesSamples = 0x2;

    /// <summary>MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE.</summary>
    /// <summary>MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE。</summary>
    public const uint MftOutputDataBufferFormatChange = 0x100;

    /// <summary>MFT_MESSAGE_COMMAND_FLUSH.</summary>
    /// <summary>MFT_MESSAGE_COMMAND_FLUSH。</summary>
    public const uint MftMessageFlush = 0;

    /// <summary>MFT_MESSAGE_NOTIFY_BEGIN_STREAMING.</summary>
    /// <summary>MFT_MESSAGE_NOTIFY_BEGIN_STREAMING。</summary>
    public const uint MftMessageBeginStreaming = 1;

    /// <summary>MFT_MESSAGE_NOTIFY_START_OF_STREAM.</summary>
    /// <summary>MFT_MESSAGE_NOTIFY_START_OF_STREAM。</summary>
    public const uint MftMessageStartOfStream = 4;

    // --- Error codes ----------------------------------------------------------

    public const int SOk = 0;
    public const int SFalse = 1;
    public const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    public const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    public const int MfENotAccepting = unchecked((int)0xC00D6D78);
    public const int MfEAttributeNotFound = unchecked((int)0xC00D36E6);
    public const int MfENoMoreTypes = unchecked((int)0xC00D36B9);
}

/// <summary>MFT_REGISTER_TYPE_INFO structure describing a registered MFT's stream type.</summary>
/// <summary>MFT_REGISTER_TYPE_INFO 结构，描述已注册 MFT 的流类型。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftRegisterTypeInfo
{
    public Guid MajorType;

    public Guid Subtype;

    public MftRegisterTypeInfo(Guid majorType, Guid subtype)
    {
        MajorType = majorType;
        Subtype = subtype;
    }
}

/// <summary>MFT_OUTPUT_DATA_BUFFER structure (passed by reference to ProcessOutput).</summary>
/// <summary>MFT_OUTPUT_DATA_BUFFER 结构（以引用方式传给 ProcessOutput）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputDataBuffer
{
    public uint StreamId;
    public IMFSample? Sample;
    public uint Status;
    public IMFSample? Events;
}

/// <summary>MFT_OUTPUT_STREAM_INFO structure.</summary>
/// <summary>MFT_OUTPUT_STREAM_INFO 结构。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputStreamInfo
{
    public uint Flags;
    public uint Size;
    public uint Alignment;
}

/// <summary>MFT_INPUT_STREAM_INFO structure.</summary>
/// <summary>MFT_INPUT_STREAM_INFO 结构。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftInputStreamInfo
{
    public long MaxLatency;
    public uint Flags;
    public uint Size;
    public uint MaxLookahead;
    public uint Alignment;
}

/// <summary>
/// IMFAttributes — all 32 vtable slots declared in mfobjects.h order.
/// </summary>
/// <summary>
/// IMFAttributes — 按 mfobjects.h 顺序声明全部 32 个 vtable 槽位。
/// </summary>
[ComImport]
[Guid("2cd2d921-c447-449c-a34e-c21d2febf4bd")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(in Guid key, IntPtr pValue);
    [PreserveSig] int GetItemType(in Guid key, out int type);
    [PreserveSig] int CompareItem(in Guid key, IntPtr value, IntPtr pResult);
    [PreserveSig] int Compare(IMFAttributes theOther, int matchType, IntPtr pResult);
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    [PreserveSig] int GetDouble(in Guid key, out double value);
    [PreserveSig] int GetGUID(in Guid key, out Guid value);
    [PreserveSig] int GetStringLength(in Guid key, out uint length);
    [PreserveSig] int GetString(in Guid key, IntPtr buffer, uint bufferSize, out uint length);
    [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr buffer, out uint length);
    [PreserveSig] int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] int GetBlob(in Guid key, IntPtr buffer, uint bufferSize, out uint size);
    [PreserveSig] int GetAllocatedBlob(in Guid key, out IntPtr buffer, out uint size);
    [PreserveSig] int SetUnknown(in Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
    [PreserveSig] int GetUnknown(in Guid key, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object unknown);
    [PreserveSig] int SetItem(in Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid key, uint value);
    [PreserveSig] int SetUINT64(in Guid key, ulong value);
    [PreserveSig] int SetDouble(in Guid key, double value);
    [PreserveSig] int SetGUID(in Guid key, in Guid value);
    [PreserveSig] int SetStringLength(in Guid key, uint value);
    [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(in Guid key, IntPtr buffer, uint size);
    [PreserveSig] int SetBlobSize(in Guid key, uint size);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
}

/// <summary>IMFMediaType — inherits the 32 IMFAttributes slots, then 4 of its own.</summary>
/// <summary>IMFMediaType — 继承 IMFAttributes 的 32 个槽位，再追加自身 4 个。</summary>
[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c858")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    [PreserveSig] int GetMajorType(out Guid majorType);
    [PreserveSig] int IsCompressedFormat(out int compressed);
    [PreserveSig] int GetRepresentation(in Guid representation, out IntPtr representationData);
    [PreserveSig] int FreeRepresentation(in Guid representation, IntPtr representationData);
}

/// <summary>IMFMediaBuffer.</summary>
/// <summary>IMFMediaBuffer。</summary>
[ComImport]
[Guid("045fa593-8b98-4d9d-a27c-cea88a95a6fe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint currentLength);
    [PreserveSig] int SetCurrentLength(uint currentLength);
    [PreserveSig] int GetMaxLength(out uint maxLength);
}

/// <summary>IMFSample — inherits the 32 IMFAttributes slots, then 12 of its own.</summary>
/// <summary>IMFSample — 继承 IMFAttributes 的 32 个槽位，再追加自身 12 个。</summary>
[ComImport]
[Guid("b7c2cbf4-3c5f-4b8f-8c14-1e2f4c5d6a7b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample : IMFAttributes
{
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out uint count);
    [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    [PreserveSig] int RemoveBufferByIndex(uint index);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out uint length);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

/// <summary>IMFActivate — inherits the 32 IMFAttributes slots, then 3 of its own.</summary>
/// <summary>IMFActivate — 继承 IMFAttributes 的 32 个槽位，再追加自身 3 个。</summary>
[ComImport]
[Guid("1140318e-0f48-45a5-a285-91e1b0c1e14a")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFActivate : IMFAttributes
{
    [PreserveSig] int ActivateObject(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    [PreserveSig] int ShutdownObject();
    [PreserveSig] int DetachObject();
}

/// <summary>IMFTransform (vtable order per mftransform.h; standalone IUnknown).</summary>
/// <summary>IMFTransform（按 mftransform.h 的 vtable 顺序；独立 IUnknown 接口）。</summary>
[ComImport]
[Guid("bf94c121-36b4-4554-9a0b-91d73cbcd82c")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFTransform
{
    [PreserveSig] int GetStreamLimits(
        out uint inputMinimum, out uint inputMaximum, out uint outputMinimum, out uint outputMaximum);
    [PreserveSig] int GetStreamCount(out uint inputCount, out uint outputCount);
    [PreserveSig] int GetStreamIDs(uint inputIdArraySize, IntPtr inputIds, uint outputIdArraySize, IntPtr outputIds);
    [PreserveSig] int GetInputStreamInfo(uint streamId, out MftInputStreamInfo info);
    [PreserveSig] int GetOutputStreamInfo(uint streamId, out MftOutputStreamInfo info);
    [PreserveSig] int GetAttributes(out IMFAttributes attributes);
    [PreserveSig] int GetInputAvailableType(uint streamId, uint typeIndex, out IMFMediaType type);
    [PreserveSig] int GetOutputAvailableType(uint streamId, uint typeIndex, out IMFMediaType type);
    [PreserveSig] int SetInputType(uint streamId, IMFMediaType? type, uint flags);
    [PreserveSig] int SetOutputType(uint streamId, IMFMediaType? type, uint flags);
    [PreserveSig] int GetInputCurrentType(uint streamId, out IMFMediaType type);
    [PreserveSig] int GetOutputCurrentType(uint streamId, out IMFMediaType type);
    [PreserveSig] int GetInputStatus(uint streamId, out uint status);
    [PreserveSig] int GetOutputStatus(out uint status);
    [PreserveSig] int SetOutputBounds(long lower, long upper);
    [PreserveSig] int ProcessEvent(uint streamId, IntPtr events);
    [PreserveSig] int ProcessMessage(uint message, IntPtr param);
    [PreserveSig] int ProcessInput(uint streamId, IMFSample sample, uint flags);
    [PreserveSig] int ProcessOutput(uint flags, uint outputBufferCount, ref MftOutputDataBuffer outputSamples, out uint status);
}
