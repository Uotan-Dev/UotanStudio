using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Storage;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private MtkStorageDiscoverySource? _storageCapabilitySource;

    /// <summary>
    /// Gets the unified storage capability published by the latest successful discovery on any
    /// XFlash, XML or Legacy path, or <see langword="null"/> while no geometry is current.
    /// <para>获取由 XFlash、XML 或 Legacy 任一路径最近一次成功发现发布的统一存储能力；没有有效几何信息时为
    /// <see langword="null"/>。</para>
    /// </summary>
    /// <value>
    /// The immutable snapshot describing regions, RPMB posture, NAND/NOR semantics and permissions;
    /// it is cleared whenever the bound wire geometry is cleared, and it never grants authorization.
    /// <para>描述区域、RPMB 姿态、NAND/NOR 语义与权限的不可变快照；绑定它的线协议几何信息被清除时它同样被清除，
    /// 且从不授予任何授权。</para>
    /// </value>
    public MtkStorageCapabilitySnapshot? StorageCapability { get; private set; }

    /// <summary>
    /// Gets the monotonic count of storage capability publications on this session.
    /// <para>获取本会话存储能力发布次数的单调计数。</para>
    /// </summary>
    /// <value>
    /// The current storage generation; <c>0</c> before the first successful discovery. Every
    /// published snapshot carries the generation captured with it.
    /// <para>当前存储代际；首次成功发现前为 <c>0</c>。每份发布的快照都携带与其一同捕获的代际。</para>
    /// </value>
    public ulong StorageGeneration { get; private set; }

    private void PublishStorageCapability(MtkStorageDiscoverySource source,
        Func<ulong, IReadOnlyList<ProtocolEvidence>, MtkStorageCapabilitySnapshot> build)
    {
        StorageGeneration = checked(StorageGeneration + 1);
        var evidence = new ProtocolEvidence
        {
            Protocol = "mtk",
            Phase = "storage-snapshot",
            Outcome = CommandOutcomeKind.Completed,
            Detail = $"{source} storage capability published; generation={StorageGeneration}"
        };
        StorageCapability = build(StorageGeneration, [evidence]);
        _storageCapabilitySource = source;
        _evidence.Add(evidence);
    }

    private void ClearStorageCapability(MtkStorageDiscoverySource source)
    {
        if (_storageCapabilitySource == source)
        {
            StorageCapability = null;
            _storageCapabilitySource = null;
        }
    }
}