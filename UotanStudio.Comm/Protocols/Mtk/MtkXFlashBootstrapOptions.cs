using UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Explicit version policy for an already selected loader entry, not automatic device compatibility proof.</summary>
public enum MtkDaIdentityMatch
{
    /// <summary>Hardware/subcode and both revision fields must match exactly.</summary>
    Exact,
    /// <summary>Hardware/subcode must match; device revisions must meet the entry's minimum revisions.</summary>
    MinimumRevisions
}

/// <summary>Caller-selected resources and policy for one stock XFlash startup attempt.</summary>
public sealed record MtkXFlashBootstrapOptions
{
    /// <summary>Gets the explicitly selected loader path.</summary>
    public required string LoaderPath { get; init; }
    /// <summary>Gets the expected directory layout or unambiguous detection policy.</summary>
    public MtkDaDirectoryLayout DirectoryLayout { get; init; } = MtkDaDirectoryLayout.Auto;
    /// <summary>Gets the zero-based loader entry index.</summary>
    public required int EntryIndex { get; init; }
    /// <summary>Gets the DA1 region index; its complete bytes and declared signature length are sent.</summary>
    public required int StageOneRegionIndex { get; init; }
    /// <summary>Gets the distinct DA2 region index.</summary>
    public required int StageTwoRegionIndex { get; init; }
    /// <summary>Gets the explicit DA2 signature-tail inclusion policy.</summary>
    public required MtkDaRegionContent StageTwoContent { get; init; }
    /// <summary>Gets the identity matching policy; no wildcard hardware/subcode match is permitted.</summary>
    public MtkDaIdentityMatch IdentityMatch { get; init; } = MtkDaIdentityMatch.Exact;
    /// <summary>Gets the selected storage family; no fallback family is inferred.</summary>
    public required MtkXFlashStorageKind StorageKind { get; init; }
    /// <summary>Gets optional explicitly extracted EMI, required for the Boot ROM path.</summary>
    public MtkEmiParameters? EmiParameters { get; init; }
    /// <summary>Gets the UART log level sent to DA1.</summary>
    public uint DaLogLevel { get; init; } = 2;
    /// <summary>Gets the per-transfer timeout in milliseconds.</summary>
    public int TransferTimeoutMs { get; init; } = 5000;
    /// <summary>Gets the boot handshake's per-byte timeout in milliseconds.</summary>
    public int HandshakeTimeoutMs { get; init; } = 100;
    /// <summary>
    /// Gets the explicit provider for the boot byte-stream profile. No provider is invoked
    /// unless BootResourceOrder selects resources; this does not implement packet/ZLP USB authentication.
    /// </summary>
    public IMtkBootResourceProvider? BootResourceProvider { get; init; }
    /// <summary>
    /// Gets zero to two distinct resource kinds in caller-selected upload order, captured
    /// before startup. The provider must be present exactly when this selection is nonempty.
    /// The caller must not mutate the collection during initial synchronous capture.
    /// </summary>
    public IReadOnlyList<MtkBootResourceKind> BootResourceOrder { get; init; } = [];
    /// <summary>Gets an optional explicit SLA responder, invoked only if the DA1 request reports 0x1D0D.</summary>
    public IMtkBootRomSlaResponder? BootRomSlaResponder { get; init; }
    /// <summary>Gets the cooperative SLA exchange deadline in milliseconds; no retry occurs on expiry.</summary>
    public int SlaTimeoutMs { get; init; } = 60000;
}