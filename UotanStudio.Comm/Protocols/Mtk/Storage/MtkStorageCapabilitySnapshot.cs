using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Mtk.Storage;

/// <summary>
/// The single immutable storage-capability contract every MTK discovery path publishes.
/// <para>每条 MTK 发现路径发布的唯一不可变存储能力契约。</para>
/// The XFlash, XML DA and Legacy wire models remain available as side evidence so no protocol
/// specific field is lost; this snapshot normalizes their region identity, capacities, RPMB
/// posture, NAND page/block/OOB/ECC semantics and NOR alignment into one type, records the
/// current storage generation in its evidence, and fails closed: regions or capabilities the
/// device never reported are absent or <see cref="FeatureSupport.Unknown"/> and are never
/// writable. Replay-protected, boot and authenticated regions are never ordinary storage.
/// <para>XFlash、XML DA 与 Legacy 线协议模型仍作为旁证可用，协议特有字段不丢失；本快照将它们的区域身份、
/// 容量、RPMB 姿态、NAND 页/块/OOB/ECC 语义与 NOR 对齐归一为同一类型，在证据中记录当前存储代际，并失败关闭：
/// 设备从未上报的区域或能力不会出现在快照中，或保持 <see cref="FeatureSupport.Unknown"/>，且绝不可写。
/// 重放保护、引导与需认证区域绝不是普通存储。</para>
/// </summary>
public sealed record MtkStorageCapabilitySnapshot : CapabilitySnapshot
{
    /// <summary>
    /// Gets the storage family the reporting agent described.
    /// <para>获取上报代理所描述的存储家族。</para>
    /// </summary>
    public required MtkXFlashStorageKind Kind { get; init; }

    /// <summary>
    /// Gets the wire path that produced this observation.
    /// <para>获取产生本观测的线协议路径。</para>
    /// </summary>
    public required MtkStorageDiscoverySource Source { get; init; }

    /// <summary>
    /// Gets the reported logical block size shared by the ordinary regions.
    /// <para>获取普通区域共用的上报逻辑块大小。</para>
    /// </summary>
    /// <value>
    /// A positive alignment in bytes, or <see langword="null"/> when the profile imposed no single
    /// block size for the family (Legacy NAND page streams and NOR reads align per operation).
    /// <para>正数字节对齐值；配置未为该家族规定统一块大小（Legacy NAND 页流与 NOR 读取按操作对齐）时为
    /// <see langword="null"/>。</para>
    /// </value>
    public int? BlockSizeBytes { get; init; }

    /// <summary>
    /// Gets every reported physical region with its unified identity and permissions.
    /// <para>获取每个已上报物理区域及其统一身份与权限。</para>
    /// </summary>
    public required IReadOnlyList<MtkStorageRegionCapability> Regions { get; init; }

    /// <summary>
    /// Gets how the profile exposed the replay-protected area.
    /// <para>获取配置暴露重放保护区域的方式。</para>
    /// </summary>
    public required MtkStorageRpmbAccess RpmbAccess { get; init; }

    /// <summary>
    /// Gets NAND geometry semantics when the family is NAND.
    /// <para>获取家族为 NAND 时的 NAND 几何语义。</para>
    /// </summary>
    public MtkNandCapability? Nand { get; init; }

    /// <summary>
    /// Gets NOR alignment semantics when the family is NOR.
    /// <para>获取家族为 NOR 时的 NOR 对齐语义。</para>
    /// </summary>
    public MtkNorCapability? Nor { get; init; }

    /// <summary>
    /// Gets the session storage generation this snapshot was published under.
    /// <para>获取本快照发布时所属的会话存储代际。</para>
    /// </summary>
    /// <value>
    /// A monotonically increasing generation supplied by the session, or <see langword="null"/> for
    /// a standalone projection that no session has published.
    /// <para>由会话提供的单调递增代际；未经会话发布的独立投影为 <see langword="null"/>。</para>
    /// </value>
    public ulong? StorageGeneration { get; init; }

    /// <summary>
    /// Gets the reported card identity in hexadecimal, when the profile carried one.
    /// <para>获取上报的十六进制卡标识（若配置携带）。</para>
    /// </summary>
    public string? CidHex { get; init; }

    /// <summary>
    /// Gets the Legacy-reported firmware revision bytes in hexadecimal, when present.
    /// <para>获取 Legacy 上报的十六进制固件版本字节（若存在）。</para>
    /// </summary>
    public string? FirmwareRevisionHex { get; init; }

    /// <summary>
    /// Gets the agent's bounded write packet length in bytes, reported by XFlash queries only.
    /// <para>获取代理有界写入包长度（仅 XFlash 查询上报）。</para>
    /// </summary>
    public int? WritePacketBytes { get; init; }

    /// <summary>
    /// Gets the agent's bounded read packet length in bytes, reported by XFlash queries only.
    /// <para>获取代理有界读取包长度（仅 XFlash 查询上报）。</para>
    /// </summary>
    public int? ReadPacketBytes { get; init; }

    /// <summary>
    /// Gets the unmodified nonzero device type reported by the XFlash agent.
    /// <para>获取 XFlash 代理上报的未经修改且非零的设备类型值。</para>
    /// </summary>
    public uint? ReportedDeviceType { get; init; }

    /// <summary>
    /// Projects a validated XFlash storage report into the unified contract.
    /// <para>将经过校验的 XFlash 存储报告投影为统一契约。</para>
    /// </summary>
    /// <param name="storage">
    /// The parsed XFlash geometry for one explicitly queried family.
    /// <para>某显式查询家族已解析的 XFlash 几何信息。</para>
    /// </param>
    /// <param name="storageGeneration">
    /// The session generation to stamp, or <see langword="null"/> for a standalone projection.
    /// <para>要标记的会话代际；独立投影为 <see langword="null"/>。</para>
    /// </param>
    /// <param name="evidence">
    /// The evidence that produced the report, or <see langword="null"/> to synthesize a projection record.
    /// <para>产生该报告的证据；为 <see langword="null"/> 时合成一条投影记录。</para>
    /// </param>
    /// <param name="capturedAtUtc">
    /// The capture instant, defaulting to the current UTC time.
    /// <para>捕获时刻，默认为当前 UTC 时间。</para>
    /// </param>
    /// <returns>
    /// A validated snapshot preserving the XFlash packet lengths and device type.
    /// <para>保留 XFlash 包长度与设备类型的已校验快照。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is <see langword="null"/>.
    /// <para><paramref name="storage"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The report contains conflicting regions.
    /// <para>报告包含冲突区域。</para></exception>
    public static MtkStorageCapabilitySnapshot FromXFlash(MtkXFlashStorageInfo storage, ulong? storageGeneration = null,
        IReadOnlyList<ProtocolEvidence>? evidence = null, DateTimeOffset? capturedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var regions = XFlashRegions(storage);
        var nand = storage.Nand;
        return new MtkStorageCapabilitySnapshot
        {
            Source = MtkStorageDiscoverySource.XFlash,
            StorageGeneration = storageGeneration,
            CapturedAtUtc = capturedAtUtc ?? DateTimeOffset.UtcNow,
            Evidence = ResolveEvidence(evidence, MtkStorageDiscoverySource.XFlash, storageGeneration),
            Kind = storage.Kind,
            BlockSizeBytes = storage.BlockSize,
            Regions = regions,
            RpmbAccess = RegionsHaveRpmb(regions),
            Nand = storage.Kind == MtkXFlashStorageKind.Nand && nand is not null
                ? new MtkNandCapability
                {
                    PageSizeBytes = nand.PageSize,
                    EraseBlockSizeBytes = nand.EraseBlockSize,
                    SpareSizeBytes = nand.SpareSize,
                    TotalCapacityBytes = nand.TotalSize,
                    AvailableCapacityBytes = nand.AvailableSize,
                    BadBlockTableSupport = nand.HasBadBlockTable ? FeatureSupport.Supported : FeatureSupport.Unsupported,
                    SpareAccess = nand.SpareSize > 0 ? FeatureSupport.Supported : FeatureSupport.Unsupported,
                    EccHandling = FeatureSupport.Supported
                }
                : null,
            Nor = storage.Kind == MtkXFlashStorageKind.Nor
                ? new MtkNorCapability
                {
                    CapacityBytes = storage.Regions[0].CapacityBytes,
                    WriteGranularityBytes = storage.BlockSize
                }
                : null,
            CidHex = storage.CidHex.Length > 0 ? storage.CidHex : null,
            WritePacketBytes = storage.WritePacketLength,
            ReadPacketBytes = storage.ReadPacketLength,
            ReportedDeviceType = storage.DeviceType
        }.Validate();
    }

    /// <summary>
    /// Projects a validated XML DA hardware report into the unified contract.
    /// <para>将经过校验的 XML DA 硬件报告投影为统一契约。</para>
    /// </summary>
    /// <param name="storage">
    /// The parsed XML geometry with its fixed-order region names.
    /// <para>包含固定顺序区域名的已解析 XML 几何信息。</para>
    /// </param>
    /// <param name="storageGeneration">
    /// The session generation to stamp, or <see langword="null"/> for a standalone projection.
    /// <para>要标记的会话代际；独立投影为 <see langword="null"/>。</para>
    /// </param>
    /// <param name="evidence">
    /// The evidence that produced the report, or <see langword="null"/> to synthesize a projection record.
    /// <para>产生该报告的证据；为 <see langword="null"/> 时合成一条投影记录。</para>
    /// </param>
    /// <param name="capturedAtUtc">
    /// The capture instant, defaulting to the current UTC time.
    /// <para>捕获时刻，默认为当前 UTC 时间。</para>
    /// </param>
    /// <returns>
    /// A validated snapshot preserving the XML region tokens as canonical names.
    /// <para>保留 XML 区域标记作为规范名称的已校验快照。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is <see langword="null"/>.
    /// <para><paramref name="storage"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The report contains conflicting regions.
    /// <para>报告包含冲突区域。</para></exception>
    public static MtkStorageCapabilitySnapshot FromXml(MtkXmlStorageInfo storage, ulong? storageGeneration = null,
        IReadOnlyList<ProtocolEvidence>? evidence = null, DateTimeOffset? capturedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var regions = XmlRegions(storage);
        return new MtkStorageCapabilitySnapshot
        {
            Source = MtkStorageDiscoverySource.Xml,
            StorageGeneration = storageGeneration,
            CapturedAtUtc = capturedAtUtc ?? DateTimeOffset.UtcNow,
            Evidence = ResolveEvidence(evidence, MtkStorageDiscoverySource.Xml, storageGeneration),
            Kind = storage.Kind == MtkXmlStorageKind.Emmc ? MtkXFlashStorageKind.Emmc : MtkXFlashStorageKind.Ufs,
            BlockSizeBytes = storage.BlockSize,
            Regions = regions,
            RpmbAccess = RegionsHaveRpmb(regions),
            CidHex = storage.CidHex
        }.Validate();
    }

    /// <summary>
    /// Projects a validated Legacy DA inventory into the unified contract.
    /// <para>将经过校验的 Legacy DA 清单投影为统一契约。</para>
    /// </summary>
    /// <param name="storage">
    /// The parsed Legacy geometry for the explicitly selected family.
    /// <para>显式选定家族的已解析 Legacy 几何信息。</para>
    /// </param>
    /// <param name="storageGeneration">
    /// The session generation to stamp, or <see langword="null"/> for a standalone projection.
    /// <para>要标记的会话代际；独立投影为 <see langword="null"/>。</para>
    /// </param>
    /// <param name="evidence">
    /// The evidence that produced the report, or <see langword="null"/> to synthesize a projection record.
    /// <para>产生该报告的证据；为 <see langword="null"/> 时合成一条投影记录。</para>
    /// </param>
    /// <param name="capturedAtUtc">
    /// The capture instant, defaulting to the current UTC time.
    /// <para>捕获时刻，默认为当前 UTC 时间。</para>
    /// </param>
    /// <returns>
    /// A validated snapshot preserving the Legacy CID and firmware revision.
    /// <para>保留 Legacy CID 与固件版本的已校验快照。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is <see langword="null"/>.
    /// <para><paramref name="storage"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The report contains conflicting regions.
    /// <para>报告包含冲突区域。</para></exception>
    public static MtkStorageCapabilitySnapshot FromLegacy(MtkLegacyStorageInfo storage, ulong? storageGeneration = null,
        IReadOnlyList<ProtocolEvidence>? evidence = null, DateTimeOffset? capturedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var regions = LegacyRegions(storage);
        var nand = storage.Nand;
        var nor = storage.Nor;
        return new MtkStorageCapabilitySnapshot
        {
            Source = MtkStorageDiscoverySource.Legacy,
            StorageGeneration = storageGeneration,
            CapturedAtUtc = capturedAtUtc ?? DateTimeOffset.UtcNow,
            Evidence = ResolveEvidence(evidence, MtkStorageDiscoverySource.Legacy, storageGeneration),
            Kind = storage.Kind,
            BlockSizeBytes = storage.Kind == MtkXFlashStorageKind.Emmc ? 512 : null,
            Regions = regions,
            RpmbAccess = RegionsHaveRpmb(regions),
            Nand = storage.Kind == MtkXFlashStorageKind.Nand
                ? new MtkNandCapability
                {
                    PageSizeBytes = RequireLegacyNand(nand).PageSize,
                    EraseBlockSizeBytes = RequireLegacyNand(nand).EraseBlockSize,
                    SpareSizeBytes = RequireLegacyNand(nand).SpareSize,
                    TotalCapacityBytes = RequireLegacyNand(nand).CapacityBytes,
                    BadBlockTableSupport = RequireLegacyNand(nand).HasBadBlockTable
                        ? FeatureSupport.Supported : FeatureSupport.Unsupported,
                    SpareAccess = RequireLegacyNand(nand).SpareSize > 0
                        ? FeatureSupport.Supported : FeatureSupport.Unsupported,
                    EccHandling = FeatureSupport.Supported
                }
                : null,
            Nor = storage.Kind == MtkXFlashStorageKind.Nor
                ? new MtkNorCapability
                {
                    CapacityBytes = RequireLegacyNor(nor).CapacityBytes,
                    WriteGranularityBytes = 512,
                    FlashDeviceId = RequireLegacyNor(nor).FlashId
                }
                : null,
            CidHex = storage.CidHex.Length > 0 ? storage.CidHex : null,
            FirmwareRevisionHex = storage.FirmwareRevisionHex.Length > 0 ? storage.FirmwareRevisionHex : null
        }.Validate();
    }

    /// <summary>
    /// Finds the first region with the requested identity.
    /// <para>查找第一个具有指定身份的分区。</para>
    /// </summary>
    /// <param name="kind">
    /// The region identity to locate; <see cref="MtkStorageRegionKind.Unknown"/> is not a lookup key.
    /// <para>要定位的区域身份；<see cref="MtkStorageRegionKind.Unknown"/> 不是可查找的键。</para>
    /// </param>
    /// <returns>
    /// The matching region, or <see langword="null"/> when the profile never reported it; a null
    /// result must fail closed instead of standing in for ordinary storage.
    /// <para>匹配的区域；配置从未上报时为 <see langword="null"/>；查找结果为空时必须失败关闭，不得当作普通存储。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is
    /// <see cref="MtkStorageRegionKind.Unknown"/>.
    /// <para><paramref name="kind"/> 为 <see cref="MtkStorageRegionKind.Unknown"/>。</para></exception>
    public MtkStorageRegionCapability? FindRegion(MtkStorageRegionKind kind)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(kind, MtkStorageRegionKind.Unknown);
        return Regions.FirstOrDefault(region => region.Kind == kind);
    }

    /// <summary>
    /// Finds the region with the requested canonical name.
    /// <para>按规范名称查找区域。</para>
    /// </summary>
    /// <param name="canonicalName">
    /// The exact, case-sensitive canonical region name.
    /// <para>精确且区分大小写的规范区域名称。</para>
    /// </param>
    /// <returns>
    /// The matching region, or <see langword="null"/> when absent.
    /// <para>匹配的区域，缺失时为 <see langword="null"/>。</para>
    /// </returns>
    public MtkStorageRegionCapability? FindRegion(string canonicalName)
    {
        ArgumentNullException.ThrowIfNull(canonicalName);
        return Regions.FirstOrDefault(region => region.CanonicalName == canonicalName);
    }

    /// <summary>
    /// Gets the regions ordinary block operations may target.
    /// <para>获取普通块操作可指向的区域。</para>
    /// </summary>
    /// <value>
    /// Reported regions excluding authenticated-only and replay-protected ones; write permission is
    /// still governed by each region's <see cref="MtkStorageRegionCapability.IsOrdinaryWritable"/>.
    /// <para>已上报区域中排除需认证与重放保护后的集合；写入权限仍由各区域的
    /// <see cref="MtkStorageRegionCapability.IsOrdinaryWritable"/> 决定。</para>
    /// </value>
    public IReadOnlyList<MtkStorageRegionCapability> OrdinaryRegions =>
        [.. Regions.Where(region => !region.AuthenticatedOnly && region.Kind != MtkStorageRegionKind.Rpmb)];

    /// <summary>
    /// Compares every reported fact while ignoring capture time, evidence and generation.
    /// <para>比较全部已上报事实，忽略捕获时刻、证据与代际。</para>
    /// </summary>
    /// <param name="other">
    /// The snapshot to compare with, typically a re-discovery under a newer generation.
    /// <para>要比较的快照，通常是新一代际下的重新发现结果。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when both snapshots describe exactly the same geometry and permissions.
    /// <para>两份快照描述完全相同的几何信息与权限时返回 <see langword="true"/>。</para>
    /// </returns>
    public bool GeometryEquals(MtkStorageCapabilitySnapshot? other) => other is not null
        && Kind == other.Kind
        && Source == other.Source
        && BlockSizeBytes == other.BlockSizeBytes
        && Regions.SequenceEqual(other.Regions)
        && RpmbAccess == other.RpmbAccess
        && Nand == other.Nand
        && Nor == other.Nor
        && CidHex == other.CidHex
        && FirmwareRevisionHex == other.FirmwareRevisionHex
        && WritePacketBytes == other.WritePacketBytes
        && ReadPacketBytes == other.ReadPacketBytes
        && ReportedDeviceType == other.ReportedDeviceType;

    /// <summary>
    /// Checks the snapshot for internal contradictions.
    /// <para>检查快照的内部矛盾。</para>
    /// </summary>
    /// <returns>
    /// The validated snapshot, so the check chains onto an object initializer.
    /// <para>通过校验的快照本身，使检查可链式接在对象初始化器之后。</para>
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The region list is empty or duplicated, an authenticated region is not RPMB or vice versa,
    /// RPMB posture contradicts the region list, a negative capacity was carried, or the NAND/NOR
    /// companion records contradict the family.
    /// <para>区域列表为空或重复、需认证区域不是 RPMB（或反之）、RPMB 姿态与区域列表矛盾、
    /// 携带负容量，或 NAND/NOR 附属记录与家族矛盾。</para>
    /// </exception>
    public MtkStorageCapabilitySnapshot Validate()
    {
        if (Regions.Count == 0)
        {
            throw new InvalidOperationException("A storage capability snapshot must report at least one region.");
        }
        if (Regions.Select(region => region.CanonicalName).Distinct(StringComparer.Ordinal).Count() != Regions.Count)
        {
            throw new InvalidOperationException("Two regions share the same canonical name.");
        }
        var identifiers = Regions.Where(region => region.PhysicalIdentifier is not null)
            .Select(region => region.PhysicalIdentifier!.Value).ToArray();
        if (identifiers.Distinct().Count() != identifiers.Length)
        {
            throw new InvalidOperationException("Two regions share the same physical identifier.");
        }
        if (Regions.Any(region => region.CapacityBytes < 0))
        {
            throw new InvalidOperationException("A region carried a negative capacity.");
        }
        if (Regions.Any(region => region.AuthenticatedOnly && region.Kind != MtkStorageRegionKind.Rpmb)
            || Regions.Any(region => region.Kind == MtkStorageRegionKind.Rpmb && !region.AuthenticatedOnly))
        {
            throw new InvalidOperationException("Authenticated-only marking and the RPMB region identity disagree.");
        }
        if (Regions.Where(region => region.Kind is MtkStorageRegionKind.EmmcUser or MtkStorageRegionKind.EmmcBoot1
            or MtkStorageRegionKind.EmmcBoot2 or MtkStorageRegionKind.Rpmb
            or MtkStorageRegionKind.NandMainArea or MtkStorageRegionKind.NorWholeDevice)
            .GroupBy(region => region.Kind).Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("A unique region identity was reported more than once.");
        }
        if ((RpmbAccess == MtkStorageRpmbAccess.None) != !Regions.Any(region => region.Kind == MtkStorageRegionKind.Rpmb))
        {
            throw new InvalidOperationException("The RPMB posture contradicts the reported regions.");
        }
        if (Kind == MtkXFlashStorageKind.Nand && (Nand is null || Nor is not null)
            || Kind == MtkXFlashStorageKind.Nor && (Nor is null || Nand is not null)
            || Kind is MtkXFlashStorageKind.Emmc or MtkXFlashStorageKind.Ufs && (Nand is not null || Nor is not null))
        {
            throw new InvalidOperationException("The NAND or NOR companion record contradicts the storage family.");
        }
        return this;
    }

    private static MtkStorageRegionCapability[] XFlashRegions(MtkXFlashStorageInfo storage) => storage.Kind switch
    {
        MtkXFlashStorageKind.Emmc or MtkXFlashStorageKind.Ufs => [.. storage.Regions.Select(region =>
            new MtkStorageRegionCapability
            {
                Kind = XFlashRegionKind(storage.Kind, region),
                CanonicalName = XFlashRegionName(storage.Kind, region),
                PhysicalIdentifier = region.PartitionId,
                CapacityBytes = region.CapacityBytes,
                ReadSupport = OrdinarySupport(region.RequiresAuthenticatedAccess, region.CapacityBytes),
                WriteSupport = OrdinarySupport(region.RequiresAuthenticatedAccess, region.CapacityBytes),
                EraseSupport = OrdinarySupport(region.RequiresAuthenticatedAccess, region.CapacityBytes),
                AuthenticatedOnly = region.RequiresAuthenticatedAccess,
                AlignmentBytes = storage.BlockSize
            })],
        MtkXFlashStorageKind.Nor => [new MtkStorageRegionCapability
        {
            Kind = MtkStorageRegionKind.NorWholeDevice,
            CanonicalName = "NOR-WHOLE",
            PhysicalIdentifier = storage.Regions[0].PartitionId,
            CapacityBytes = storage.Regions[0].CapacityBytes,
            ReadSupport = FeatureSupport.Supported,
            WriteSupport = FeatureSupport.Supported,
            EraseSupport = FeatureSupport.Supported,
            AlignmentBytes = storage.BlockSize
        }],
        MtkXFlashStorageKind.Nand => [new MtkStorageRegionCapability
        {
            Kind = MtkStorageRegionKind.NandMainArea,
            CanonicalName = "NAND-MAIN",
            PhysicalIdentifier = storage.Regions[0].PartitionId,
            CapacityBytes = RequireXFlashNand(storage.Nand).AvailableSize,
            ReadSupport = FeatureSupport.Supported,
            WriteSupport = FeatureSupport.Supported,
            EraseSupport = FeatureSupport.Supported,
            AlignmentBytes = RequireXFlashNand(storage.Nand).PageSize
        }],
        _ => throw new InvalidOperationException("The XFlash storage family is unsupported.")
    };

    private static MtkStorageRegionKind XFlashRegionKind(MtkXFlashStorageKind family, MtkXFlashStorageRegion region) =>
        region.RequiresAuthenticatedAccess ? MtkStorageRegionKind.Rpmb
            : family == MtkXFlashStorageKind.Ufs ? MtkStorageRegionKind.UfsLogicalUnit
            : region.PartitionId == 8 ? MtkStorageRegionKind.EmmcUser : MtkStorageRegionKind.Unknown;

    private static string XFlashRegionName(MtkXFlashStorageKind family, MtkXFlashStorageRegion region) =>
        family switch
        {
            MtkXFlashStorageKind.Emmc when region.RequiresAuthenticatedAccess => "EMMC-RPMB",
            MtkXFlashStorageKind.Emmc when region.PartitionId == 8 => "EMMC-USER",
            MtkXFlashStorageKind.Emmc => $"XFLASH-EMMC-PARTITION-{region.PartitionId}",
            _ => $"XFLASH-UFS-PARTITION-{region.PartitionId}"
        };

    private static MtkStorageRegionCapability[] XmlRegions(MtkXmlStorageInfo storage)
    {
        var regions = new MtkStorageRegionCapability[storage.Regions.Count];
        for (var index = 0; index < regions.Length; index++)
        {
            var region = storage.Regions[index];
            regions[index] = new MtkStorageRegionCapability
            {
                Kind = XmlRegionKind(region),
                CanonicalName = region.Name,
                CapacityBytes = region.CapacityBytes,
                ReadSupport = OrdinarySupport(region.RequiresAuthenticatedAccess, region.CapacityBytes),
                WriteSupport = OrdinarySupport(region.RequiresAuthenticatedAccess, region.CapacityBytes),
                EraseSupport = OrdinarySupport(region.RequiresAuthenticatedAccess, region.CapacityBytes),
                AuthenticatedOnly = region.RequiresAuthenticatedAccess,
                AlignmentBytes = storage.BlockSize
            };
        }
        return regions;
    }

    private static MtkStorageRegionKind XmlRegionKind(MtkXmlStorageRegion region) =>
        region.RequiresAuthenticatedAccess ? MtkStorageRegionKind.Rpmb : region.Name switch
        {
            "EMMC-USER" => MtkStorageRegionKind.EmmcUser,
            "EMMC-BOOT1" => MtkStorageRegionKind.EmmcBoot1,
            "EMMC-BOOT2" => MtkStorageRegionKind.EmmcBoot2,
            "EMMC-GP1" or "EMMC-GP2" or "EMMC-GP3" or "EMMC-GP4" => MtkStorageRegionKind.EmmcGeneralPurpose,
            "UFS-LUA0" or "UFS-LUA1" or "UFS-LUA2" => MtkStorageRegionKind.UfsLogicalUnit,
            _ => MtkStorageRegionKind.Unknown
        };

    private static MtkStorageRegionCapability[] LegacyRegions(MtkLegacyStorageInfo storage) => storage.Kind switch
    {
        MtkXFlashStorageKind.Emmc => [.. storage.Regions.Select(region => new MtkStorageRegionCapability
        {
            Kind = region.RequiresAuthenticatedAccess ? MtkStorageRegionKind.Rpmb
                : region.PartitionId == 8 ? MtkStorageRegionKind.EmmcUser : MtkStorageRegionKind.Unknown,
            CanonicalName = region.RequiresAuthenticatedAccess ? "EMMC-RPMB"
                : region.PartitionId == 8 ? "EMMC-USER" : $"LEGACY-EMMC-PARTITION-{region.PartitionId}",
            PhysicalIdentifier = region.PartitionId,
            CapacityBytes = region.CapacityBytes,
            ReadSupport = OrdinarySupport(region.RequiresAuthenticatedAccess, region.CapacityBytes),
            WriteSupport = OrdinarySupport(region.RequiresAuthenticatedAccess, region.CapacityBytes),
            EraseSupport = OrdinarySupport(region.RequiresAuthenticatedAccess, region.CapacityBytes),
            AuthenticatedOnly = region.RequiresAuthenticatedAccess,
            AlignmentBytes = 512
        })],
        MtkXFlashStorageKind.Nor => [new MtkStorageRegionCapability
        {
            Kind = MtkStorageRegionKind.NorWholeDevice,
            CanonicalName = "NOR-WHOLE",
            CapacityBytes = RequireLegacyNor(storage.Nor).CapacityBytes,
            ReadSupport = FeatureSupport.Supported,
            WriteSupport = FeatureSupport.Supported,
            EraseSupport = FeatureSupport.Supported,
            AlignmentBytes = 512
        }],
        MtkXFlashStorageKind.Nand => [new MtkStorageRegionCapability
        {
            Kind = MtkStorageRegionKind.NandMainArea,
            CanonicalName = "NAND-MAIN",
            CapacityBytes = RequireLegacyNand(storage.Nand).CapacityBytes,
            ReadSupport = FeatureSupport.Supported,
            WriteSupport = FeatureSupport.Supported,
            EraseSupport = FeatureSupport.Supported,
            AlignmentBytes = RequireLegacyNand(storage.Nand).PageSize
        }],
        _ => throw new InvalidOperationException("The Legacy storage family is unsupported.")
    };

    private static IReadOnlyList<ProtocolEvidence> ResolveEvidence(IReadOnlyList<ProtocolEvidence>? evidence,
        MtkStorageDiscoverySource source, ulong? storageGeneration) => evidence ??
        [
            new ProtocolEvidence
            {
                Protocol = "mtk",
                Phase = "storage-snapshot",
                Outcome = CommandOutcomeKind.Completed,
                Detail = $"{source} capability projected; generation={(storageGeneration?.ToString() ?? "unbound")}"
            }
        ];

    private static FeatureSupport OrdinarySupport(bool authenticated, long capacityBytes) => authenticated
        || capacityBytes == 0 ? FeatureSupport.Unsupported : FeatureSupport.Supported;

    private static MtkStorageRpmbAccess RegionsHaveRpmb(IReadOnlyList<MtkStorageRegionCapability> regions) =>
        regions.Any(region => region.Kind == MtkStorageRegionKind.Rpmb)
            ? MtkStorageRpmbAccess.Reported : MtkStorageRpmbAccess.None;

    private static MtkXFlashNandGeometry RequireXFlashNand(MtkXFlashNandGeometry? nand) =>
        nand ?? throw new InvalidOperationException("The NAND family carries no NAND geometry.");

    private static MtkLegacyNorGeometry RequireLegacyNor(MtkLegacyNorGeometry? nor) =>
        nor ?? throw new InvalidOperationException("The NOR family carries no NOR geometry.");

    private static MtkLegacyNandGeometry RequireLegacyNand(MtkLegacyNandGeometry? nand) =>
        nand ?? throw new InvalidOperationException("The NAND family carries no NAND geometry.");
}