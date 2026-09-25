using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UotanStudio.Media.Decoding;

namespace UotanStudio.Media.Acceleration.Linux;

/// <summary>
/// A driver-advertised VA profile and entrypoint pair; not a decoded-stream guarantee.
/// <para>驱动报告的 VA profile 与 entrypoint 组合；不保证具体码流可解码。</para>
/// </summary>
/// <param name="Profile">VAProfile value from va.h. <para>来自 va.h 的 VAProfile 值。</para></param>
/// <param name="Entrypoint">VAEntrypoint value; 1 is VLD, 6 is EncSlice, 10 is VideoProc.
/// <para>VAEntrypoint 值；1 为 VLD，6 为 EncSlice，10 为 VideoProc。</para></param>
public sealed record VaApiCapability(int Profile, int Entrypoint);

/// <summary>
/// A serialized VA codec parameter, slice or processing buffer.
/// <para>序列化的 VA 编解码器参数、切片或处理缓冲区。</para>
/// </summary>
/// <param name="Type">VABufferType from the corresponding libva codec header.
/// <para>来自对应 libva 编解码器头文件的 VABufferType。</para></param>
/// <param name="ElementSize">Native size of one structure or slice data element.
/// <para>单个结构体或切片数据元素的原生大小。</para></param>
/// <param name="Data">
/// Whole elements in native ABI layout; copied by vaCreateBuffer.
/// <para>按原生 ABI 布局排列的完整元素；由 vaCreateBuffer 复制。</para>
/// </param>
public sealed record VaApiBuffer(int Type, uint ElementSize, ReadOnlyMemory<byte> Data);

/// <summary>
/// Owns a DRM render node, VA display and an optional YUV420 context/surface pool.
/// <para>Linux VA-API 提交与 DMA-BUF 导出接口；调用者负责码流解析及参考帧管理。</para>
/// </summary>
/// <remarks>
/// Use on a single owning thread. These are acceleration primitives, not an Annex-B decoder.
/// Supply codec-specific parameters from va_dec_h264.h / va_dec_hevc.h, or a video processing pipeline.
/// <para>
/// 必须在单一所属线程上使用。这些是加速原语，不是 Annex-B 解码器。
/// 需按 va_dec_h264.h / va_dec_hevc.h 提供编解码器专用参数，或用于视频处理管线。
/// </para>
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

    /// <summary>
    /// Opens an explicit DRM render node with close-on-exec and initializes the VA driver.
    /// <para>以 close-on-exec 方式打开指定的 DRM 渲染节点，并初始化 VA 驱动。</para>
    /// </summary>
    /// <param name="renderNode">
    /// Absolute path of the DRM render node to open, such as <c>/dev/dri/renderD128</c>.
    /// <para>要打开的 DRM 渲染节点绝对路径，例如 <c>/dev/dri/renderD128</c>。</para>
    /// </param>
    /// <exception cref="PlatformNotSupportedException">The current OS is not Linux.
    /// <para>当前操作系统不是 Linux。</para></exception>
    /// <exception cref="ArgumentException"><c>renderNode</c> is null or empty.
    /// <para><c>renderNode</c> 为 null 或空。</para></exception>
    /// <exception cref="IOException">The render node cannot be opened. <para>无法打开该渲染节点。</para></exception>
    /// <exception cref="NotSupportedException">The driver exposes no VA DRM display for the node.
    /// <para>驱动未为该节点提供 VA DRM display。</para></exception>
    /// <exception cref="InvalidOperationException">
    /// <c>vaInitialize</c> returned a failure status.
    /// <para><c>vaInitialize</c> 返回失败状态。</para>
    /// </exception>
    /// <example>
    /// <code>
    /// using var session = new VaApiSession("/dev/dri/renderD128");
    /// Version negotiated = session.Version;
    /// var capabilities = session.QueryCapabilities();
    /// </code>
    /// </example>
    public VaApiSession(string renderNode = "/dev/dri/renderD128")
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        ArgumentException.ThrowIfNullOrEmpty(renderNode);
        _fd = VaNative.open(renderNode, 2 | 0x80000);
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

    /// <summary>
    /// Gets the negotiated libva API version.
    /// <para>获取协商得到的 libva API 版本。</para>
    /// </summary>
    /// <value>
    /// The version reported by <c>vaInitialize</c>, or 0.0 while initialization has not completed.
    /// <para>由 <c>vaInitialize</c> 报告的版本；初始化尚未完成时为 0.0。</para>
    /// </value>
    public Version Version { get; } = new(0, 0);

    /// <summary>
    /// Probes existing render nodes. This is device availability, not codec or stream support.
    /// <para>探测现有的渲染节点。这仅表示设备可用，不代表支持某个编解码器或码流。</para>
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when at least one render node initializes a VA display; otherwise <see langword="false"/>.
    /// <para>至少有一个渲染节点能初始化 VA display 时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</para>
    /// </returns>
    public static bool IsSupported()
    {
        if (!OperatingSystem.IsLinux()) return false;
        return RenderNodes().Any(Probe);
    }

    internal static string[] RenderNodes() => Directory.Exists("/dev/dri")
        ? Directory.GetFiles("/dev/dri", "renderD*").Order(StringComparer.Ordinal).ToArray() : [];

    internal static bool SupportsCodec(VaApiCapability capability, VideoCodecId codec) => capability.Entrypoint == 1 && codec switch
    {
        VideoCodecId.H264 => capability.Profile is 5 or 6 or 7 or 13,
        VideoCodecId.H265 => capability.Profile == 17,
        VideoCodecId.Av1 => capability.Profile == 32,
        _ => false,
    };

    private static bool Probe(string node)
    {
        try
        {
            using var session = new VaApiSession(node);
            return session.Version.Major > 0;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Queries actual driver profile/entrypoint pairs for decoding, encoding and processing.
    /// <para>查询驱动实际提供的解码、编码与处理 profile/entrypoint 组合。</para>
    /// </summary>
    /// <returns>
    /// Every pair the driver advertises; presence of a pair does not prove a specific stream decodes.
    /// <para>驱动报告的全部组合；某组合存在并不能证明具体码流可解码。</para>
    /// </returns>
    /// <exception cref="ObjectDisposedException">The session was disposed. <para>会话已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">The session belongs to another thread or a query failed.
    /// <para>会话属于其他线程，或查询失败。</para></exception>
    /// <exception cref="InvalidDataException">The driver reported an out-of-range capability count.
    /// <para>驱动报告的能力计数超出范围。</para></exception>
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

    /// <summary>
    /// Creates an 8-bit YUV420 context and reference surface pool, replacing any prior context.
    /// <para>创建 8 位 YUV420 上下文与参考表面池，并替换之前的任何上下文。</para>
    /// </summary>
    /// <param name="profile">VA profile value the driver must advertise; see <see cref="QueryCapabilities"/>.
    /// <para>驱动必须报告的 VA profile 值；参见 <see cref="QueryCapabilities"/>。</para></param>
    /// <param name="entrypoint">VA entrypoint value paired with <c>profile</c>.
    /// <para>与 <c>profile</c> 配对的 VA entrypoint 值。</para></param>
    /// <param name="width">Decoded frame width in pixels, within the shared dimension limits.
    /// <para>解码帧宽度（像素），须处于共享尺寸上限之内。</para></param>
    /// <param name="height">Decoded frame height in pixels, within the shared dimension limits.
    /// <para>解码帧高度（像素），须处于共享尺寸上限之内。</para></param>
    /// <param name="surfaceCount">Pool size from 1 to 32 surfaces; defaults to 16.
    /// <para>池大小，1 到 32 个表面；默认为 16。</para></param>
    /// <exception cref="ObjectDisposedException">The session was disposed. <para>会话已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">The session belongs to another thread or a VA call failed.
    /// <para>会话属于其他线程，或某次 VA 调用失败。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">Dimensions or <c>surfaceCount</c> are outside the supported bounds.
    /// <para>尺寸或 <c>surfaceCount</c> 超出支持范围。</para></exception>
    /// <exception cref="NotSupportedException">The profile/entrypoint pair is unavailable on this render node.
    /// <para>该 profile/entrypoint 组合在此渲染节点上不可用。</para></exception>
    /// <exception cref="InvalidDataException">The driver reported an out-of-range capability count.
    /// <para>驱动报告的能力计数超出范围。</para></exception>
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

    /// <summary>
    /// Gets a native surface ID for use in serialized reference-picture parameters.
    /// <para>获取原生表面 ID，用于序列化的参考帧参数中。</para>
    /// </summary>
    /// <param name="index">Zero-based position in the pool created by <see cref="Configure"/>.
    /// <para>由 <see cref="Configure"/> 创建的池中的从零开始的索引。</para></param>
    /// <returns>
    /// The VA surface ID; valid only while the current context lives.
    /// <para>VA 表面 ID；仅在当前上下文存活期间有效。</para>
    /// </returns>
    /// <exception cref="ObjectDisposedException">The session was disposed. <para>会话已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">The session belongs to another thread.
    /// <para>会话属于其他线程。</para></exception>
    /// <exception cref="IndexOutOfRangeException"><c>index</c> is outside the surface pool.
    /// <para><c>index</c> 超出表面池范围。</para></exception>
    public uint GetSurfaceId(int index) { CheckReady(); return _surfaces[index]; }

    /// <summary>
    /// Copies and submits a complete picture's native parameter buffers to a configured surface.
    /// <para>将完整图像的原生参数缓冲区复制并提交到已配置的表面。</para>
    /// </summary>
    /// <param name="surfaceIndex">Zero-based position in the pool created by <see cref="Configure"/>.
    /// <para>由 <see cref="Configure"/> 创建的池中的从零开始的索引。</para></param>
    /// <param name="buffers">
    /// One to 4096 codec/slice buffers whose combined data stays within the access-unit limit; each element size
    /// must divide its data length.
    /// <para>1 到 4096 个编解码器/切片缓冲区，其数据总大小不得超过访问单元上限；每个元素的 ElementSize 必须能整除其数据长度。</para>
    /// </param>
    /// <exception cref="ObjectDisposedException">The session was disposed. <para>会话已被释放。</para></exception>
    /// <exception cref="ArgumentNullException"><c>buffers</c> or an element is null.
    /// <para><c>buffers</c> 或其某个元素为 null。</para></exception>
    /// <exception cref="InvalidOperationException">The session belongs to another thread or a VA render call failed.
    /// <para>会话属于其他线程，或某次 VA 渲染调用失败。</para></exception>
    /// <exception cref="ArgumentException">A buffer layout is invalid or the total size is excessive.
    /// <para>某个缓冲区布局无效或总大小超限。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The buffer count is outside 1 to 4096.
    /// <para>缓冲区数量不在 1 到 4096 范围内。</para></exception>
    /// <exception cref="IndexOutOfRangeException"><c>surfaceIndex</c> is outside the surface pool.
    /// <para><c>surfaceIndex</c> 超出表面池范围。</para></exception>
    public void Submit(int surfaceIndex, IReadOnlyList<VaApiBuffer> buffers)
    {
        CheckReady();
        ArgumentNullException.ThrowIfNull(buffers);
        uint target = _surfaces[surfaceIndex];
        if (buffers.Count is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(buffers));
        var ids = new List<uint>(buffers.Count);
        long total = 0;
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
    /// <para>等待解码完成并导出只读的合成 DRM PRIME 层。调用方必须释放返回的描述符，以关闭所有导出的文件描述符。</para>
    /// </summary>
    /// <param name="index">Zero-based position in the pool created by <see cref="Configure"/>.
    /// <para>由 <see cref="Configure"/> 创建的池中的从零开始的索引。</para></param>
    /// <returns>
    /// An owned <see cref="DmaBufSurface"/> snapshot; independent of later session state until disposed.
    /// <para>已拥有的 <see cref="DmaBufSurface"/> 快照；在被释放前独立于后续的会话状态。</para>
    /// </returns>
    /// <exception cref="ObjectDisposedException">The session was disposed. <para>会话已被释放。</para></exception>
    /// <exception cref="InvalidOperationException">The session belongs to another thread or a VA call failed.
    /// <para>会话属于其他线程，或某次 VA 调用失败。</para></exception>
    /// <exception cref="IndexOutOfRangeException"><c>index</c> is outside the surface pool.
    /// <para><c>index</c> 超出表面池范围。</para></exception>
    /// <exception cref="InvalidDataException">The driver returned an inconsistent DRM descriptor.
    /// <para>驱动返回了不一致的 DRM 描述符。</para></exception>
    public DmaBufSurface ExportSurface(int index)
    {
        CheckReady();
        uint id = _surfaces[index];
        Check(VaNative.vaSyncSurface(_display, id), "sync surface");
        var descriptor = new DmaBufDescriptor();
        Check(VaNative.vaExportSurfaceHandle(_display, id, 0x40000000, 1 | 8, &descriptor), "export DRM PRIME 2");
        return new DmaBufSurface(descriptor);
    }

    /// <summary>
    /// Terminates the VA display, releases the context and surface pool, and closes the render node; repeat calls are no-ops.
    /// <para>终止 VA display、释放上下文与表面池并关闭渲染节点；重复调用不执行任何操作。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The session is still alive and belongs to another thread.
    /// <para>会话仍然存活且属于其他线程。</para>
    /// </exception>
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