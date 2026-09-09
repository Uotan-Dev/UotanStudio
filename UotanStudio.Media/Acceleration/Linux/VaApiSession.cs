using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UotanStudio.Media.Decoding;

namespace UotanStudio.Media.Acceleration.Linux;

/// <summary>A driver-advertised VA profile and entrypoint pair; not a decoded-stream guarantee.</summary>
/// <param name="Profile">VAProfile value from va.h.</param>
/// <param name="Entrypoint">VAEntrypoint value; 1 is VLD, 6 is EncSlice, 10 is VideoProc.</param>
public sealed record VaApiCapability(int Profile, int Entrypoint);

/// <summary>A serialized VA codec parameter, slice or processing buffer.</summary>
/// <param name="Type">VABufferType from the corresponding libva codec header.</param>
/// <param name="ElementSize">Native size of one structure or slice data element.</param>
/// <param name="Data">Whole elements in native ABI layout; copied by vaCreateBuffer.</param>
public sealed record VaApiBuffer(int Type, uint ElementSize, ReadOnlyMemory<byte> Data);

/// <summary>
/// Owns a DRM render node, VA display and an optional YUV420 context/surface pool.
/// Linux VA-API 提交与 DMA-BUF 导出接口；调用者负责码流解析及参考帧管理。
/// </summary>
/// <remarks>
/// Use on a single owning thread. These are acceleration primitives, not an Annex-B decoder.
/// Supply codec-specific parameters from va_dec_h264.h / va_dec_hevc.h, or a video processing pipeline.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class VaApiSession : IDisposable
{
    private int _fd = -1;
    private nint _display;
    private uint _config = uint.MaxValue, _context = uint.MaxValue;
    private uint[] _surfaces = [];
    private bool _disposed;
    private readonly int _thread = Environment.CurrentManagedThreadId;

    /// <summary>Opens an explicit DRM render node with close-on-exec and initializes the VA driver.</summary>
    public VaApiSession(string renderNode = "/dev/dri/renderD128")
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        ArgumentException.ThrowIfNullOrEmpty(renderNode);
        _fd = VaNative.open(renderNode, 2 | 0x80000); // O_RDWR | O_CLOEXEC
        if (_fd < 0) throw new IOException($"Cannot open {renderNode}: errno {Marshal.GetLastPInvokeError()}.");
        try
        {
            _display = VaNative.vaGetDisplayDRM(_fd);
            if (_display == 0) throw new NotSupportedException("VA DRM display is unavailable.");
            int result = VaNative.vaInitialize(_display, out int major, out int minor);
            Check(result, "initialize");
            Version = new Version(major, minor);
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Gets the negotiated libva API version.</summary>
    public Version Version { get; } = new(0, 0);

    /// <summary>Queries actual driver profile/entrypoint pairs for decoding, encoding and processing.</summary>
    public IReadOnlyList<VaApiCapability> QueryCapabilities()
    {
        CheckReady();
        int[] profiles = new int[BoundedCount(VaNative.vaMaxNumProfiles(_display))];
        int[] entries = new int[BoundedCount(VaNative.vaMaxNumEntrypoints(_display))];
        var result = new List<VaApiCapability>();
        fixed (int* p = profiles)
        {
            Check(VaNative.vaQueryConfigProfiles(_display, p, out int profileCount), "query profiles");
            if ((uint)profileCount > profiles.Length) throw new InvalidDataException("Invalid VA profile count.");
            for (int i = 0; i < profileCount; i++)
            {
                fixed (int* e = entries)
                {
                    Check(VaNative.vaQueryConfigEntrypoints(_display, p[i], e, out int entryCount), "query entrypoints");
                    if ((uint)entryCount > entries.Length) throw new InvalidDataException("Invalid VA entrypoint count.");
                    for (int j = 0; j < entryCount; j++) result.Add(new(p[i], e[j]));
                }
            }
        }
        return result;
    }

    /// <summary>Creates an 8-bit YUV420 context and reference surface pool, replacing any prior context.</summary>
    public void Configure(int profile, int entrypoint, int width, int height, int surfaceCount = 16)
    {
        CheckReady();
        VideoFrameLimits.ValidateDimensions(width, height);
        if (surfaceCount is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(surfaceCount));
        if (!QueryCapabilities().Contains(new VaApiCapability(profile, entrypoint)))
            throw new NotSupportedException("VA profile/entrypoint is unavailable on this render node.");
        ReleaseContext();
        try
        {
            Check(VaNative.vaCreateConfig(_display, profile, entrypoint, 0, 0, out _config), "create config");
            var surfaces = new uint[surfaceCount];
            fixed (uint* p = surfaces)
            {
                Check(VaNative.vaCreateSurfaces(_display, 1, (uint)width, (uint)height, p, (uint)surfaceCount, 0, 0), "create YUV420 surfaces");
                _surfaces = surfaces;
                Check(VaNative.vaCreateContext(_display, _config, width, height, 1, p, surfaceCount, out _context), "create context");
            }
        }
        catch { ReleaseContext(); throw; }
    }

    /// <summary>Gets a native surface ID for use in serialized reference-picture parameters.</summary>
    public uint GetSurfaceId(int index) { CheckReady(); return _surfaces[index]; }

    /// <summary>Copies and submits a complete picture's native parameter buffers to a configured surface.</summary>
    public void Submit(int surfaceIndex, IReadOnlyList<VaApiBuffer> buffers)
    {
        CheckReady();
        ArgumentNullException.ThrowIfNull(buffers);
        uint target = _surfaces[surfaceIndex];
        if (buffers.Count is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(buffers));
        var ids = new List<uint>(buffers.Count);
        long total = 0;
        // Validate the entire submission before creating native objects.
        foreach (VaApiBuffer buffer in buffers)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            total += buffer.Data.Length;
            if (buffer.ElementSize == 0 || buffer.Data.IsEmpty || buffer.Data.Length % buffer.ElementSize != 0
                || total > VideoFrameLimits.MaximumAccessUnitBytes) throw new ArgumentException("Invalid or excessive VA buffer layout.", nameof(buffers));
        }
        try
        {
            foreach (VaApiBuffer buffer in buffers)
            {
                fixed (byte* data = buffer.Data.Span)
                {
                    Check(VaNative.vaCreateBuffer(_display, _context, buffer.Type, buffer.ElementSize,
                        (uint)buffer.Data.Length / buffer.ElementSize, data, out uint id), "create buffer");
                    ids.Add(id);
                }
            }
            Check(VaNative.vaBeginPicture(_display, _context, target), "begin picture");
            try
            {
                uint[] array = ids.ToArray();
                fixed (uint* p = array) Check(VaNative.vaRenderPicture(_display, _context, p, array.Length), "render picture");
            }
            finally { Check(VaNative.vaEndPicture(_display, _context), "end picture"); }
        }
        finally { foreach (uint id in ids) _ = VaNative.vaDestroyBuffer(_display, id); }
    }

    /// <summary>
    /// Waits for completion and exports read-only composed DRM PRIME layers.
    /// The caller must dispose the returned descriptor to close all exported file descriptors.
    /// </summary>
    public DmaBufSurface ExportSurface(int index)
    {
        CheckReady();
        uint id = _surfaces[index];
        Check(VaNative.vaSyncSurface(_display, id), "sync surface");
        var descriptor = new DmaBufDescriptor();
        Check(VaNative.vaExportSurfaceHandle(_display, id, 0x40000000, 1 | 8, &descriptor), "export DRM PRIME 2");
        return new DmaBufSurface(descriptor);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        CheckThread();
        _disposed = true;
        ReleaseContext();
        if (_display != 0) { _ = VaNative.vaTerminate(_display); _display = 0; }
        if (_fd >= 0) { _ = VaNative.close(_fd); _fd = -1; }
    }
    private void ReleaseContext()
    {
        if (_context != uint.MaxValue) { _ = VaNative.vaDestroyContext(_display, _context); _context = uint.MaxValue; }
        if (_surfaces.Length != 0)
        {
            fixed (uint* p = _surfaces) _ = VaNative.vaDestroySurfaces(_display, p, _surfaces.Length);
            _surfaces = [];
        }
        if (_config != uint.MaxValue) { _ = VaNative.vaDestroyConfig(_display, _config); _config = uint.MaxValue; }
    }
    private static int BoundedCount(int count) => count is >= 0 and <= 4096 ? count : throw new InvalidDataException("Invalid VA capability capacity.");
    private void CheckReady() { ObjectDisposedException.ThrowIf(_disposed, this); CheckThread(); }
    private void CheckThread()
    {
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("VA session belongs to another thread.");
    }
    private static void Check(int status, string operation)
    {
        if (status != 0) throw new InvalidOperationException($"VA-API {operation} failed: {status}.");
    }
}
