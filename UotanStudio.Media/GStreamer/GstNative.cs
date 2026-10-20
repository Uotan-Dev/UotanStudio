using System.Reflection;
using System.Runtime.InteropServices;

namespace UotanStudio.Media.GStreamer;

/// <summary>GStreamer 1.x stable C API; no managed plugin loading.</summary>
internal static unsafe partial class GstNative
{
    private const string Gst = "uotan-gstreamer";
    private const string Video = "uotan-gstvideo";
    private const string App = "uotan-gstapp";
    private const string Glib = "uotan-glib";
    static GstNative() => NativeLibrary.SetDllImportResolver(typeof(GstNative).Assembly, Resolve);
    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var stem = name switch { Gst => "gstreamer-1.0", App => "gstapp-1.0", Glib => "glib-2.0", Video => "gstvideo-1.0", _ => null };
        if (stem is null) return 0;
        var file = OperatingSystem.IsWindows() ? $"lib{stem}-0.dll" : OperatingSystem.IsMacOS() ? $"lib{stem}.0.dylib" : $"lib{stem}.so.0";
        if (NativeLibrary.TryLoad(file, assembly, searchPath, out var handle)) return handle;
        if (OperatingSystem.IsMacOS() && NativeLibrary.TryLoad($"/Library/Frameworks/GStreamer.framework/Versions/1.0/lib/{file}", out handle)) return handle;
        throw new DllNotFoundException($"GStreamer runtime library is missing: {file}. Install GStreamer 1.24 or newer with the required codec plugins.");
    }
    internal static void Initialize()
    {
        if (gst_init_check(0, 0, out var error) == 0) throw new NotSupportedException(ReadError(error));
        if (error != 0) g_error_free(error);
        gst_version(out var major, out var minor, out _, out _);
        if (major != 1 || minor < 24) throw new NotSupportedException("GStreamer 1.24 or newer is required.");
    }
    internal static string ReadError(nint error)
    {
        if (error == 0) return "Unknown GStreamer error.";
        try { return Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "GStreamer error"; }
        finally { g_error_free(error); }
    }
    internal static bool HasElement(string name)
    {
        var factory = gst_element_factory_find(name);
        if (factory == 0) return false;
        gst_object_unref(factory);
        return true;
    }

    /// <summary>
    /// Asks an element factory whether its source pad can negotiate system-memory NV12, using the
    /// pad's own caps query rather than a hard-coded element list.
    /// <para>通过源 pad 自身的 caps 查询判断该元件能否协商系统内存 NV12，而不依赖硬编码的元件清单。</para>
    /// </summary>
    /// <param name="name">
    /// The element factory name to instantiate and query.
    /// <para>要实例化并查询的元件工厂名称。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the intersection with <c>video/x-raw,format=NV12</c> is non-empty, so a
    /// caps filter can be placed directly on the decoder and no conversion stage is needed;
    /// <see langword="false"/> for missing factories, absent source pads, failed factories, or an empty
    /// intersection (hardware elements advertising GPU memory only).
    /// <para>与 <c>video/x-raw,format=NV12</c> 的交集非空时返回 <see langword="true"/>，表示可把 caps 过滤直接压在解码器上、
    /// 无需转换阶段；工厂缺失、无源 pad、实例化失败，或交集为空（例如仅声明 GPU 内存的硬件元件）时返回
    /// <see langword="false"/>。</para>
    /// </returns>
    internal static bool CanOutputNv12(string name)
    {
        var element = gst_element_factory_make(name, null);
        if (element == 0) return false;
        try
        {
            var pad = gst_element_get_static_pad(element, "src");
            if (pad == 0) return false;
            try
            {
                var filter = gst_caps_from_string("video/x-raw,format=NV12");
                if (filter == 0) return false;
                try
                {
                    var queried = gst_pad_query_caps(pad, filter);
                    if (queried == 0) return false;
                    try { return gst_caps_is_empty(queried) == 0; }
                    finally { gst_caps_unref(queried); }
                }
                finally { gst_caps_unref(filter); }
            }
            finally { gst_object_unref(pad); }
        }
        finally { gst_object_unref(element); }
    }
    internal static int BufferPtsOffset => IntPtr.Size == 8 ? 72 : 40;
    internal static void SetTimestamp(nint buffer, long ticks)
    {
        var nanoseconds = checked(ticks * 100);
        Marshal.WriteInt64(buffer, BufferPtsOffset, nanoseconds);
        Marshal.WriteInt64(buffer, BufferPtsOffset + 8, -1);
    }
    internal static long GetTimestamp(nint buffer)
    {
        var value = Marshal.ReadInt64(buffer, BufferPtsOffset);
        return value < 0 ? 0 : value / 100;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MapInfo
    {
        internal nint Memory;
        internal int Flags;
        internal nint Data;
        internal nuint Size, MaxSize;
        private nint _user0, _user1, _user2, _user3, _reserved0, _reserved1, _reserved2, _reserved3;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Colorimetry { internal int Range, Matrix, Transfer, Primaries; }
    [LibraryImport(Video)] internal static partial nint gst_buffer_get_video_meta(nint buffer);
    [LibraryImport(Video)] internal static partial int gst_video_meta_map(nint meta, uint plane, out MapInfo info, out nint data, out int stride, int flags);
    [LibraryImport(Video)] internal static partial int gst_video_meta_unmap(nint meta, uint plane, ref MapInfo info);
    [LibraryImport(Video)] internal static partial int gst_video_colorimetry_from_string(out Colorimetry color, nint value);
    [LibraryImport(Gst, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gst_structure_get_string(nint structure, string field);

    /// <summary>
    /// Reads a string field from a caps structure.
    /// <para>从 caps 结构读取字符串字段。</para>
    /// </summary>
    /// <param name="structure">
    /// The structure returned by <c>gst_caps_get_structure</c>.
    /// <para>由 <c>gst_caps_get_structure</c> 返回的结构。</para>
    /// </param>
    /// <param name="field">
    /// The field name, such as <c>format</c>.
    /// <para>字段名称，例如 <c>format</c>。</para>
    /// </param>
    /// <returns>
    /// The field value, or <see langword="null"/> when the structure omits it.
    /// <para>字段值；结构中不存在该字段时返回 <see langword="null"/>。</para>
    /// </returns>
    internal static string? ReadStructureString(nint structure, string field)
    {
        var value = gst_structure_get_string(structure, field);
        return value == 0 ? null : Marshal.PtrToStringUTF8(value);
    }
    [LibraryImport(Gst)] internal static partial int gst_init_check(nint argc, nint argv, out nint error);
    [LibraryImport(Gst)] internal static partial void gst_version(out uint major, out uint minor, out uint micro, out uint nano);
    [LibraryImport(Gst, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gst_parse_launch(string pipeline, out nint error);
    [LibraryImport(Gst, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gst_element_factory_find(string name);
    [LibraryImport(Gst, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gst_element_factory_make(string factoryName, string? instanceName);
    [LibraryImport(Gst, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gst_element_get_static_pad(nint element, string padName);
    [LibraryImport(Gst, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gst_caps_from_string(string caps);
    [LibraryImport(Gst)] internal static partial nint gst_pad_query_caps(nint pad, nint filter);
    [LibraryImport(Gst)] internal static partial int gst_caps_is_empty(nint caps);
    [LibraryImport(Gst)] internal static partial void gst_caps_unref(nint caps);
    [LibraryImport(Gst, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint gst_bin_get_by_name(nint bin, string name);
    [LibraryImport(Gst)] internal static partial int gst_element_set_state(nint element, int state);
    [LibraryImport(Gst)] internal static partial int gst_element_get_state(nint element, out int state, out int pending, ulong timeout);
    [LibraryImport(Gst)] internal static partial nint gst_element_get_bus(nint element);
    [LibraryImport(Gst)] internal static partial nint gst_bus_pop_filtered(nint bus, uint types);
    [LibraryImport(Gst)] internal static partial void gst_message_parse_error(nint message, out nint error, out nint debug);
    [LibraryImport(Gst)] internal static partial void gst_object_unref(nint value);
    [LibraryImport(Gst)] internal static partial void gst_mini_object_unref(nint value);
    [LibraryImport(Gst)] internal static partial nint gst_buffer_new_allocate(nint allocator, nuint size, nint parameters);
    [LibraryImport(Gst)] internal static partial nuint gst_buffer_fill(nint buffer, nuint offset, void* source, nuint size);
    [LibraryImport(Gst)] internal static partial nuint gst_buffer_extract(nint buffer, nuint offset, void* target, nuint size);
    [LibraryImport(Gst)] internal static partial int gst_buffer_map(nint buffer, out MapInfo info, int flags);
    [LibraryImport(Gst)] internal static partial void gst_buffer_unmap(nint buffer, in MapInfo info);
    [LibraryImport(Gst)] internal static partial nuint gst_buffer_get_size(nint buffer);
    [LibraryImport(Gst)] internal static partial nint gst_sample_get_buffer(nint sample);
    [LibraryImport(Gst)] internal static partial nint gst_sample_get_caps(nint sample);
    [LibraryImport(Gst)] internal static partial nint gst_caps_get_structure(nint caps, uint index);
    [LibraryImport(Gst, StringMarshalling = StringMarshalling.Utf8)] internal static partial int gst_structure_get_int(nint structure, string field, out int value);
    [LibraryImport(App)] internal static partial int gst_app_src_push_buffer(nint source, nint buffer);
    [LibraryImport(App)] internal static partial ulong gst_app_src_get_current_level_bytes(nint source);
    [LibraryImport(App)] internal static partial int gst_app_src_end_of_stream(nint source);
    [LibraryImport(App)] internal static partial nint gst_app_sink_try_pull_sample(nint sink, ulong timeout);
    [LibraryImport(App)] internal static partial int gst_app_sink_is_eos(nint sink);
    [LibraryImport(Glib)] internal static partial void g_error_free(nint error);
    [LibraryImport(Glib)] internal static partial void g_free(nint value);
}