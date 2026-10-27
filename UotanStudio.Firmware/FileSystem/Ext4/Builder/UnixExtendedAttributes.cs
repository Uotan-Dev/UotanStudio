using System.Runtime.InteropServices;

namespace UotanStudio.Firmware.FileSystem.Ext4.Builder;

/// <summary>
/// Reads one extended attribute through the platform <c>getxattr</c> entry point, which returns the attribute value directly; "absent, empty or unreadable" all mean no attribute.
/// <para>通过平台 <c>getxattr</c> 入口直接读取单个扩展属性值；“不存在、为空或不可读”一律视为无属性。</para>
/// </summary>
internal static unsafe partial class UnixExtendedAttributes
{
    /// <summary>
    /// Upper bound for one attribute value; the kernel caps xattr values at 64 KiB, so a larger probe result cannot be a real selinux context.
    /// <para>单个属性值的上限；内核把 xattr 值限制在 64 KiB 以内，因此更大的探测结果不可能是真实的 selinux 上下文。</para>
    /// </summary>
    private const int MaxValueBytes = 1 << 16;

    [LibraryImport("libc", EntryPoint = "getxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetXattrLinux(string path, string name, byte* value, nuint size);

    [LibraryImport("libc", EntryPoint = "getxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetXattrMacOs(string path, string name, byte* value, nuint size, uint position, int options);

    /// <summary>
    /// Reads one attribute value; returns <see langword="false"/> when the attribute is absent, empty or unreadable.
    /// <para>读取一个属性值；属性不存在、为空或不可读时返回 <see langword="false"/>。</para>
    /// </summary>
    /// <param name="path">Host path to inspect. <para>要检查的宿主路径。</para></param>
    /// <param name="name">Full attribute name, for example <c>security.selinux</c>. <para>完整属性名，例如 <c>security.selinux</c>。</para></param>
    /// <param name="value">Receives the exact value bytes when the call succeeds. <para>调用成功时接收精确的属性值字节。</para></param>
    /// <returns><see langword="true"/> when a non-empty value was read. <para>读到非空值时返回 <see langword="true"/>。</para></returns>
    internal static bool TryRead(string path, string name, out byte[] value)
    {
        value = [];
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var length = ProbeLength(path, name);
                if (length <= 0 || length > MaxValueBytes)
                {
                    return false;
                }

                var buffer = new byte[(int)length];
                nint read;
                fixed (byte* pointer = buffer)
                {
                    read = OperatingSystem.IsMacOS()
                        ? GetXattrMacOs(path, name, pointer, (nuint)buffer.Length, 0, 0)
                        : GetXattrLinux(path, name, pointer, (nuint)buffer.Length);
                }

                if (read <= 0)
                {
                    return false;
                }

                if (read <= buffer.Length)
                {
                    value = read == buffer.Length ? buffer : buffer[..(int)read];
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static nint ProbeLength(string path, string name)
        => OperatingSystem.IsMacOS()
            ? GetXattrMacOs(path, name, null, 0, 0, 0)
            : GetXattrLinux(path, name, null, 0);
}