using System.Runtime.InteropServices;

namespace UotanStudio.Media.Decoding.VideoToolbox;

[StructLayout(LayoutKind.Sequential)]
internal struct CmTime
{
    public long Value;
    public int Scale;
    public uint Flags;
    public long Epoch;
    internal static CmTime FromTicks(long ticks) => new() { Value = ticks, Scale = 10000000, Flags = 1 };
    internal readonly long Ticks => Scale > 0 && (Flags & 1) != 0 ? checked((long)((decimal)Value * 10000000 / Scale)) : 0;
}
[StructLayout(LayoutKind.Sequential)]
internal struct CmTiming { public CmTime Duration, Presentation, Decode; }
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VtCallback
{
    public delegate* unmanaged<nint, nint, int, uint, nint, CmTime, CmTime, void> Function;
    public nint RefCon;
}

internal static unsafe partial class VtNative
{
    internal const string Vt = "/System/Library/Frameworks/VideoToolbox.framework/VideoToolbox";
    internal const string Cm = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    internal const string Cv = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    internal const string Cf = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    internal const uint Nv12 = 0x34323076;
    internal const uint Bgra = 0x42475224;
    internal static uint Codec(VideoCodecId codec) => codec switch
    {
        VideoCodecId.H264 => 0x61766331,
        VideoCodecId.H265 => 0x68766331,
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };
    internal static void Check(int status, string operation)
    {
        if (status != 0) throw new InvalidOperationException($"VideoToolbox {operation} failed: {status}.");
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, nint> Libraries = new();
    private static nint Export(string library, string name) =>
        NativeLibrary.GetExport(Libraries.GetOrAdd(library, static path => NativeLibrary.Load(path)), name);
    internal static nint Symbol(string library, string name) => Marshal.ReadIntPtr(Export(library, name));
    internal static nint Dictionary() => CFDictionaryCreateMutable(0, 0,
        Export(Cf, "kCFTypeDictionaryKeyCallBacks"), Export(Cf, "kCFTypeDictionaryValueCallBacks"));
    internal static void SetBoolean(nint dictionary, string library, string key, bool value) =>
        CFDictionarySetValue(dictionary, Symbol(library, key), Symbol(Cf, value ? "kCFBooleanTrue" : "kCFBooleanFalse"));
    internal static nint PixelAttributes() => PixelAttributes(Nv12, gpuCompatible: false);
    internal static nint PixelAttributes(uint pixelFormat, bool gpuCompatible)
    {
        nint dictionary = Dictionary();
        int format = unchecked((int)pixelFormat);
        nint number = CFNumberCreate(0, 3, &format);
        try
        {
            CFDictionarySetValue(dictionary, Symbol(Cv, "kCVPixelBufferPixelFormatTypeKey"), number);
            if (gpuCompatible)
            {
                SetBoolean(dictionary, Cv, "kCVPixelBufferOpenGLCompatibilityKey", true);
                SetBoolean(dictionary, Cv, "kCVPixelBufferMetalCompatibilityKey", true);
            }
        }
        catch { CFRelease(dictionary); throw; }
        finally { CFRelease(number); }
        return dictionary;
    }
    [LibraryImport(Cv)] internal static partial nint CVPixelBufferGetIOSurface(nint pixelBuffer);
    [LibraryImport(Cf)] internal static partial void CFRetain(nint value);
    [LibraryImport(Cf)] internal static partial byte CFEqual(nint left, nint right);
    [LibraryImport(Cv)] internal static partial nint CVBufferCopyAttachment(nint buffer, nint key, nint mode);
    [LibraryImport(Cf)] internal static partial void CFRelease(nint value);
    [LibraryImport(Cf)] internal static partial nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keys, nint values);
    [LibraryImport(Cf)] internal static partial void CFDictionarySetValue(nint dictionary, nint key, nint value);
    [LibraryImport(Cf)] internal static partial nint CFNumberCreate(nint allocator, int type, void* value);
    [LibraryImport(Vt)] internal static partial byte VTIsHardwareDecodeSupported(uint codec);
    [LibraryImport(Vt)] internal static partial int VTDecompressionSessionCreate(nint allocator, nint format, nint specification, nint attributes, VtCallback* callback, out nint session);
    [LibraryImport(Vt)] internal static partial int VTDecompressionSessionDecodeFrame(nint session, nint sample, uint flags, nint source, out uint info);
    [LibraryImport(Vt)] internal static partial int VTDecompressionSessionWaitForAsynchronousFrames(nint session);
    [LibraryImport(Vt)] internal static partial int VTDecompressionSessionFinishDelayedFrames(nint session);
    [LibraryImport(Vt)] internal static partial void VTDecompressionSessionInvalidate(nint session);
    [LibraryImport(Vt)] internal static partial int VTSessionSetProperty(nint session, nint key, nint value);
    [LibraryImport(Vt)] internal static partial int VTCompressionSessionCreate(nint allocator, int width, int height, uint codec, nint specification, nint attributes, nint compressedAllocator, delegate* unmanaged<nint, nint, int, uint, nint, void> callback, nint refCon, out nint session);
    [LibraryImport(Vt)] internal static partial int VTCompressionSessionPrepareToEncodeFrames(nint session);
    [LibraryImport(Vt)] internal static partial int VTCompressionSessionEncodeFrame(nint session, nint image, CmTime presentation, CmTime duration, nint properties, nint source, out uint flags);
    [LibraryImport(Vt)] internal static partial int VTCompressionSessionCompleteFrames(nint session, CmTime until);
    [LibraryImport(Vt)] internal static partial void VTCompressionSessionInvalidate(nint session);
    [LibraryImport(Cm)] internal static partial int CMVideoFormatDescriptionCreateFromH264ParameterSets(nint allocator, nuint count, byte** sets, nuint* sizes, int lengthBytes, out nint format);
    [LibraryImport(Cm)] internal static partial int CMVideoFormatDescriptionCreateFromHEVCParameterSets(nint allocator, nuint count, byte** sets, nuint* sizes, int lengthBytes, nint extensions, out nint format);
    [LibraryImport(Cm)] internal static partial int CMVideoFormatDescriptionGetH264ParameterSetAtIndex(nint format, nuint index, out nint parameter, out nuint size, out nuint count, out int lengthBytes);
    [LibraryImport(Cm)] internal static partial int CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(nint format, nuint index, out nint parameter, out nuint size, out nuint count, out int lengthBytes);
    [LibraryImport(Cm)] internal static partial int CMBlockBufferCreateWithMemoryBlock(nint allocator, nint memory, nuint length, nint blockAllocator, nint customSource, nuint offset, nuint dataLength, uint flags, out nint block);
    [LibraryImport(Cm)] internal static partial int CMBlockBufferReplaceDataBytes(void* source, nint block, nuint offset, nuint length);
    [LibraryImport(Cm)] internal static partial int CMBlockBufferCopyDataBytes(nint block, nuint offset, nuint length, void* destination);
    [LibraryImport(Cm)] internal static partial nuint CMBlockBufferGetDataLength(nint block);
    [LibraryImport(Cm)] internal static partial int CMSampleBufferCreateReady(nint allocator, nint data, nint format, nint samples, nint timingCount, CmTiming* timing, nint sizeCount, nuint* sizes, out nint sample);
    [LibraryImport(Cm)] internal static partial nint CMSampleBufferGetDataBuffer(nint sample);
    [LibraryImport(Cm)] internal static partial nint CMSampleBufferGetFormatDescription(nint sample);
    [LibraryImport(Cm)] internal static partial CmTime CMSampleBufferGetPresentationTimeStamp(nint sample);
    [LibraryImport(Cv)] internal static partial int CVPixelBufferCreate(nint allocator, nuint width, nuint height, uint format, nint attributes, out nint buffer);
    [LibraryImport(Cv)] internal static partial int CVPixelBufferLockBaseAddress(nint buffer, ulong flags);
    [LibraryImport(Cv)] internal static partial int CVPixelBufferUnlockBaseAddress(nint buffer, ulong flags);
    [LibraryImport(Cv)] internal static partial uint CVPixelBufferGetPixelFormatType(nint buffer);
    [LibraryImport(Cv)] internal static partial nuint CVPixelBufferGetWidth(nint buffer);
    [LibraryImport(Cv)] internal static partial nuint CVPixelBufferGetHeight(nint buffer);
    [LibraryImport(Cv)] internal static partial nuint CVPixelBufferGetPlaneCount(nint buffer);
    [LibraryImport(Cv)] internal static partial nint CVPixelBufferGetBaseAddressOfPlane(nint buffer, nuint plane);
    [LibraryImport(Cv)] internal static partial nuint CVPixelBufferGetBytesPerRowOfPlane(nint buffer, nuint plane);
    [LibraryImport(Cv)] internal static partial nuint CVPixelBufferGetHeightOfPlane(nint buffer, nuint plane);
}