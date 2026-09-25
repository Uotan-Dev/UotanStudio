using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Acceleration.Linux;

[StructLayout(LayoutKind.Sequential)]
internal struct DmaBufObject { public int Fd; public uint Size; public ulong Modifier; }
[InlineArray(4)] internal struct DmaBufObjects { private DmaBufObject _element0; }
[InlineArray(4)] internal struct FourUInts { private uint _element0; }
[StructLayout(LayoutKind.Sequential)]
internal struct DmaBufLayer { public uint Format, Planes; public FourUInts ObjectIndices, Offsets, Pitches; }
[InlineArray(4)] internal struct DmaBufLayers { private DmaBufLayer _element0; }
[StructLayout(LayoutKind.Sequential)]
internal struct DmaBufDescriptor
{
    public uint Format, Width, Height, ObjectCount;
    public DmaBufObjects Objects;
    public uint LayerCount;
    public DmaBufLayers Layers;
}

/// <summary>
/// Owns the DMA-BUF file descriptors exported from a VA surface.
/// <para>拥有从 VA 表面导出的 DMA-BUF 文件描述符。</para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class DmaBufSurface : IDisposable
{
    internal DmaBufDescriptor Descriptor;
    private bool _disposed;
    internal DmaBufSurface(DmaBufDescriptor descriptor)
    {
        Descriptor = descriptor;
        if (descriptor.ObjectCount is < 1 or > 4 || descriptor.LayerCount is < 1 or > 4)
        {
            Dispose();
            throw new InvalidDataException("Invalid DRM PRIME descriptor.");
        }
    }
    /// <summary>Gets the exported width in pixels. <para>导出图像的宽度（像素）。</para></summary>
    /// <value>The width recorded in the DRM PRIME descriptor. <para>DRM PRIME 描述符中记录的宽度。</para></value>
    public uint Width => Descriptor.Width;
    /// <summary>Gets the exported height in pixels. <para>导出图像的高度（像素）。</para></summary>
    /// <value>The height recorded in the DRM PRIME descriptor. <para>DRM PRIME 描述符中记录的高度。</para></value>
    public uint Height => Descriptor.Height;
    /// <summary>Gets the number of DRM layers. <para>DRM 层数量。</para></summary>
    /// <value>A layer count between 1 and 4. <para>介于 1 到 4 的层计数。</para></value>
    public uint LayerCount => Descriptor.LayerCount;
    internal void CheckAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
    /// <summary>
    /// Closes every exported DMA-BUF file descriptor; safe to call more than once.
    /// <para>关闭所有导出的 DMA-BUF 文件描述符；可安全地多次调用。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = 0; i < Math.Min(Descriptor.ObjectCount, 4); i++)
            if (Descriptor.Objects[i].Fd >= 0) _ = VaNative.close(Descriptor.Objects[i].Fd);
    }
}

/// <summary>
/// Owns an EGLImage imported from a VA DMA-BUF layer into an existing EGL display.
/// <para>Linux VA 表面到 EGL 图像的桥接；EGL display/context 的生命周期由渲染器持有。</para>
/// </summary>
/// <remarks>
/// Destroy images before destroying their EGL display. All operations belong on the renderer's thread.
/// <para>必须先销毁图像，再销毁其所属的 EGL display。所有操作都必须绑定在渲染器线程上执行。</para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe partial class EglDmaBufImage : IDisposable
{
    private readonly nint _display;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly delegate* unmanaged<nint, nint, uint> _destroy;
    private nint _image;
    private EglDmaBufImage(nint display, nint image, delegate* unmanaged<nint, nint, uint> destroy)
    { _display = display; _image = image; _destroy = destroy; }
    /// <summary>
    /// Gets the borrowed EGLImage handle for texture binding.
    /// <para>获取用于纹理绑定的借用 EGLImage 句柄。</para>
    /// </summary>
    /// <value>
    /// The raw handle, valid only while this instance is alive and owned by the caller's EGL display.
    /// <para>原始句柄，仅在本实例存活且归属调用方的 EGL display 期间有效。</para>
    /// </value>
    /// <exception cref="ObjectDisposedException">The image was already destroyed. <para>图像已被销毁。</para></exception>
    public nint Handle => _image != 0 ? _image : throw new ObjectDisposedException(nameof(EglDmaBufImage));

    /// <summary>
    /// Imports a composed DRM layer, preserving per-object format modifiers.
    /// <para>导入已合成的 DRM 层，并保留每个对象的格式修饰符（modifier）。</para>
    /// </summary>
    /// <param name="eglDisplay">
    /// An initialized EGL display on the caller's renderer thread.
    /// <para>调用方渲染器线程上已初始化的 EGL display。</para>
    /// </param>
    /// <param name="surface">The live DMA-BUF descriptor exported from a VA surface.
    /// <para>从 VA 表面导出的有效 DMA-BUF 描述符。</para></param>
    /// <param name="layerIndex">Index of the composed layer to import; the descriptor must expose exactly one layer.
    /// <para>要导入的合成层索引；描述符必须只暴露一个层。</para></param>
    /// <returns>
    /// An owned EGLImage wrapper that must be disposed on the same renderer thread.
    /// <para>已拥有的 EGLImage 包装器，必须在同一渲染器线程上释放。</para>
    /// </returns>
    /// <exception cref="PlatformNotSupportedException">The current OS is not Linux.
    /// <para>当前操作系统不是 Linux。</para></exception>
    /// <exception cref="ArgumentNullException"><c>surface</c> is null. <para><c>surface</c> 为 null。</para></exception>
    /// <exception cref="ObjectDisposedException">The source surface was disposed. <para>源表面已被释放。</para></exception>
    /// <exception cref="ArgumentException"><c>eglDisplay</c> is zero. <para><c>eglDisplay</c> 为零。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><c>layerIndex</c> is outside the layer set.
    /// <para><c>layerIndex</c> 超出层集合范围。</para></exception>
    /// <exception cref="NotSupportedException">
    /// EGL DMA-BUF or modifier import is unavailable, the descriptor is not a single composed layer,
    /// or a layer has a plane count outside the supported 1 to 3 range.
    /// <para>EGL 不支持 DMA-BUF 或 modifier 导入、描述符不是单个合成层，或层的平面数不在支持的 1 到 3 范围内。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">A DRM object index or plane is inconsistent.
    /// <para>DRM 对象索引或平面数据不一致。</para></exception>
    /// <exception cref="InvalidOperationException">
    /// <c>eglCreateImageKHR</c> rejected the attribute list.
    /// <para><c>eglCreateImageKHR</c> 拒绝了属性列表。</para>
    /// </exception>
    public static EglDmaBufImage Import(nint eglDisplay, DmaBufSurface surface, int layerIndex = 0)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        ArgumentNullException.ThrowIfNull(surface);
        surface.CheckAlive();
        if (eglDisplay == 0) throw new ArgumentException("An initialized EGLDisplay is required.", nameof(eglDisplay));
        string extensions = Marshal.PtrToStringUTF8(eglQueryString(eglDisplay, 0x3055)) ?? "";
        string[] names = extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!names.Contains("EGL_EXT_image_dma_buf_import")) throw new NotSupportedException("EGL DMA-BUF import is unavailable.");
        bool modifiers = names.Contains("EGL_EXT_image_dma_buf_import_modifiers");
        var descriptor = surface.Descriptor;
        if (descriptor.LayerCount != 1) throw new NotSupportedException("Import requires a composed DRM layer; separate chroma layers need individual dimensions.");
        if ((uint)layerIndex >= descriptor.LayerCount) throw new ArgumentOutOfRangeException(nameof(layerIndex));
        DmaBufLayer layer = descriptor.Layers[layerIndex];
        if (layer.Planes is < 1 or > 3) throw new NotSupportedException("EGL import supports one to three planes.");
        var attributes = new List<int> { 0x3057, checked((int)descriptor.Width), 0x3056, checked((int)descriptor.Height), 0x3271, unchecked((int)layer.Format) };
        for (int i = 0; i < layer.Planes; i++)
        {
            uint objectIndex = layer.ObjectIndices[i];
            if (objectIndex >= descriptor.ObjectCount) throw new InvalidDataException("DRM object index is out of range.");
            DmaBufObject obj = descriptor.Objects[(int)objectIndex];
            if (obj.Fd < 0 || layer.Pitches[i] == 0 || layer.Offsets[i] >= obj.Size) throw new InvalidDataException("Invalid DRM plane.");
            attributes.AddRange([0x3272 + i * 3, obj.Fd, 0x3273 + i * 3, checked((int)layer.Offsets[i]), 0x3274 + i * 3, checked((int)layer.Pitches[i])]);
            if (modifiers && obj.Modifier != 0x00ffffffffffffff)
                attributes.AddRange([0x3443 + i * 2, unchecked((int)obj.Modifier), 0x3444 + i * 2, unchecked((int)(obj.Modifier >> 32))]);
            else if (obj.Modifier != 0 && obj.Modifier != 0x00ffffffffffffff)
                throw new NotSupportedException("This surface requires EGL modifier import support.");
        }
        attributes.Add(0x3038);
        var create = (delegate* unmanaged<nint, nint, uint, nint, int*, nint>)eglGetProcAddress("eglCreateImageKHR");
        var destroy = (delegate* unmanaged<nint, nint, uint>)eglGetProcAddress("eglDestroyImageKHR");
        if (create == null || destroy == null) throw new NotSupportedException("EGL image entrypoints are unavailable.");
        int[] array = attributes.ToArray();
        fixed (int* p = array)
        {
            nint image = create(eglDisplay, 0, 0x3270, 0, p);
            if (image == 0) throw new InvalidOperationException($"EGL DMA-BUF import failed: 0x{eglGetError():X}.");
            return new(eglDisplay, image, destroy);
        }
    }

    /// <summary>
    /// Binds this image to the currently bound GL_TEXTURE_EXTERNAL_OES texture in a current GLES context.
    /// <para>在当前的 GLES 上下文中，将此图像绑定到当前绑定的 GL_TEXTURE_EXTERNAL_OES 纹理。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The call comes from a thread other than the owning renderer thread.
    /// <para>调用来自除所属渲染器线程之外的其他线程。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The current context lacks GL_OES_EGL_image_external or its entrypoint.
    /// <para>当前上下文缺少 GL_OES_EGL_image_external 扩展或其入口点。</para>
    /// </exception>
    /// <exception cref="ObjectDisposedException">The image was already destroyed. <para>图像已被销毁。</para></exception>
    public void BindExternalTexture()
    {
        CheckThread();
        var getString = (delegate* unmanaged<uint, nint>)eglGetProcAddress("glGetString");
        string extensions = getString == null ? "" : Marshal.PtrToStringUTF8(getString(0x1F03)) ?? "";
        if (!extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("GL_OES_EGL_image_external"))
            throw new NotSupportedException("A current GLES context with GL_OES_EGL_image_external is required.");
        var bind = (delegate* unmanaged<uint, nint, void>)eglGetProcAddress("glEGLImageTargetTexture2DOES");
        if (bind == null) throw new NotSupportedException("GL_OES_EGL_image_external is unavailable.");
        bind(0x8D65, Handle);
    }
    /// <summary>
    /// Destroys the EGLImage on the owning renderer thread; calling an already-disposed instance is a no-op.
    /// <para>在所属的渲染器线程上销毁 EGLImage；对已释放的实例重复调用不执行任何操作。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The image is still alive and the call comes from a thread other than the owning renderer thread.
    /// <para>图像仍然存活，且调用来自除所属渲染器线程之外的其他线程。</para>
    /// </exception>
    public void Dispose()
    {
        if (_image == 0) return;
        CheckThread();
        _ = _destroy(_display, _image);
        _image = 0;
    }
    private void CheckThread()
    {
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("EGL image belongs to another renderer thread.");
    }
    [LibraryImport("libEGL.so.1")] private static partial nint eglQueryString(nint display, int name);
    [LibraryImport("libEGL.so.1", StringMarshalling = StringMarshalling.Utf8)] private static partial nint eglGetProcAddress(string name);
    [LibraryImport("libEGL.so.1")] private static partial int eglGetError();
}