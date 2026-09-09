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

/// <summary>Owns the DMA-BUF file descriptors exported from a VA surface.</summary>
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
    /// <summary>Gets the exported width in pixels.</summary>
    public uint Width => Descriptor.Width;
    /// <summary>Gets the exported height in pixels.</summary>
    public uint Height => Descriptor.Height;
    /// <summary>Gets the number of DRM layers.</summary>
    public uint LayerCount => Descriptor.LayerCount;
    internal void CheckAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
    /// <inheritdoc />
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
/// Linux VA 表面到 EGL 图像的桥接；EGL display/context 的生命周期由渲染器持有。
/// </summary>
/// <remarks>Destroy images before destroying their EGL display. All operations belong on the renderer's thread.</remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe partial class EglDmaBufImage : IDisposable
{
    private readonly nint _display;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly delegate* unmanaged<nint, nint, uint> _destroy;
    private nint _image;
    private EglDmaBufImage(nint display, nint image, delegate* unmanaged<nint, nint, uint> destroy)
    { _display = display; _image = image; _destroy = destroy; }
    /// <summary>Gets the borrowed EGLImage handle for texture binding.</summary>
    public nint Handle => _image != 0 ? _image : throw new ObjectDisposedException(nameof(EglDmaBufImage));

    /// <summary>Imports a composed DRM layer, preserving per-object format modifiers.</summary>
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
        attributes.Add(0x3038); // EGL_NONE
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

    /// <summary>Binds this image to the currently bound GL_TEXTURE_EXTERNAL_OES texture in a current GLES context.</summary>
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
    /// <inheritdoc />
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
