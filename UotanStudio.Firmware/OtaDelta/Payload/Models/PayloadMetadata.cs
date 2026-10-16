using UotanStudio.Firmware.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.OtaDelta.Payload.Models;

/// <summary>
/// Snapshot of a parsed OTA payload: header fields plus the decoded install manifest.
/// <para>已解析 OTA payload 的快照：头部字段与解码后的安装 manifest。</para>
/// </summary>
public class PayloadMetadata
{
    /// <summary>
    /// Block size in bytes used by payload install operations.
    /// <para>payload 安装操作使用的块大小（字节）。</para>
    /// </summary>
    /// <value>
    /// Taken from the manifest; partition sizes and operation extents are counted in multiples of this value.
    /// <para>取自 manifest；分区大小与操作范围均按该值的倍数计量。</para>
    /// </value>
    public uint BlockSize { get; internal set; }

    /// <summary>
    /// Partition names collected from the manifest entries for lookup and progress reporting.
    /// <para>从 manifest 条目收集的分区名称，用于查找与进度上报。</para>
    /// </summary>
    /// <value>
    /// Mutable list in manifest declaration order; empty before parsing.
    /// <para>按 manifest 声明顺序排列的可变列表；解析前为空。</para>
    /// </value>
    public List<string> PartitionNames { get; } = [];

    /// <summary>
    /// Byte length of the serialized protobuf manifest recorded in the payload header.
    /// <para>payload 头部记录的序列化 protobuf manifest 字节长度。</para>
    /// </summary>
    /// <value>
    /// Used to size the manifest region while parsing; a mismatch with the bytes actually read raises
    /// <see cref="InvalidManifestException"/>.
    /// <para>解析时用于确定 manifest 区域大小；与实际读取的字节数不一致时抛出
    /// <see cref="InvalidManifestException"/>。</para>
    /// </value>
    public ulong ManifestLength { get; internal set; }

    /// <summary>
    /// Byte length of the metadata signature block recorded in the payload header.
    /// <para>payload 头部记录的元数据签名块字节长度。</para>
    /// </summary>
    /// <value>
    /// After the manifest is read the parser skips or reads a signature block of exactly this size;
    /// <c>0</c> means no metadata signature bytes.
    /// <para>读取 manifest 后，解析器跳过或读取恰好该大小的签名块；<c>0</c> 表示没有元数据签名字节。</para>
    /// </value>
    public uint MetadataSignatureLength { get; internal set; }

    /// <summary>
    /// Base file offset from which operation data extents in the payload are measured.
    /// <para>payload 中操作数据范围的基准文件偏移。</para>
    /// </summary>
    /// <value>
    /// Read from the payload header and passed to the extractor as the origin for extent offsets.
    /// <para>读自 payload 头部，并作为范围偏移的起点传给提取器。</para>
    /// </value>
    public ulong BaseOffset { get; internal set; }

    /// <summary>
    /// Decoded protobuf manifest describing partitions, groups and install operations.
    /// <para>描述分区、分区组与安装操作的解码后 protobuf manifest。</para>
    /// </summary>
    /// <value>
    /// The manifest copied from the parse result; a default empty manifest before parsing.
    /// <para>解析结果复制而来的 manifest；解析前为默认的空 manifest。</para>
    /// </value>
    public DeltaArchiveManifest Manifest { get; internal set; } = new();

    /// <summary>
    /// Maximum timestamp recorded in the manifest; the payload format expresses it in milliseconds since the Unix epoch.
    /// <para>manifest 记录的最大时间戳；payload 格式以 Unix 纪元毫秒表示。</para>
    /// </summary>
    /// <value>
    /// Copied verbatim from the manifest during parsing; <c>0</c> when the manifest declares none.
    /// <para>解析时原样复制自 manifest；manifest 未声明时为 <c>0</c>。</para>
    /// </value>
    public long MaxTimestamp { get; internal set; }

    /// <summary>
    /// Dynamic partition metadata carried by the manifest.
    /// <para>manifest 携带的动态分区元数据。</para>
    /// </summary>
    /// <value>
    /// The manifest value when present, otherwise a new empty instance; set during parsing.
    /// <para>manifest 提供时取其值，否则为新的空实例；在解析时设置。</para>
    /// </value>
    public DynamicPartitionMetadata DynamicPartitionMetadata { get; internal set; } = new();

    /// <summary>
    /// Offset of the payload signatures section recorded in the manifest.
    /// <para>manifest 记录的 payload 签名区偏移。</para>
    /// </summary>
    /// <value>
    /// Copied from the manifest; <c>0</c> when the manifest declares no signatures section.
    /// <para>取自 manifest；manifest 未声明签名区时为 <c>0</c>。</para>
    /// </value>
    public ulong SignaturesOffset { get; internal set; }

    /// <summary>
    /// Byte size of the payload signatures section recorded in the manifest.
    /// <para>manifest 记录的 payload 签名区字节大小。</para>
    /// </summary>
    /// <value>
    /// Copied from the manifest; <c>0</c> when the manifest declares no signatures section.
    /// <para>取自 manifest；manifest 未声明签名区时为 <c>0</c>。</para>
    /// </value>
    public ulong SignaturesSize { get; internal set; }
}

/// <summary>
/// Partition name and size summary prepared for extraction progress reporting.
/// <para>为提取进度上报准备的分区名称与大小摘要。</para>
/// </summary>
public class PartitionInfoData
{
    /// <summary>
    /// Partition name as declared in the manifest.
    /// <para>manifest 中声明的分区名称。</para>
    /// </summary>
    /// <value>
    /// Non-empty; entries without a name in the manifest are not reported.
    /// <para>非空；manifest 中没有名称的条目不会被报告。</para>
    /// </value>
    public string Name { get; internal set; } = string.Empty;

    /// <summary>
    /// Progress size of the partition in bytes.
    /// <para>分区进度大小（字节）。</para>
    /// </summary>
    /// <value>
    /// Derived from the partition's declared extent by the payload block size and clamped to <c>0</c>
    /// when the computed value would be negative.
    /// <para>由分区的声明范围按 payload 块大小换算得出，计算值为负时钳制为 <c>0</c>。</para>
    /// </value>
    public ulong SizeBytes { get; internal set; }

    /// <summary>
    /// Human-readable rendering of <see cref="SizeBytes"/> for display.
    /// <para>供显示的 <see cref="SizeBytes"/> 可读格式化结果。</para>
    /// </summary>
    /// <value>
    /// One decimal place with a unit suffix: <c>GB</c> at 1 GiB and above, <c>MB</c> at 1 MiB and above,
    /// otherwise <c>KB</c>, e.g. <c>"2.0GB"</c>.
    /// <para>保留一位小数并带单位后缀：不小于 1 GiB 显示 <c>GB</c>，不小于 1 MiB 显示 <c>MB</c>，
    /// 否则显示 <c>KB</c>，例如 <c>"2.0GB"</c>。</para>
    /// </value>
    public string SizeReadable { get; internal set; } = string.Empty;
}

internal static class PartitionInfoHelper
{
    /// <summary>
    /// Computes partition size information for all named partitions in the manifest.
    /// <para>计算 manifest 中所有已命名分区的分区大小信息。</para>
    /// </summary>
    internal static List<PartitionInfoData> ComputePartitionInfo(DeltaArchiveManifest manifest)
    {
        var list = new List<PartitionInfoData>();

        foreach (var part in manifest.Partitions)
        {
            if (string.IsNullOrEmpty(part.PartitionName))
            {
                continue;
            }

            var bytes = (ulong)Math.Max(0, PayloadExtractor.ProgressSize(part, manifest.BlockSize));

            list.Add(new PartitionInfoData
            {
                Name = part.PartitionName,
                SizeBytes = bytes,
                SizeReadable = FormatSize(bytes)
            });
        }

        return list;
    }

    /// <summary>
    /// Formats a byte count into a human-readable size string (e.g., "2.0GB", "512.0MB").
    /// <para>将字节数格式化为可读的大小字符串（例如 "2.0GB"、"512.0MB"）。</para>
    /// </summary>
    internal static string FormatSize(ulong bytes)
    {
        return bytes >= 1024UL * 1024 * 1024
            ? $"{bytes / (1024.0 * 1024 * 1024):F1}GB"
            : bytes >= 1024UL * 1024 ? $"{bytes / (1024.0 * 1024):F1}MB" : $"{bytes / 1024.0:F1}KB";
    }
}