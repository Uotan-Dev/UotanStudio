using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;

namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Extracts one bounded regular configuration file from raw EXT4/EROFS without host path traversal.
/// <para>从原始 EXT4/EROFS 镜像中提取单个有上限的普通配置文件，不允许宿主侧路径穿越。</para>
/// </summary>
public static class ImageConfigurationReader
{
    /// <summary>Streams at most 16 MiB into output, leaving caller streams open; rejects links and special files.
    /// <para>最多向 <paramref name="output"/> 写出 16 MiB 数据，调用方的流保持打开；符号链接与特殊文件会被拒绝。</para></summary>
    /// <example>
    /// <code>
    /// ImageConfigurationReader.Extract(image, "EXT4", "/system/etc/init.rc", target, token);
    /// </code>
    /// </example>
    /// <param name="image">Readable, seekable raw EXT4 or EROFS image positioned at its start; it stays open and its position is not restored.
    /// <para>定位在镜像起始处、可读且可寻址的原始 EXT4 或 EROFS 镜像；调用结束后仍保持打开，且不会恢复其位置。</para></param>
    /// <param name="format">Selects the parser: <c>"EXT4"</c> or <c>"EROFS"</c>, matched case-sensitively; any other value is rejected.
    /// <para>决定使用哪个解析器：区分大小写的 <c>"EXT4"</c> 或 <c>"EROFS"</c>， 其他取值一律拒绝。</para></param>
    /// <param name="path">Image-relative path of 1 to 4096 characters; leading slashes are ignored, backslashes and NUL are forbidden, and no segment may be empty, <c>.</c> or <c>..</c>.
    /// <para>长度为 1 到 4096 个字符的镜像内路径； 忽略前导斜杠，禁止反斜杠与 NUL，且任何一段都不能为空、<c>.</c> 或 <c>..</c>。</para></param>
    /// <param name="output">Writable stream that receives the file bytes from its current position; it is not disposed and not truncated, and writes larger than the 16 MiB ceiling fail.
    /// <para>从当前位置接收文件字节的可写流；不会被释放或截断， 超过 16 MiB 上限的写入会失败。</para></param>
    /// <param name="token">Checked before and after parsing and inside every write.
    /// <para>在解析前后以及每次写入内部都会检查。</para></param>
    /// <exception cref="InvalidDataException">The path is malformed, the format is unsupported, the target is not a regular file of at most 16 MiB, or the written byte count differs from the declared entry size.
    /// <para>路径格式非法、格式不受支持、目标不是不超过 16 MiB 的普通文件，或实际写入字节数与条目声明大小不一致。</para></exception>
    /// <exception cref="FileNotFoundException">No entry in the image matches the normalized path.
    /// <para>镜像中不存在与规整后路径匹配的条目。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled.
    /// <para><paramref name="token"/> 已发出取消信号。</para></exception>
    public static void Extract(Stream image, string format, string path, Stream output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (path.Length is < 1 or > 4096 || path.Contains('\0') || path.Contains('\\'))
        {
            throw new InvalidDataException("Invalid image-relative file path.");
        }
        path = path.TrimStart('/');
        if (path.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("Image path must identify one literal file without traversal.");
        }
        using var source = new BoundedReadStream(image, 0, image.Length, leaveOpen: true);
        using var limited = new CappedWriteStream(output, 16 * 1024 * 1024, token);
        long size;
        switch (format)
        {
            case "EXT4":
                using (var parser = new Ext4Parser(source, 100000, 16 * 1024 * 1024, token))
                {
                    var entry = parser.ListFiles().SingleOrDefault(entry => entry.Path == path)
                        ?? throw new FileNotFoundException("Image configuration file is missing.", path);
                    Validate(entry.Mode, entry.Size);
                    size = entry.Size;
                    parser.ReadFile(path, limited);
                }
                break;
            case "EROFS":
                using (var parser = new ErofsParser(source, 100000, 16 * 1024 * 1024, token))
                {
                    var entry = parser.EnumerateFiles().SingleOrDefault(entry => entry.Path == path)
                        ?? throw new FileNotFoundException("Image configuration file is missing.", path);
                    Validate(entry.Mode, entry.Size);
                    size = entry.Size;
                    parser.ReadFile(path, limited);
                }
                break;
            default:
                throw new PatchingUnsupportedFeatureException("Only raw EXT4 and EROFS image configuration reads are supported.");
        }
        token.ThrowIfCancellationRequested();
        if (limited.Written != size) { throw new PatchingCorruptedException("Image file output does not match its declared size."); }
    }

    private static void Validate(ushort mode, long size)
    {
        if ((mode & 0xF000) != 0x8000 || size is < 0 or > 16 * 1024 * 1024)
        {
            throw new PatchingCorruptedException("Image configuration must be a regular file of at most 16 MiB.");
        }
    }
}