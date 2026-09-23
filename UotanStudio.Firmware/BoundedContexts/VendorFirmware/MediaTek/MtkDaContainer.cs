namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

/// <summary>Known all-in-one download-agent directory layouts.</summary>
public enum MtkDaDirectoryLayout
{
    /// <summary>Validate both layouts and reject ambiguous input.</summary>
    Auto,
    /// <summary>A 216-byte entry with a 16-byte fixed header.</summary>
    Legacy,
    /// <summary>A 220-byte entry with a 20-byte fixed header including software version.</summary>
    Modern
}

/// <summary>A loader region's on-disk fields, without implied execution or signature validation.</summary>
/// <param name="FileOffset">The absolute byte offset in the container.</param>
/// <param name="Length">The region byte count including any signature suffix.</param>
/// <param name="LoadAddress">The unmodified target load address.</param>
/// <param name="StartOffset">The unmodified fourth region field; execution semantics are not inferred.</param>
/// <param name="SignatureLength">The declared signature suffix byte count.</param>
public sealed record MtkDaRegion(uint FileOffset, uint Length, uint LoadAddress, uint StartOffset, uint SignatureLength);

/// <summary>One explicitly identified download-agent directory entry.</summary>
/// <param name="HardwareCode">The target hardware code.</param>
/// <param name="HardwareSubCode">The hardware subcode.</param>
/// <param name="HardwareVersion">The hardware version.</param>
/// <param name="SoftwareVersion">The software version, absent in the legacy layout.</param>
/// <param name="PageSize">The unmodified page-size field; zero is preserved.</param>
/// <param name="EntryRegionIndex">The entry-region index supplied by the container.</param>
/// <param name="Regions">The validated, read-only region directory.</param>
public sealed record MtkDaEntry(ushort HardwareCode, ushort HardwareSubCode, ushort HardwareVersion,
    ushort? SoftwareVersion, ushort PageSize, ushort EntryRegionIndex, IReadOnlyList<MtkDaRegion> Regions);

/// <summary>Validated metadata for an all-in-one MTK download-agent container.</summary>
/// <param name="Layout">The unambiguous or explicitly selected directory layout.</param>
/// <param name="HeaderIndicatesV6">Whether the header contains the V6 marker; this is not a protocol capability probe.</param>
/// <param name="Entries">The read-only entry directory; selection is left to the caller.</param>
public sealed record MtkDaContainer(MtkDaDirectoryLayout Layout, bool HeaderIndicatesV6, IReadOnlyList<MtkDaEntry> Entries);