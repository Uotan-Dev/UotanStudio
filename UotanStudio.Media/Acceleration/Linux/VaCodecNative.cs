using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UotanStudio.Media.Decoding;

namespace UotanStudio.Media.Acceleration.Linux;

// Fixed bridge ABI v1, independent of FFmpeg's versioned structure layouts.
[StructLayout(LayoutKind.Sequential)]
internal struct VaFrameInfo
{
    internal int Width, Height, Stride, Length, Matrix, Range;
    internal long Timestamp;
}

internal interface IVaCodecSession : IDisposable
{
    int Send(ReadOnlySpan<byte> packet, long timestamp);
    int Receive(out VaFrameInfo info);
    void Copy(Span<byte> destination);
    void Reset();
}

internal sealed class VaCodecHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    internal VaCodecHandle(nint value) : this() => SetHandle(value);
    protected override bool ReleaseHandle() { VaCodecNative.Close(handle); return true; }
}

internal sealed unsafe partial class VaCodecNative(VaCodecHandle handle) : IVaCodecSession
{
    private const string Library = "uotan_media_va";
    private const int ErrorBytes = 1024;

    internal static IVaCodecSession Open(VideoCodecId codec, string? renderNode)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("VA-API requires Linux.");
        if (AbiVersion() != 1) throw new NotSupportedException("Incompatible VA-API bridge ABI (expected 1).");
        string[] nodes = renderNode is not null ? [renderNode] : VaApiSession.RenderNodes();
        var errors = new List<string>();
        byte* errorBuffer = stackalloc byte[ErrorBytes];
        foreach (string node in nodes)
        {
            try
            {
                using var capabilities = new VaApiSession(node);
                bool supported = false;
                foreach (VaApiCapability capability in capabilities.QueryCapabilities())
                    supported |= VaApiSession.SupportsCodec(capability, codec);
                if (!supported)
                {
                    errors.Add($"{node}: no 8-bit VLD profile for {codec}");
                    continue;
                }
                int status = OpenNative((int)codec, node, out nint pointer, errorBuffer);
                var owner = new VaCodecHandle(pointer);
                if (status < 0 || owner.IsInvalid)
                {
                    owner.Dispose();
                    errors.Add($"{node}: {ReadError(errorBuffer)}");
                    continue;
                }
                return new VaCodecNative(owner);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException)
            {
                errors.Add($"{node}: {error.Message}");
            }
        }
        throw new NotSupportedException($"No VA-API device accepts {codec}. {string.Join("; ", errors)}");
    }

    public int Send(ReadOnlySpan<byte> packet, long timestamp)
    {
        byte* error = stackalloc byte[ErrorBytes];
        fixed (byte* data = packet)
            return Check(SendNative(handle, data, packet.Length, timestamp, error), error);
    }

    public int Receive(out VaFrameInfo info)
    {
        byte* error = stackalloc byte[ErrorBytes];
        return Check(ReceiveNative(handle, out info, error), error);
    }

    public void Copy(Span<byte> destination)
    {
        byte* error = stackalloc byte[ErrorBytes];
        fixed (byte* data = destination)
            Check(CopyNative(handle, data, destination.Length, error), error);
    }

    public void Reset() => ResetNative(handle);
    public void Dispose() => handle.Dispose();
    private static int Check(int status, byte* error) => status >= 0 ? status : throw new InvalidDataException(ReadError(error));
    private static string ReadError(byte* error)
    {
        ReadOnlySpan<byte> bytes = new(error, ErrorBytes);
        int end = bytes.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }

    [LibraryImport(Library, EntryPoint = "uv_abi_version")] private static partial int AbiVersion();
    [LibraryImport(Library, EntryPoint = "uv_open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenNative(int codec, string node, out nint handle, byte* error);
    [LibraryImport(Library, EntryPoint = "uv_close")] internal static partial void Close(nint handle);
    [LibraryImport(Library, EntryPoint = "uv_send")] private static partial int SendNative(VaCodecHandle handle, byte* data, int length, long pts, byte* error);
    [LibraryImport(Library, EntryPoint = "uv_receive")] private static partial int ReceiveNative(VaCodecHandle handle, out VaFrameInfo info, byte* error);
    [LibraryImport(Library, EntryPoint = "uv_copy")] private static partial int CopyNative(VaCodecHandle handle, byte* data, int length, byte* error);
    [LibraryImport(Library, EntryPoint = "uv_reset")] private static partial void ResetNative(VaCodecHandle handle);
}