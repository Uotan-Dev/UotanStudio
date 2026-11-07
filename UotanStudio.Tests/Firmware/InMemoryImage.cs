using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.F2fs;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Packs a source directory into in-memory image bytes through the streaming builder overloads, for
/// tests that parse or hash the raw image without a file on disk.
/// <para>经流式构建重载把源目录打包进内存字节，供直接解析或散列原始镜像而无需磁盘文件的测试使用。</para>
/// </summary>
internal static class InMemoryImage
{
    /// <summary>
    /// Packs a directory into erofs image bytes.
    /// <para>将目录打包为 erofs 镜像字节。</para>
    /// </summary>
    /// <param name="directoryPath">Existing directory whose tree becomes the image root.
    /// <para>作为镜像根目录的现存目录。</para></param>
    /// <param name="options">Build options; <see langword="null"/> uses validated defaults.
    /// <para>构建选项；为 <see langword="null"/> 时使用经校验的默认值。</para></param>
    /// <returns>The complete image as one byte array.<para>完整镜像的字节数组。</para></returns>
    public static byte[] Erofs(string directoryPath, ErofsOptions? options = null)
    {
        using var image = new MemoryStream();
        new ErofsBuilder().Build(directoryPath, image, options);
        return image.ToArray();
    }

    /// <summary>
    /// Packs a directory into ext4 image bytes.
    /// <para>将目录打包为 ext4 镜像字节。</para>
    /// </summary>
    /// <param name="sourceDirectory">Host directory tree packed into the image; it must exist.
    /// <para>被打包的主机目录树；必须存在。</para></param>
    /// <param name="options">Layout and capacity options; <see langword="null"/> uses defaults.
    /// <para>布局与容量选项；为 <see langword="null"/> 时使用默认值。</para></param>
    /// <returns>The complete image as one byte array.<para>完整镜像的字节数组。</para></returns>
    public static byte[] Ext4(string sourceDirectory, Ext4Options? options = null)
    {
        using var image = new MemoryStream();
        Ext4Builder.Build(sourceDirectory, image, options);
        return image.ToArray();
    }

    /// <summary>
    /// Packs a directory into f2fs image bytes.
    /// <para>将目录打包为 f2fs 镜像字节。</para>
    /// </summary>
    /// <param name="sourceDirectory">Host directory tree packed into the image; it must exist.
    /// <para>被打包的主机目录树；必须存在。</para></param>
    /// <param name="options">Build options; <see langword="null"/> uses validated defaults.
    /// <para>构建选项；为 <see langword="null"/> 时使用经校验的默认值。</para></param>
    /// <returns>The complete image as one byte array.<para>完整镜像的字节数组。</para></returns>
    public static byte[] F2fs(string sourceDirectory, F2fsOptions? options = null)
    {
        using var image = new MemoryStream();
        F2fsBuilder.Build(sourceDirectory, image, options);
        return image.ToArray();
    }
}