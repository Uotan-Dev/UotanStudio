using System.Buffers.Binary;
using System.Text;
using FormatDetector = UotanStudio.Firmware.Core.FormatDetector;

namespace UotanStudio.Services.Firmware;

/// <summary>Filesystem layout probed from a raw image header. 从 raw 镜像头部探测出的文件系统布局。</summary>
internal enum RawImageLayout
{
    /// <summary>ext4 filesystem image (magic 0xEF53 at offset 1080). ext4 文件系统镜像（魔数 0xEF53 位于偏移 1080）。</summary>
    Ext4,

    /// <summary>EROFS filesystem image (magic 0xE0F5E1E2 at offset 1024). EROFS 文件系统镜像（魔数 0xE0F5E1E2 位于偏移 1024）。</summary>
    Erofs,

    /// <summary>F2FS filesystem image (magic 0xF2F52010 at offset 1024). F2FS 文件系统镜像（魔数 0xF2F52010 位于偏移 1024）。</summary>
    F2fs,
}

/// <summary>
/// <para>镜像输入侧职责：格式探测与源文件路径解析。只返回纯数据（格式字符串、布局枚举、绝对路径），
/// 依赖经构造参数显式注入；不引用视图、ViewModel 或任何 UI 单例。解包目录规则仍由
/// <see cref="ImageOperationPaths"/> 负责，本服务不生成输出路径。</para>
/// Image input-side responsibilities: format probing and source-path resolution. Only plain data is
/// returned (format strings, layout enums, absolute paths); dependencies are injected explicitly via
/// constructor parameters; no view, ViewModel or UI singleton is referenced. Unpack-directory rules
/// stay with <see cref="ImageOperationPaths"/>; this service never produces output paths.
/// </summary>
internal sealed class ImageInputService
{
    private readonly Func<string, string?> _resolvePath;

    /// <summary>Warning sink: (category, message). 警告输出槽：（类别，消息）。</summary>
    private readonly Action<string, string> _logWarning;

    /// <summary>
    /// <para>创建输入服务。<paramref name="resolvePath"/> 把工程内相对路径解析为绝对路径
    /// （生产环境来自 <c>ProjectContextService.ResolvePath</c>）；<paramref name="logWarning"/>
    /// 接收（类别，消息）警告（生产环境来自 <c>LogService.Write(LogLevel.Warn, …)</c>）。</para>
    /// Creates the input service. <paramref name="resolvePath"/> maps a project-relative path to an
    /// absolute one (production: <c>ProjectContextService.ResolvePath</c>); <paramref name="logWarning"/>
    /// receives (category, message) warnings (production: <c>LogService.Write(LogLevel.Warn, …)</c>).
    /// </summary>
    public ImageInputService(Func<string, string?> resolvePath, Action<string, string> logWarning)
    {
        _resolvePath = resolvePath ?? throw new ArgumentNullException(nameof(resolvePath));
        _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
    }

    /// <summary>
    /// <para>解析并校验源文件/目录条目：相对路径不可解析或目标不存在时记警告并返回 null，
    /// 目录条目按 <c>Directory.Exists</c> 校验，文件条目按 <c>File.Exists</c> 校验。</para>
    /// Resolves and validates a source file/directory entry: logs a warning and returns null when the
    /// relative path cannot be resolved or the target is missing; directory entries check
    /// <c>Directory.Exists</c> while file entries check <c>File.Exists</c>.
    /// </summary>
    /// <param name="relativePath"><para>工程内相对路径。</para>Project-relative path.</param>
    /// <param name="isDirectory"><para>是否为目录条目（打包模式）。</para>Whether the entry is a directory (pack mode).</param>
    /// <param name="operation"><para>操作名，仅用于警告文案。</para>Operation name, used in warning text only.</param>
    /// <returns><para>绝对源路径；不可用为 null。</para>Absolute source path; null when unavailable.</returns>
    internal string? ResolveSourcePath(string relativePath, bool isDirectory, string operation)
    {
        var source = _resolvePath(relativePath);
        if (source is null)
        {
            _logWarning("Images", $"{operation} aborted: path not found: {relativePath}  {operation}中止：找不到路径：{relativePath}");
            return null;
        }

        // 目录条目（打包模式）检查 Directory.Exists，文件条目检查 File.Exists。
        // Directory entries (pack mode) check Directory.Exists; file entries check File.Exists.
        if (isDirectory ? !Directory.Exists(source) : !File.Exists(source))
        {
            _logWarning("Images", $"{operation} aborted: not found: {relativePath}  {operation}中止：找不到：{relativePath}");
            return null;
        }

        return source;
    }

    /// <summary>
    /// <para>探测镜像格式：扩展名优先（cpio/oppo/sparse），随后读取 1082 字节头部按魔数判定
    /// （Android sparse 0xED26FF3A、payload "CrAU"、boot "ANDROID!"/"VNDRBOOT"、ext4 超级块
    /// 结构校验、erofs 0xE0F5E1E2@1024、f2fs 0xF2F52010@1024）；ELF 可执行文件一律返回
    /// <c>unknown</c>，其段内数据可能偶然命中文件系统魔数；头部读取失败回退扩展名推断，
    /// 仍无法识别返回 "unknown"。只读文件头部，不做任何写入或调度。</para>
    /// Probes the image format: extensions first (cpio/oppo/sparse), then a 1082-byte header is read
    /// and matched by magic (Android sparse 0xED26FF3A, payload "CrAU", boot "ANDROID!"/"VNDRBOOT",
    /// a structural ext4 superblock check, erofs 0xE0F5E1E2@1024, f2fs 0xF2F52010@1024); ELF
    /// executables always return <c>unknown</c> because their segment data can coincidentally match a
    /// filesystem magic; header read failures fall back to extension inference, and "unknown" is
    /// returned when nothing matches. Read-only header access; no writes or dispatching.
    /// </summary>
    internal static string DetectFormat(string filePath)
    {
        // OTA block data may begin with filesystem magic; the container suffix takes precedence.
        if (filePath.EndsWith(".transfer.list", StringComparison.OrdinalIgnoreCase)) return "dat";
        if (filePath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) return "dat";
        if (filePath.EndsWith(".br", StringComparison.OrdinalIgnoreCase)) return "brotli";
        if (new UotanStudio.Firmware.BlockImage.Lp.LpFormatHandler().CanHandle(filePath)) return "super";
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        switch (extension)
        {
            case ".cpio":
                return "cpio";
            case ".ofp":
            case ".ops":
            case ".ozip":
                return "oppo";
            case ".simg":
            case ".sparse":
                return "sparse";
        }

        try
        {
            // 读取 1082 字节头部：ext4 魔数 0xEF53 位于 1080、erofs/f2fs 魔数位于 1024。
            // Read a 1082-byte header: ext4 magic 0xEF53 at 1080, erofs/f2fs magic at 1024.
            var header = FormatDetector.ReadHeader(filePath, 1082);

            // ELF 可执行文件不是镜像：其偏移 1024/1080 处的段内数据可能偶然命中文件系统魔数。
            // An ELF executable is never an image: its segment data at offsets 1024/1080 can
            // coincidentally match a filesystem magic.
            if (header.Length >= 4 && header[0] == 0x7F && header[1] == (byte)'E'
                && header[2] == (byte)'L' && header[3] == (byte)'F')
            {
                return "unknown";
            }

            if (header.Length >= 4)
            {
                // Android sparse 魔数 0xED26FF3A（小端）。
                // Android sparse magic 0xED26FF3A (little-endian).
                if (header[0] == 0x3A && header[1] == 0xFF && header[2] == 0x26 && header[3] == 0xED)
                {
                    return "sparse";
                }

                if (Encoding.ASCII.GetString(header, 0, 4) == "CrAU")
                {
                    return "payload";
                }
            }

            if (header.Length >= 8)
            {
                var magic = Encoding.ASCII.GetString(header, 0, 8);
                if (magic.StartsWith("ANDROID!", StringComparison.Ordinal) || magic.StartsWith("VNDRBOOT", StringComparison.Ordinal))
                {
                    return "boot";
                }
            }

            // ext4 文件系统镜像：超级块结构校验（见 IsExt4Superblock）。
            // ext4 filesystem image: structural superblock validation (see IsExt4Superblock).
            if (IsExt4Superblock(header))
            {
                return "ext4";
            }

            // erofs 文件系统镜像：魔数 0xE0F5E1E2 位于偏移 1024。
            // erofs filesystem image: magic 0xE0F5E1E2 at offset 1024.
            if (header.Length >= 1028
                && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xE0F5E1E2)
            {
                return "erofs";
            }

            // f2fs 文件系统镜像：魔数 0xF2F52010 位于偏移 1024。
            // f2fs filesystem image: magic 0xF2F52010 at offset 1024.
            if (header.Length >= 1028
                && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xF2F52010)
            {
                return "f2fs";
            }
        }
        catch
        {
            // 头部读取失败时回退到扩展名推断。
            // Falls back to extension-based inference when the header cannot be read.
        }

        if (extension is ".img" or ".raw")
        {
            return "raw";
        }

        return extension == ".bin" && Path.GetFileName(filePath).Contains("payload", StringComparison.OrdinalIgnoreCase) ? "payload" : "unknown";
    }

    /// <summary>
    /// <para>读取 1082 字节头部并探测 raw 镜像内的文件系统布局：ext4 超级块结构校验
    /// （见 <see cref="IsExt4Superblock"/>）、erofs/f2fs 魔数位于 1024。头部读取失败时异常原样抛出
    /// （与既有解包回退行为一致），未命中返回 null。</para>
    /// Reads a 1082-byte header and probes the filesystem layout inside a raw image: a structural ext4
    /// superblock check (see <see cref="IsExt4Superblock"/>), erofs/f2fs magic at 1024. Header read
    /// failures propagate unchanged (matching the existing unpack fallback behavior); null when no
    /// magic matches.
    /// </summary>
    internal static RawImageLayout? ProbeFilesystemHeader(string filePath)
    {
        var header = FormatDetector.ReadHeader(filePath, 1082);
        if (IsExt4Superblock(header))
        {
            return RawImageLayout.Ext4;
        }

        if (header.Length >= 1028
            && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xE0F5E1E2)
        {
            return RawImageLayout.Erofs;
        }

        if (header.Length >= 1028
            && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xF2F52010)
        {
            return RawImageLayout.F2fs;
        }

        return null;
    }

    /// <summary>
    /// <para>ext4 超级块结构校验：仅比对偏移 1080 的 2 字节魔数 0xEF53 会把偶然落上
    /// <c>53 EF</c> 的任意二进制（如 ELF 段数据）误判为镜像，因此同时要求魔数、
    /// <c>s_inodes_count</c>（@1024）与 <c>s_blocks_count_lo</c>（@1028）非零，且
    /// <c>s_log_block_size</c>（@1048）推导的块大小不超过 64 KiB。</para>
    /// Structural ext4 superblock validation: matching only the 2-byte magic 0xEF53 at offset 1080
    /// misclassifies arbitrary binaries (e.g. ELF segment data) whose bytes happen to be
    /// <c>53 EF</c>, so the check additionally requires a nonzero <c>s_inodes_count</c> (@1024) and
    /// <c>s_blocks_count_lo</c> (@1028) and a <c>s_log_block_size</c> (@1048) whose derived block
    /// size stays within 64 KiB.
    /// </summary>
    private static bool IsExt4Superblock(ReadOnlySpan<byte> header) =>
        header.Length >= 1082
        && BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(1080, 2)) == 0xEF53
        && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1024, 4)) != 0
        && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1028, 4)) != 0
        && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1048, 4)) <= 6;
}