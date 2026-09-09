using System.Runtime.InteropServices;

namespace UotanStudio.Media.Acceleration.Linux;

// libva 2.22 va.h and va_drm.h. All native handles are owned by VaApiSession.
internal static unsafe partial class VaNative
{
    private const string Va = "libva.so.2";
    [LibraryImport("libva-drm.so.2")] internal static partial nint vaGetDisplayDRM(int fd);
    [LibraryImport(Va)] internal static partial int vaInitialize(nint display, out int major, out int minor);
    [LibraryImport(Va)] internal static partial int vaTerminate(nint display);
    [LibraryImport(Va)] internal static partial int vaMaxNumProfiles(nint display);
    [LibraryImport(Va)] internal static partial int vaMaxNumEntrypoints(nint display);
    [LibraryImport(Va)] internal static partial int vaQueryConfigProfiles(nint display, int* profiles, out int count);
    [LibraryImport(Va)] internal static partial int vaQueryConfigEntrypoints(nint display, int profile, int* entries, out int count);
    [LibraryImport(Va)] internal static partial int vaCreateConfig(nint display, int profile, int entrypoint, nint attributes, int count, out uint config);
    [LibraryImport(Va)] internal static partial int vaDestroyConfig(nint display, uint config);
    [LibraryImport(Va)] internal static partial int vaCreateSurfaces(nint display, uint format, uint width, uint height, uint* surfaces, uint count, nint attributes, uint attributeCount);
    [LibraryImport(Va)] internal static partial int vaDestroySurfaces(nint display, uint* surfaces, int count);
    [LibraryImport(Va)] internal static partial int vaCreateContext(nint display, uint config, int width, int height, int flags, uint* targets, int targetCount, out uint context);
    [LibraryImport(Va)] internal static partial int vaDestroyContext(nint display, uint context);
    [LibraryImport(Va)] internal static partial int vaCreateBuffer(nint display, uint context, int type, uint size, uint count, void* data, out uint buffer);
    [LibraryImport(Va)] internal static partial int vaDestroyBuffer(nint display, uint buffer);
    [LibraryImport(Va)] internal static partial int vaBeginPicture(nint display, uint context, uint surface);
    [LibraryImport(Va)] internal static partial int vaRenderPicture(nint display, uint context, uint* buffers, int count);
    [LibraryImport(Va)] internal static partial int vaEndPicture(nint display, uint context);
    [LibraryImport(Va)] internal static partial int vaSyncSurface(nint display, uint surface);
    [LibraryImport(Va)] internal static partial int vaExportSurfaceHandle(nint display, uint surface, uint memoryType, uint flags, void* descriptor);
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] internal static partial int open(string path, int flags);
    [LibraryImport("libc")] internal static partial int close(int fd);
}
