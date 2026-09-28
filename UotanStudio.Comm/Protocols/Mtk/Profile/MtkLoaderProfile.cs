using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Mtk.Discovery;
using UotanStudio.Comm.Protocols.Mtk.Upload;
using UotanStudio.Firmware.VendorFirmware.MediaTek;

namespace UotanStudio.Comm.Protocols.Mtk.Profile;

/// <summary>
/// A parsed, hashed all-in-one MTK download-agent container offering hardware-identity-based entry
/// selection. The profile evaluates only structural metadata and the queried device identity: it
/// never matches on DA file names or model strings, and a parseable container is never treated as
/// proof of device compatibility.
/// <para>已解析并完成哈希的 all-in-one MTK 下载代理容器，提供基于硬件身份的条目选择。配置仅评估结构元数据
/// 与已查询的设备身份：绝不按 DA 文件名或机型字符串匹配；容器可解析也绝不视为设备兼容的证明。</para>
/// </summary>
public sealed class MtkLoaderProfile
{
    private MtkLoaderProfile(string filePath, string sha256Hex, MtkDaContainer container,
        IReadOnlyList<MtkDaDescriptor> entries)
    {
        FilePath = filePath;
        Sha256Hex = sha256Hex;
        Container = container;
        Entries = entries;
    }

    /// <summary>
    /// Gets the loader path the profile was parsed from.
    /// <para>获取解析本配置所使用的加载器路径。</para>
    /// </summary>
    /// <value>
    /// The absolute or caller-provided path recorded at parse time.
    /// <para>解析时记录的绝对路径或调用方提供的路径。</para>
    /// </value>
    public string FilePath { get; }

    /// <summary>
    /// Gets the SHA-256 hex digest of the complete container bytes.
    /// <para>获取完整容器字节的 SHA-256 十六进制摘要。</para>
    /// </summary>
    /// <value>
    /// The lowercase-free hex digest string produced by the hasher.
    /// <para>由哈希计算生成的十六进制摘要字符串。</para>
    /// </value>
    public string Sha256Hex { get; }

    /// <summary>
    /// Gets the validated container metadata from the Firmware reader.
    /// <para>获取来自 Firmware 读取器的已校验容器元数据。</para>
    /// </summary>
    /// <value>
    /// The parsed directory. <para>已解析的目录。</para>
    /// </value>
    public MtkDaContainer Container { get; }

    /// <summary>
    /// Gets the promoted, index-ordered entry descriptors.
    /// <para>获取按索引顺序提升后的条目描述符。</para>
    /// </summary>
    /// <value>
    /// One descriptor per validated directory entry.
    /// <para>每个已校验目录条目对应一个描述符。</para>
    /// </value>
    public IReadOnlyList<MtkDaDescriptor> Entries { get; }

    /// <summary>
    /// Parses the container metadata and hashes the complete file without holding the whole image
    /// in memory; no device is contacted and nothing is uploaded by this call.
    /// <para>解析容器元数据并对完整文件做流式哈希，不会将整个镜像驻留内存；本调用不会接触设备，也不会上传任何内容。</para>
    /// </summary>
    /// <param name="path">
    /// The explicitly selected loader path. <para>显式选择的加载器路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The operation cancellation token. <para>操作取消令牌。</para>
    /// </param>
    /// <returns>
    /// The parsed profile bound to the resource hash.
    /// <para>绑定到资源哈希的已解析配置。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty or whitespace. <para><paramref name="path"/> 为空或仅包含空白字符。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// No file exists at <paramref name="path"/>. <para><paramref name="path"/> 指向的文件不存在。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The container header or directory fails structural validation. <para>容器头部或目录未通过结构校验。</para>
    /// </exception>
    public static async Task<MtkLoaderProfile> FromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using (var hashSource = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                         4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var digest = await SHA256.HashDataAsync(hashSource, cancellationToken).ConfigureAwait(false);
            var sha256Hex = Convert.ToHexString(digest);
            using var loader = MtkDaFile.Open(path, MtkDaDirectoryLayout.Auto, cancellationToken);
            var kind = loader.Metadata.HeaderIndicatesV6 ? MtkDaKind.V6 : MtkDaKind.V5;
            var entries = new List<MtkDaDescriptor>(loader.Metadata.Entries.Count);
            for (var index = 0; index < loader.Metadata.Entries.Count; index++)
            {
                var entry = loader.Metadata.Entries[index];
                entries.Add(new MtkDaDescriptor(index, entry.HardwareCode, entry.HardwareSubCode,
                    entry.HardwareVersion, entry.SoftwareVersion, entry.PageSize, kind, entry.Regions));
            }

            return new MtkLoaderProfile(path, sha256Hex, loader.Metadata, entries);
        }
    }

    /// <summary>
    /// Evaluates every structurally valid entry against the queried device identity, applying the
    /// exact or minimum-revision policy. This is a pure decision made before any device I/O: a
    /// non-<see cref="MtkProfileDecision.SingleMatch"/> outcome must stop the upload.
    /// <para>依据已查询的设备身份，按精确或最低版本策略评估每个结构有效的条目。这是在任何设备 I/O 之前做出的
    /// 纯决策：非 <see cref="MtkProfileDecision.SingleMatch"/> 的结果必须阻止上传。</para>
    /// </summary>
    /// <param name="device">
    /// The identity returned by the boot handshake. <para>由 boot 握手返回的身份信息。</para>
    /// </param>
    /// <param name="match">
    /// The revision comparison policy. <para>版本比较策略。</para>
    /// </param>
    /// <returns>
    /// The explained selection carrying the container hash.
    /// <para>携带容器哈希的、带解释的选择结果。</para>
    /// </returns>
    public MtkProfileSelection SelectEntry(MtkDeviceInfo device,
        MtkDaIdentityMatch match = MtkDaIdentityMatch.Exact)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!Enum.IsDefined(match))
        {
            throw new ArgumentOutOfRangeException(nameof(match));
        }

        var reasons = new List<string>();
        var matched = new List<MtkDaDescriptor>();
        var structuralDefects = 0;
        foreach (var entry in Entries)
        {
            if (entry.HardwareCode == 0 || entry.SoftwareVersion is null)
            {
                structuralDefects++;
                reasons.Add($"Entry {entry.EntryIndex} lacks an explicit hardware/software identity.");
                continue;
            }

            if (entry.UsableRegionPair is not { } pair)
            {
                structuralDefects++;
                reasons.Add($"Entry {entry.EntryIndex} has no complete, bounded DA1/DA2 region pair.");
                continue;
            }

            if (entry.HardwareCode != device.HardwareCode || entry.HardwareSubCode != device.HardwareSubCode)
            {
                reasons.Add($"Entry {entry.EntryIndex} declares hardware 0x{entry.HardwareCode:X4}/0x{entry.HardwareSubCode:X4}, "
                    + $"not the queried 0x{device.HardwareCode:X4}/0x{device.HardwareSubCode:X4}.");
                continue;
            }

            var revisionsMatch = match == MtkDaIdentityMatch.Exact
                ? entry.HardwareVersion == device.HardwareVersion && entry.SoftwareVersion == device.SoftwareVersion
                : entry.HardwareVersion <= device.HardwareVersion && entry.SoftwareVersion <= device.SoftwareVersion;
            if (!revisionsMatch)
            {
                reasons.Add($"Entry {entry.EntryIndex} revisions {entry.HardwareVersion:X4}/{entry.SoftwareVersion:X4} "
                    + $"fail the {match} policy for device {device.HardwareVersion:X4}/{device.SoftwareVersion:X4}.");
                continue;
            }

            matched.Add(entry with { Regions = entry.Regions });
            reasons.Add($"Entry {entry.EntryIndex} matches hardware, subcode, and the {match} revision policy.");
        }

        var (stageOne, stageTwo) = matched.Count == 1 ? matched[0].UsableRegionPair!.Value : (-1, -1);
        return new MtkProfileSelection
        {
            Decision = matched.Count switch
            {
                1 => MtkProfileDecision.SingleMatch,
                > 1 => MtkProfileDecision.Ambiguous,
                _ => structuralDefects == Entries.Count
                    ? MtkProfileDecision.NeedsExplicitSelection
                    : MtkProfileDecision.NoMatch
            },
            Descriptor = matched.Count == 1 ? matched[0] : null,
            EntryIndex = matched.Count == 1 ? matched[0].EntryIndex : null,
            StageOneRegionIndex = matched.Count == 1 ? stageOne : null,
            StageTwoRegionIndex = matched.Count == 1 ? stageTwo : null,
            Reasons = reasons,
            Sha256Hex = Sha256Hex
        };
    }

    /// <summary>
    /// Builds the upload-stage evidence that a profile decision has gated a bring-up attempt; used
    /// by session bootstrap paths before any agent byte is sent.
    /// <para>构建表明配置决策已对启动尝试进行门控的上传阶段证据；供会话启动路径在发送任何代理字节之前使用。</para>
    /// </summary>
    /// <param name="selection">
    /// The evaluated selection. <para>已评估的选择结果。</para>
    /// </param>
    /// <param name="outcome">
    /// The observed consequence for the run. <para>本次运行观察到的后果。</para>
    /// </param>
    /// <returns>
    /// A <see cref="Common.Evidence.ProtocolEvidence"/> in phase <c>da-profile</c>.
    /// <para>处于 <c>da-profile</c> 阶段的 <see cref="Common.Evidence.ProtocolEvidence"/>。</para>
    /// </returns>
    public Common.Evidence.ProtocolEvidence BuildGateEvidence(MtkProfileSelection selection,
        Common.Evidence.CommandOutcomeKind outcome)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection.ToEvidence() with { Outcome = outcome };
    }
}