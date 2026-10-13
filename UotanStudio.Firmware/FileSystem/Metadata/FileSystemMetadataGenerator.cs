using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.Core.Text;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.F2fs;

namespace UotanStudio.Firmware.FileSystem.Metadata;

/// <summary>
/// Generates Android-style filesystem metadata files (<c>fs_config</c>, <c>file_contexts</c>, <c>partition_info</c>)
/// from parser inode data after extraction. The flat files are an export for external tools and an optional
/// hand-written input for a fresh directory build; the lossless channel stays <c>metadata.json</c>, so each
/// writer emits only the strict AOSP grammar and reports, without throwing, every entry the grammar cannot hold.
/// <para>从解析器 inode 数据生成 Android 风格的文件系统元数据文件（<c>fs_config</c>、<c>file_contexts</c>、
/// <c>partition_info</c>）。这些扁平文件是供外部工具消费的导出物，也是全新目录构建时的可选人工输入；
/// 无损通道始终是 <c>metadata.json</c>，因此各写入器只输出严格的 AOSP 语法，并对语法无法承载的条目
/// 以警告（而非异常）方式上报。</para>
/// </summary>
internal static class FileSystemMetadataGenerator
{
    private const string DefaultSelinuxLabel = "u:object_r:system_file:s0";
    private const string SelinuxXattrName = "security.selinux";
    private const string CapabilityXattrName = "security.capability";

    /// <summary>
    /// The <c>magic_etc</c> revision field of a <c>security.capability</c> xattr (<c>VFS_CAP_REVISION_MASK</c>)
    /// plus the three revisions whose layout is read here. Revisions 2 and 3 keep the same two 32-bit halves for
    /// the permitted and inheritable sets; revision 3 only appends a namespace root id.
    /// <para><c>security.capability</c> xattr 中 <c>magic_etc</c> 的版本字段（<c>VFS_CAP_REVISION_MASK</c>）
    /// 以及此处可读取的三个版本。版本 2 与 3 对 permitted 与 inheritable 两组权限使用相同的两个 32 位半字；
    /// 版本 3 仅在其后追加命名空间 root id。</para>
    /// </summary>
    private const uint VfsCapabilityRevisionMask = 0xFF000000;
    private const uint VfsCapabilityRevision1 = 0x01000000;
    private const uint VfsCapabilityRevision2 = 0x02000000;
    private const uint VfsCapabilityRevision3 = 0x03000000;

    /// <summary>
    /// Byte sizes the kernel accepts for a <c>security.capability</c> record: 12 for revision 1, 20 for the plain
    /// revision 2 or 3 record, and 24 when the namespace root id is stored.
    /// <para>内核接受的 <c>security.capability</c> 记录字节长度：版本 1 为 12，常规版本 2 或 3 记录为 20，
    /// 存入命名空间 root id 时为 24。</para>
    /// </summary>
    private const int VfsCapabilityRevision1Size = 12;
    private const int VfsCapabilityRecordSize = 20;
    private const int VfsCapabilityNamespaceSize = 24;

    /// <summary>
    /// BOM-free UTF-8 for every flat export, matching <c>PortableExtractionPaths.NameEncoding</c>: the
    /// <see cref="Encoding.UTF8"/> singleton would prepend <c>EF BB BF</c> and break AOSP tools on line 1.
    /// <para>所有扁平导出使用的无 BOM UTF-8，与 <c>PortableExtractionPaths.NameEncoding</c> 一致：
    /// <see cref="Encoding.UTF8"/> 单例会在文件开头写入 <c>EF BB BF</c>，使 AOSP 工具在第一行即失败。</para>
    /// </summary>
    private static readonly UTF8Encoding MetadataEncoding = new(false, true);

    /// <summary>
    /// Generates all three metadata files for an EXT4 extraction.
    /// <para>为 EXT4 解包生成全部三个元数据文件。</para>
    /// </summary>
    /// <param name="metadataDirectory">Directory the files are written to; created when missing.
    /// <para>写入目标的目录；不存在时自动创建。</para></param>
    /// <param name="partitionName">Prefix applied to all three generated file names.
    /// <para>作为三个生成文件名前缀的分区名。</para></param>
    /// <param name="parser">Opened EXT4 parser supplying entries (root included), ownership, modes and xattrs.
    /// <para>已打开的 EXT4 解析器，提供条目（含根目录）、属主、模式与扩展属性。</para></param>
    /// <param name="superblockConfig">Optional JSON object whose members are written as
    /// <c>partition.key=value</c> lines; <see langword="null"/> emits only the default
    /// <c>partition_name</c> line.
    /// <para>可选 JSON 对象，其成员以 <c>分区.key=value</c> 行写入；为 <see langword="null"/> 时仅输出默认的
    /// <c>partition_name</c> 行。</para></param>
    /// <returns>One message per entry the flat grammar cannot carry (link or special node, whitespace in a
    /// path, unreadable capability xattr); empty when every entry was exported.
    /// <para>每条扁平语法无法承载的条目（链接或特殊节点、路径含空白、能力 xattr 不可解析）对应一条说明；
    /// 全部条目均已导出时为空。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="parser"/> is <see langword="null"/>.
    /// <para><paramref name="parser"/> 为 <see langword="null"/>。</para></exception>
    public static IReadOnlyList<string> GenerateForExt4(string metadataDirectory, string partitionName, Ext4Parser parser, JsonObject? superblockConfig = null)
    {
        ArgumentNullException.ThrowIfNull(parser);
        Directory.CreateDirectory(metadataDirectory);
        List<string> warnings = [];
        var entries = parser.ListEntriesWithRoot();
        WriteFsConfig(metadataDirectory, partitionName, entries.Select(e => (
            e.Path,
            e.Uid,
            e.Gid,
            e.Mode,
            CapabilityOf(e.Xattrs))), warnings);
        WriteFileContextsFromXattrs(metadataDirectory, partitionName, entries.Select(e => (
            e.Path,
            e.Xattrs)), warnings);
        WritePartitionInfo(metadataDirectory, partitionName, superblockConfig);
        return warnings;
    }

    /// <summary>
    /// Generates all three metadata files for an EROFS extraction.
    /// <para>为 EROFS 解包生成全部三个元数据文件。</para>
    /// </summary>
    /// <param name="metadataDirectory">Directory the files are written to; created when missing.
    /// <para>写入目标的目录；不存在时自动创建。</para></param>
    /// <param name="partitionName">Prefix applied to all three generated file names.
    /// <para>作为三个生成文件名前缀的分区名。</para></param>
    /// <param name="parser">Opened EROFS parser supplying the enumerated entries, ownership, modes and xattrs.
    /// <para>已打开的 EROFS 解析器，提供枚举出的条目、属主、模式与扩展属性。</para></param>
    /// <param name="superblockConfig">Optional JSON object whose members are written as
    /// <c>partition.key=value</c> lines; <see langword="null"/> emits only the default
    /// <c>partition_name</c> line.
    /// <para>可选 JSON 对象，其成员以 <c>分区.key=value</c> 行写入；为 <see langword="null"/> 时仅输出默认的
    /// <c>partition_name</c> 行。</para></param>
    /// <returns>One message per entry the flat grammar cannot carry; see
    /// <see cref="GenerateForExt4"/>. <para>扁平语法无法承载的条目各产生一条说明；见
    /// <see cref="GenerateForExt4"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="parser"/> is <see langword="null"/>.
    /// <para><paramref name="parser"/> 为 <see langword="null"/>。</para></exception>
    public static IReadOnlyList<string> GenerateForErofs(string metadataDirectory, string partitionName, ErofsParser parser, JsonObject? superblockConfig = null)
    {
        ArgumentNullException.ThrowIfNull(parser);
        Directory.CreateDirectory(metadataDirectory);
        List<string> warnings = [];
        var entries = parser.EnumerateEntries().ToList();
        WriteFsConfig(metadataDirectory, partitionName, entries.Select(e => (
            e.Path,
            e.Uid,
            e.Gid,
            e.Mode,
            e.Xattrs?.FirstOrDefault(x => x.Name == CapabilityXattrName)?.Value)), warnings);
        WriteFileContextsFromErofsXattrs(metadataDirectory, partitionName, entries, warnings);
        WritePartitionInfo(metadataDirectory, partitionName, superblockConfig);
        return warnings;
    }

    /// <summary>
    /// Generates all three metadata files for an F2FS extraction.
    /// <para>为 F2FS 解包生成全部三个元数据文件。</para>
    /// </summary>
    /// <param name="metadataDirectory">Directory the files are written to; created when missing.
    /// <para>写入目标的目录；不存在时自动创建。</para></param>
    /// <param name="partitionName">Prefix applied to all three generated file names.
    /// <para>作为三个生成文件名前缀的分区名。</para></param>
    /// <param name="parser">Opened F2FS parser supplying entries (root included), ownership, modes and xattrs.
    /// <para>已打开的 F2FS 解析器，提供条目（含根目录）、属主、模式与扩展属性。</para></param>
    /// <param name="superblockConfig">Optional JSON object whose members are written as
    /// <c>partition.key=value</c> lines; <see langword="null"/> emits only the default
    /// <c>partition_name</c> line.
    /// <para>可选 JSON 对象，其成员以 <c>分区.key=value</c> 行写入；为 <see langword="null"/> 时仅输出默认的
    /// <c>partition_name</c> 行。</para></param>
    /// <returns>One message per entry the flat grammar cannot carry; see
    /// <see cref="GenerateForExt4"/>. <para>扁平语法无法承载的条目各产生一条说明；见
    /// <see cref="GenerateForExt4"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="parser"/> is <see langword="null"/>.
    /// <para><paramref name="parser"/> 为 <see langword="null"/>。</para></exception>
    public static IReadOnlyList<string> GenerateForF2fs(string metadataDirectory, string partitionName, F2fsParser parser, JsonObject? superblockConfig = null)
    {
        ArgumentNullException.ThrowIfNull(parser);
        Directory.CreateDirectory(metadataDirectory);
        List<string> warnings = [];
        var entries = parser.ListEntriesWithRoot();
        WriteFsConfig(metadataDirectory, partitionName, entries.Select(e => (
            e.Path,
            e.Uid,
            e.Gid,
            e.Mode,
            CapabilityOf(e.Xattrs))), warnings);
        WriteFileContextsFromXattrs(metadataDirectory, partitionName, entries.Select(e => (e.Path, e.Xattrs)), warnings);
        WritePartitionInfo(metadataDirectory, partitionName, superblockConfig);
        return warnings;
    }

    /// <summary>
    /// Generates metadata files from the extraction directory on disk.
    /// <para>从磁盘上的解包目录生成元数据文件。</para>
    /// </summary>
    /// <param name="metadataDirectory">Directory the files are written to; created when missing.
    /// <para>写入目标的目录；不存在时自动创建。</para></param>
    /// <param name="partitionName">Prefix applied to all three generated file names.
    /// <para>作为三个生成文件名前缀的分区名。</para></param>
    /// <param name="extractionDirectory">Tree enumerated recursively; every path is exported with uid, gid and
    /// mode <c>0</c>, so the <c>fs_config</c> rows read <c>0 0 0000</c> — the mode is written verbatim instead
    /// of being replaced by invented defaults — and the default SELinux label is used. A missing directory
    /// yields only the root entry.
    /// <para>递归枚举的目录树；每个条目都以 uid、gid 与 mode <c>0</c> 导出，因此 <c>fs_config</c> 行写作
    /// <c>0 0 0000</c>——模式按原值写出，不会被替换成臆造的默认权限——并使用默认 SELinux 标签。
    /// 目录不存在时仅生成根条目。</para></param>
    /// <returns>One message per entry the flat grammar cannot carry; see
    /// <see cref="GenerateForExt4"/>. <para>扁平语法无法承载的条目各产生一条说明；见
    /// <see cref="GenerateForExt4"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="extractionDirectory"/> is <see langword="null"/>.
    /// <para><paramref name="extractionDirectory"/> 为 <see langword="null"/>。</para></exception>
    public static IReadOnlyList<string> GenerateFromDirectory(string metadataDirectory, string partitionName, string extractionDirectory)
    {
        ArgumentNullException.ThrowIfNull(extractionDirectory);
        Directory.CreateDirectory(metadataDirectory);
        List<string> warnings = [];
        List<string> paths = ["."];
        if (Directory.Exists(extractionDirectory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(extractionDirectory, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(extractionDirectory, entry).Replace('\\', '/');
                if (IsHostLink(Path.Combine(extractionDirectory, rel)))
                {
                    warnings.Add($"{rel} is a host link and has no file-type column in fs_config; skipped from both exports.");
                    continue;
                }

                paths.Add(rel);
            }
        }

        WriteFsConfig(metadataDirectory, partitionName, paths.Select(p => (
            p,
            (uint)0,
            (uint)0,
            (ushort)0,
            (byte[]?)null)), warnings);
        WriteFileContextsWithDefaults(metadataDirectory, partitionName, paths, warnings);
        WritePartitionInfo(metadataDirectory, partitionName, null);
        return warnings;
    }

    /// <summary>
    /// Returns the prefixed filename for a metadata file.
    /// <para>返回带分区名前缀的元数据文件名。</para>
    /// </summary>
    /// <param name="partitionName">Partition prefix placed before the underscore.
    /// <para>置于下划线左侧的分区名前缀。</para></param>
    /// <param name="suffix">Metadata kind, e.g. <c>fs_config</c>, <c>file_contexts</c> or <c>info</c>;
    /// an empty value yields the bare <c>partition_</c> prefix used for sidecar lookups.
    /// <para>元数据类别，如 <c>fs_config</c>、<c>file_contexts</c> 或 <c>info</c>；为空时返回用于附属文件
    /// 查找的 <c>分区_</c> 前缀。</para></param>
    /// <returns><c>{partitionName}_{suffix}</c>. <para>形如 <c>{分区名}_{后缀}</c> 的文件名。</para></returns>
    public static string GetMetadataFileName(string partitionName, string suffix)
        => $"{partitionName}_{suffix}";

    /// <summary>
    /// Detects the partition name from a source filename (strips extension).
    /// <para>从源文件名检测分区名（去除扩展名）。</para>
    /// </summary>
    /// <param name="sourceFileName">Path or file name of the source image.
    /// <para>源镜像的路径或文件名。</para></param>
    /// <returns>The file name without its extension, with a further trailing <c>.raw</c> stripped
    /// case-insensitively; <c>"unknown"</c> when nothing is left.
    /// <para>去除扩展名后的文件名，还会不区分大小写地剥离紧随其后的 <c>.raw</c>；若结果为空则返回
    /// <c>"unknown"</c>。</para></returns>
    public static string DetectPartitionName(string sourceFileName)
    {
        var name = Path.GetFileNameWithoutExtension(sourceFileName);
        if (name.EndsWith(".raw", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }

    /// <summary>
    /// Writes the <c>path uid gid mode capabilities</c> rows. uid and gid stay decimal because AOSP defines
    /// them that way; the mode always goes through <see cref="FsConfigModes"/> so the token is four octal
    /// digits, including the literal <c>0000</c> for an unknown mode, while the capabilities column stays
    /// <c>0</c> and any decoded capability is only reported. Rows use LF because AOSP readers treat a
    /// trailing CR as part of the last field.
    /// <para>写入 <c>path uid gid mode capabilities</c> 行。uid 与 gid 保持十进制，因为 AOSP 如此定义；
    /// 模式一律经 <see cref="FsConfigModes"/> 输出为四位八进制记号，未知模式也照原样写作 <c>0000</c>；
    /// capabilities 列保持 <c>0</c>，解码到的能力只以上报方式给出。行尾使用 LF，因为 AOSP 读取器会把结尾的
    /// CR 当作最后一个字段的一部分。</para>
    /// </summary>
    /// <param name="metadataDirectory">Directory the file is written to. <para>写入目标的目录。</para></param>
    /// <param name="partitionName">File-name prefix. <para>文件名前缀。</para></param>
    /// <param name="entries">Candidate rows in enumeration order. <para>按枚举顺序排列的候选行。</para></param>
    /// <param name="warnings">Sink for entries the grammar cannot express.
    /// <para>无法用该语法表达的条目写入此列表。</para></param>
    private static void WriteFsConfig(string metadataDirectory, string partitionName,
        IEnumerable<(string Path, uint Uid, uint Gid, ushort Mode, byte[]? Capability)> entries,
        List<string> warnings)
    {
        var fileName = GetMetadataFileName(partitionName, "fs_config");
        var path = Path.Combine(metadataDirectory, fileName);
        var sb = new StringBuilder();
        foreach (var (entryPath, uid, gid, mode, capability) in entries)
        {
            var normalized = NormalizePath(entryPath);
            var skipReason = GetFsConfigSkipReason(normalized, mode);
            if (skipReason is not null)
            {
                warnings.Add($"fs_config: {normalized}: {skipReason}.");
                continue;
            }

            sb.Append(normalized);
            sb.Append(' ');
            sb.Append(uid.ToString(CultureInfo.InvariantCulture));
            sb.Append(' ');
            sb.Append(gid.ToString(CultureInfo.InvariantCulture));
            sb.Append(' ');
            sb.Append(FsConfigModes.Format(mode));
            sb.Append(' ');
            sb.Append(FormatCapabilities(capability, normalized, warnings));
            sb.Append('\n');
        }

        WriteAtomic(path, sb.ToString());
    }

    /// <summary>
    /// Names the reason a row cannot be expressed in <c>fs_config</c>, or <see langword="null"/> when it can.
    /// The grammar has no type column, so only regular files and directories (and rows whose source carried no
    /// type bits at all) are writable; symlinks, FIFOs, sockets and device nodes would otherwise land as
    /// regular-file rows.
    /// <para>给出该行无法用 <c>fs_config</c> 表达的原因，可表达时为 <see langword="null"/>。该语法没有类型列，
    /// 因此只有常规文件与目录（以及源数据未携带类型位的行）可以写出；符号链接、FIFO、套接字与设备节点
    /// 若强行写出就会变成常规文件行。</para>
    /// </summary>
    /// <param name="imagePath">Normalized absolute image path. <para>已规整的绝对镜像路径。</para></param>
    /// <param name="mode">Raw inode mode including the file-type bits.
    /// <para>含文件类型位的原始 inode 模式。</para></param>
    /// <returns>The reason text, or <see langword="null"/>. <para>原因说明，或 <see langword="null"/>。</para></returns>
    private static string? GetFsConfigSkipReason(string imagePath, ushort mode)
    {
        if (imagePath.Any(char.IsWhiteSpace))
        {
            return "the space-delimited fs_config grammar defines no escaping for whitespace in a path";
        }

        var type = (ushort)(mode & InodeTypeBits.FormatMask);
        if (type is 0 or InodeTypeBits.RegularFile or InodeTypeBits.Directory)
        {
            return null;
        }

        return $"{DescribeInodeType(type)} has no file-type column in fs_config";
    }

    /// <summary>
    /// Human-readable name of a POSIX file-type bit pattern used in skip messages.
    /// <para>用于跳过说明的 POSIX 文件类型位名称。</para>
    /// </summary>
    /// <param name="type">A value already masked with <see cref="InodeTypeBits.FormatMask"/>.
    /// <para>已用 <see cref="InodeTypeBits.FormatMask"/> 掩码后的值。</para></param>
    /// <returns>The type name. <para>类型名称。</para></returns>
    private static string DescribeInodeType(ushort type)
        => type switch
        {
            InodeTypeBits.SymbolicLink => "symbolic link",
            InodeTypeBits.Fifo => "FIFO",
            InodeTypeBits.Socket => "socket",
            InodeTypeBits.CharDevice => "character device",
            InodeTypeBits.BlockDevice => "block device",
            _ => "inode type",
        };

    /// <summary>
    /// Renders the capabilities column as <c>0</c> for every entry, because the numeric dialect AOSP readers
    /// expect in that column is not asserted here: the decoded permitted set is reported as a warning instead of
    /// being written with an unverified base. An entry carrying no capability data needs no warning.
    /// <para>生成 capabilities 列：一律写作 <c>0</c>，因为本实现不断言 AOSP 读取器在该列期望的数值进制；
    /// 解码出的 permitted 集合改为以告警上报，而不是用未经验证的进制写出。条目本身无能力数据时不产生告警。</para>
    /// </summary>
    /// <param name="capability">Raw xattr value, or <see langword="null"/>.
    /// <para>xattr 原始值，或 <see langword="null"/>。</para></param>
    /// <param name="imagePath">Normalized absolute image path used in messages.
    /// <para>说明文本中使用的已规整绝对镜像路径。</para></param>
    /// <param name="warnings">Sink for capability data that is not exported.
    /// <para>未导出的能力数据写入此列表。</para></param>
    /// <returns>The literal <c>0</c> token. <para>固定为 <c>0</c> 记号。</para></returns>
    private static string FormatCapabilities(byte[]? capability, string imagePath, List<string> warnings)
    {
        if (capability is null)
        {
            return "0";
        }

        if (!TryReadCapabilityRecord(capability, out var permitted, out var inheritable, out var namespaceRootId))
        {
            warnings.Add($"fs_config: {imagePath}: the security.capability xattr is not a VFS capability record; the capabilities column is exported as 0.");
            return "0";
        }

        if (permitted != 0 || inheritable != 0 || namespaceRootId != 0)
        {
            warnings.Add($"fs_config: {imagePath}: security.capability permitted=0x{permitted:x16} inheritable=0x{inheritable:x16} rootid={namespaceRootId} is not exported; the capabilities column stays 0 because the expected numeric base is unverified.");
        }

        return "0";
    }

    /// <summary>
    /// Decodes the kernel <c>vfs_cap_data</c> layout: <c>magic_etc</c> at offset 0, then
    /// <c>data[0].permitted</c>, <c>data[0].inheritable</c>, <c>data[1].permitted</c>,
    /// <c>data[1].inheritable</c> as little-endian 32-bit words, and the revision 3 <c>rootid</c> at offset 20.
    /// Only the sizes the kernel accepts are read, so a truncated or padded record is reported instead of
    /// guessed.
    /// <para>解码内核 <c>vfs_cap_data</c> 布局：偏移 0 是 <c>magic_etc</c>，随后依次为
    /// <c>data[0].permitted</c>、<c>data[0].inheritable</c>、<c>data[1].permitted</c>、
    /// <c>data[1].inheritable</c>（小端 32 位字），版本 3 的 <c>rootid</c> 位于偏移 20。
    /// 只接受内核允许的长度，因此被截断或填充的记录会上报而不做猜测。</para>
    /// </summary>
    /// <param name="value">Raw xattr bytes. <para>xattr 原始字节。</para></param>
    /// <param name="permitted">Permitted set joined from both halves. <para>由两个半字拼合的 permitted 集。</para></param>
    /// <param name="inheritable">Inheritable set joined from both halves.
    /// <para>由两个半字拼合的 inheritable 集。</para></param>
    /// <param name="namespaceRootId">Revision 3 root id, or <c>0</c>. <para>版本 3 的 root id，或 <c>0</c>。</para></param>
    /// <returns><see langword="true"/> when the record has a known revision and size.
    /// <para>记录长度与版本均为已知值时返回 <see langword="true"/>。</para></returns>
    private static bool TryReadCapabilityRecord(byte[] value, out ulong permitted, out ulong inheritable, out uint namespaceRootId)
    {
        permitted = 0;
        inheritable = 0;
        namespaceRootId = 0;
        if (value.Length is not (VfsCapabilityRevision1Size or VfsCapabilityRecordSize or VfsCapabilityNamespaceSize))
        {
            return false;
        }

        var revision = BinaryPrimitives.ReadUInt32LittleEndian(value) & VfsCapabilityRevisionMask;
        if (revision is not (VfsCapabilityRevision1 or VfsCapabilityRevision2 or VfsCapabilityRevision3))
        {
            return false;
        }

        var isRevision1 = revision == VfsCapabilityRevision1;
        var hasUpperSet = value.Length > VfsCapabilityRevision1Size;
        if (isRevision1 == hasUpperSet)
        {
            return false;
        }

        var lowPermitted = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(4, 4));
        var lowInheritable = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(8, 4));
        var highPermitted = 0u;
        var highInheritable = 0u;
        if (hasUpperSet)
        {
            highPermitted = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(12, 4));
            highInheritable = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(16, 4));
        }

        if (value.Length > VfsCapabilityRecordSize)
        {
            namespaceRootId = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(20, 4));
        }

        permitted = ((ulong)highPermitted << 32) | lowPermitted;
        inheritable = ((ulong)highInheritable << 32) | lowInheritable;
        return true;
    }

    private static void WriteFileContextsFromXattrs(string metadataDirectory, string partitionName,
        IEnumerable<(string Path, IReadOnlyDictionary<string, byte[]>? Xattrs)> entries, List<string> warnings)
    {
        var fileName = GetMetadataFileName(partitionName, "file_contexts");
        var path = Path.Combine(metadataDirectory, fileName);
        var sb = new StringBuilder();
        foreach (var (entryPath, xattrs) in entries)
        {
            var normalized = NormalizePath(entryPath);
            string? label = null;
            if (xattrs is not null && xattrs.TryGetValue(SelinuxXattrName, out var selinuxValue))
            {
                var decoded = Encoding.UTF8.GetStringTrimNul(selinuxValue);
                if (!string.IsNullOrWhiteSpace(decoded))
                {
                    label = decoded;
                }
            }

            if (label is not null)
            {
                WriteFileContextsRow(sb, normalized, label, warnings);
            }
        }

        WriteAtomic(path, sb.ToString());
    }

    private static void WriteFileContextsFromErofsXattrs(string metadataDirectory, string partitionName,
        IReadOnlyList<ErofsFileEntry> entries, List<string> warnings)
    {
        var fileName = GetMetadataFileName(partitionName, "file_contexts");
        var path = Path.Combine(metadataDirectory, fileName);
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            var normalized = NormalizePath(entry.Path);
            string? label = null;
            if (entry.Xattrs is not null)
            {
                var selinux = entry.Xattrs.FirstOrDefault(x => x.Name == SelinuxXattrName);
                if (selinux?.Name is not null)
                {
                    var decoded = Encoding.UTF8.GetStringTrimNul(selinux.Value);
                    if (!string.IsNullOrWhiteSpace(decoded))
                    {
                        label = decoded;
                    }
                }
            }

            if (label is not null)
            {
                WriteFileContextsRow(sb, normalized, label, warnings);
            }
        }

        WriteAtomic(path, sb.ToString());
    }

    private static void WriteFileContextsWithDefaults(string metadataDirectory, string partitionName,
        IEnumerable<string> paths, List<string> warnings)
    {
        var fileName = GetMetadataFileName(partitionName, "file_contexts");
        var path = Path.Combine(metadataDirectory, fileName);
        var sb = new StringBuilder();
        foreach (var entryPath in paths)
        {
            WriteFileContextsRow(sb, NormalizePath(entryPath), DefaultSelinuxLabel, warnings);
        }

        WriteAtomic(path, sb.ToString());
    }

    /// <summary>
    /// Writes one <c>path label</c> row separated by a single space, the spelling AOSP's file_contexts loader
    /// splits on; the reader takes the last whitespace-delimited token, so a path or a label containing
    /// whitespace is skipped rather than exported as two rows' worth of text.
    /// <para>写入一行 <c>path label</c>，以单个空格分隔，即 AOSP file_contexts 加载器切分所用的拼写；
    /// 读取器取最后一个空白分隔记号，因此路径或标签含空白时跳过，而不是导出会被拆成两行的文本。</para>
    /// </summary>
    /// <param name="target">Buffer collecting the rows. <para>收集各行的缓冲区。</para></param>
    /// <param name="imagePath">Normalized absolute image path. <para>已规整的绝对镜像路径。</para></param>
    /// <param name="label">SELinux context. <para>SELinux 上下文。</para></param>
    /// <param name="warnings">Sink for rows the grammar cannot express.
    /// <para>无法用该语法表达的行写入此列表。</para></param>
    private static void WriteFileContextsRow(StringBuilder target, string imagePath, string label, List<string> warnings)
    {
        if (imagePath.Any(char.IsWhiteSpace) || label.Any(char.IsWhiteSpace))
        {
            warnings.Add($"file_contexts: {imagePath}: the whitespace-delimited file_contexts grammar defines no escaping for whitespace in a path or label; skipped.");
            return;
        }

        target.Append(imagePath).Append(' ').Append(label).Append('\n');
    }

    private static void WritePartitionInfo(string metadataDirectory, string partitionName, JsonObject? superblockConfig)
    {
        var fileName = GetMetadataFileName(partitionName, "info");
        var path = Path.Combine(metadataDirectory, fileName);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Partition: {partitionName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Generated by UotanStudio");
        sb.AppendLine();
        if (superblockConfig is not null)
        {
            foreach (var (key, value) in superblockConfig)
            {
                var strValue = value?.ToString() ?? string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture, $"{partitionName}.{key}={strValue}");
            }
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{partitionName}.partition_name={partitionName}");
        }

        WriteAtomic(path, sb.ToString());
    }

    private static byte[]? CapabilityOf(IReadOnlyDictionary<string, byte[]>? xattrs)
        => xattrs is not null && xattrs.TryGetValue(CapabilityXattrName, out var value) ? value : null;

    /// <summary>
    /// Reports whether a host path is a link rather than a plain file or directory. A stat that fails is read as
    /// a link because a dangling symbolic link cannot be statted through its target on Windows, and an entry the
    /// export cannot describe is safer skipped than written as a regular-file row.
    /// <para>判断宿主机路径是链接还是普通文件或目录。stat 调用失败时按链接处理，因为 Windows 上无法对指向
    /// 不存在目标的符号链接按其目标执行 stat，而无法描述的条目跳过导出也比写成常规文件行更安全。</para>
    /// </summary>
    /// <param name="hostPath">Absolute host path inside the extraction tree.
    /// <para>解包树内的绝对宿主机路径。</para></param>
    /// <returns><see langword="true"/> when the entry is a link or cannot be identified.
    /// <para>条目为链接或无法识别时返回 <see langword="true"/>。</para></returns>
    private static bool IsHostLink(string hostPath)
    {
        try
        {
            return (File.GetAttributes(hostPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static string NormalizePath(string path)
    {
        if (path is "." or "")
        {
            return "/";
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return "/" + normalized;
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = TempFileHelper.CreateAdjacentPath(path);
        try
        {
            File.WriteAllText(temporary, content, MetadataEncoding);
            AtomicPublish.Publish(temporary, path);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch { }
        }
    }
}