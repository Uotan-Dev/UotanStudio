using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Programmer;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// The verdict of one programmer-directory run: a summary line, plus the descriptor and
/// upload-eligible flag of a unique match. No other decision carries a descriptor.
/// <para>一次 programmer 目录判定的结论：一行摘要文本，加上唯一匹配时的描述符与可上传标记。
/// 其余结论都不携带描述符。</para>
/// </summary>
internal sealed record EdlProgrammerDirectoryResult
{
    /// <summary>
    /// The matching decision, using the same values as <see cref="EdlProgrammerMatchDecision"/>.
    /// <para>匹配结论，取值与 <see cref="EdlProgrammerMatchDecision"/> 一致。</para>
    /// </summary>
    /// <value>The matching decision value. <para>匹配结论的取值。</para></value>
    internal required EdlProgrammerMatchDecision Decision { get; init; }

    /// <summary>
    /// The descriptor of a unique match; <see langword="null"/> for every other decision.
    /// <para>唯一匹配时的描述符；其余结论为 <see langword="null"/>。</para>
    /// </summary>
    /// <value>The descriptor of a unique match, absent for every other decision. <para>唯一匹配的描述符；其余结论不携带。</para></value>
    internal EdlProgrammerDescriptor? Descriptor { get; init; }

    /// <summary>
    /// Whether this verdict is already upload-eligible; only a unique match is <see langword="true"/>.
    /// <para>该结论是否已具备上传条件；只有唯一匹配为 <see langword="true"/>。</para>
    /// </summary>
    /// <value>True when this verdict is upload-eligible. <para>结论具备上传资格时为 true。</para></value>
    internal bool IsUploadEligible { get; init; }

    /// <summary>
    /// The number of admitted rows the scan produced, which shows whether the folder had content at all.
    /// <para>扫描得到的入册条目数，用于说明目录本身是否有内容。</para>
    /// </summary>
    /// <value>The number of catalog rows admitted by the scan. <para>扫描入册的目录条目数。</para></value>
    internal int CandidateCount { get; init; }

    /// <summary>
    /// The number of files the scan refused; skipped entries never enter the catalog and never take part in matching.
    /// <para>扫描拒绝的文件数；被跳过的条目不会进入目录，也不参与匹配。</para>
    /// </summary>
    /// <value>The number of files the scan refused. <para>扫描拒绝的文件数。</para></value>
    internal int SkippedCount { get; init; }

    /// <summary>
    /// The per-candidate reasons recorded when no entry carried positive matching evidence.
    /// <para>目录中所有条目都无正匹配证据时记录的逐候选原因。</para>
    /// </summary>
    /// <value>The reason list for each scanned candidate. <para>每个扫描候选的原因列表。</para></value>
    internal IReadOnlyList<EdlProgrammerCandidateReason> CandidateReasons { get; init; } = [];
}

/// <summary>
/// Scans a user-selected programmer folder into catalog rows and matches the identity the target
/// already reported against them; only a unique match is eligible for a later upload.
/// <para>扫描用户选定的 programmer 目录得到目录条目，再用目标已上报的身份做正匹配；
/// 只有唯一匹配才允许后续上传。</para>
/// </summary>
internal static class EdlProgrammerDirectoryService
{
    /// <summary>
    /// Scans the user-selected programmer folder and reports its catalog row statistics; it never
    /// touches a device and performs no matching.
    /// <para>扫描用户选定的 programmer 目录并返回其目录条目统计；不接触设备，也不做任何匹配。</para>
    /// </summary>
    /// <param name="directoryPath">The user-selected programmer folder. <para>用户选定的 programmer 目录。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The admitted-row and skipped-file statistics. <para>入册条目与被跳过文件的统计。</para></returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="directoryPath"/> is null or blank. <para><paramref name="directoryPath"/> 为 null 或仅空白。</para></exception>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist; thrown before any file is opened. <para>目录不存在；该异常在任何文件被打开之前抛出。</para></exception>
    internal static EdlProgrammerDirectoryResult ScanAsync(string directoryPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var scan = EdlProgrammerFolderScanner.Scan(directoryPath, ct);
        var catalog = EdlProgrammerCatalog.FromDescriptors(scan.Descriptors);
        return new EdlProgrammerDirectoryResult
        {
            Decision = EdlProgrammerMatchDecision.NeedsExplicitSelection,
            CandidateCount = catalog.Descriptors.Count,
            SkippedCount = scan.Skipped.Count
        };
    }

    /// <summary>
    /// Positively matches the selected folder against the Sahara identity the target reported; only
    /// a unique match returns a descriptor.
    /// <para>按目标已上报的 Sahara 身份在选定目录中做正匹配；仅唯一匹配返回描述符。</para>
    /// </summary>
    /// <param name="device">The target EDL endpoint. <para>目标 EDL 端点。</para></param>
    /// <param name="directoryPath">The user-selected programmer folder. <para>用户选定的 programmer 目录。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The matching verdict with per-candidate reasons; a non-unique match carries no descriptor. <para>匹配结论，含逐候选原因；非唯一匹配不携带描述符。</para></returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="device"/> is <see langword="null"/>. <para><paramref name="device"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="directoryPath"/> is null or blank. <para><paramref name="directoryPath"/> 为 null 或仅空白。</para></exception>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist; thrown before any file is opened. <para>目录不存在；该异常在任何文件被打开之前抛出。</para></exception>
    internal static EdlProgrammerDirectoryResult MatchAsync(DetectedDevice device, string directoryPath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        var identity = App.DeviceMonitor.GetEdlDetails(device)?.Identity;
        return MatchCore(directoryPath, identity, ct);
    }

    /// <summary>
    /// The matching core that touches no device monitor: the folder scan and the identity match
    /// need nothing but an already reported identity.
    /// <para>不接触设备监听的匹配核心：目录扫描与身份匹配只需要一个已上报的身份。</para>
    /// </summary>
    /// <param name="directoryPath">The user-selected programmer folder. <para>用户选定的 programmer 目录。</para></param>
    /// <param name="identity">The Sahara identity the target reported, or <see langword="null"/> when it reported none. <para>目标已上报的 Sahara 身份，未上报时为 <see langword="null"/>。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The matching verdict with per-candidate reasons; a non-unique match carries no descriptor. <para>匹配结论，含逐候选原因；非唯一匹配不携带描述符。</para></returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="directoryPath"/> is null or blank. <para><paramref name="directoryPath"/> 为 null 或仅空白。</para></exception>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist; thrown before any file is opened. <para>目录不存在；该异常在任何文件被打开之前抛出。</para></exception>
    internal static EdlProgrammerDirectoryResult MatchCore(string directoryPath, SaharaDeviceIdentity? identity,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var scan = EdlProgrammerFolderScanner.Scan(directoryPath, ct);
        var catalog = EdlProgrammerCatalog.FromDescriptors(scan.Descriptors);
        var selection = EdlRuleProgrammerSelector.Instance.Select(identity, catalog, ct);
        return new EdlProgrammerDirectoryResult
        {
            Decision = selection.Decision,
            Descriptor = selection.IsUploadEligible ? selection.Descriptor : null,
            IsUploadEligible = selection.IsUploadEligible,
            CandidateCount = catalog.Descriptors.Count,
            SkippedCount = scan.Skipped.Count,
            CandidateReasons = selection.PerCandidateReasons
        };
    }
}