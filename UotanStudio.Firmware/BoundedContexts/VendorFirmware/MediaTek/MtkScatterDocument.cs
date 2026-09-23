namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

/// <summary>Parsed scatter metadata, not an authorized device operation or a resolved file manifest.</summary>
/// <param name="ConfigVersion">The declared platform configuration version.</param>
/// <param name="Platform">The declared platform token; not proof of device compatibility.</param>
/// <param name="Project">The declared project name.</param>
/// <param name="PlatformFields">All platform fields, including uninterpreted extensions.</param>
/// <param name="Storages">Storage-specific layouts in source order.</param>
public sealed record MtkScatterDocument(string ConfigVersion, string Platform, string Project,
    IReadOnlyDictionary<string, string> PlatformFields, IReadOnlyList<MtkScatterStorage> Storages);

/// <summary>A storage-specific scatter layout, not discovered device geometry.</summary>
/// <param name="Name">The declared storage type.</param>
/// <param name="BootChannel">The declared boot channel.</param>
/// <param name="BlockSize">The scatter block-size hint; never use as the device logical sector size.</param>
/// <param name="Fields">All storage configuration fields.</param>
/// <param name="Partitions">Partition entries, including disabled and reserved entries.</param>
public sealed record MtkScatterStorage(string Name, string BootChannel, ulong BlockSize,
    IReadOnlyDictionary<string, string> Fields, IReadOnlyList<MtkScatterPartition> Partitions);

/// <summary>Untrusted partition declarations. Addresses and file names are not resolved or executable.</summary>
/// <param name="Index">The exact SYS index token.</param>
/// <param name="Name">The partition name.</param>
/// <param name="FileName">The literal file name, including the NONE sentinel.</param>
/// <param name="IsDownload">The manifest's download flag, not user confirmation.</param>
/// <param name="Type">The declared image type, not content detection.</param>
/// <param name="LinearStartAddress">The declared linear address.</param>
/// <param name="PhysicalStartAddress">The separate declared physical address.</param>
/// <param name="Size">The declared byte size; sentinel values remain uninterpreted.</param>
/// <param name="Region">The literal storage region token.</param>
/// <param name="Storage">The literal hardware-storage token.</param>
/// <param name="BoundaryCheck">The manifest boundary-check flag, never an instruction to disable host checks.</param>
/// <param name="IsReserved">The reserved-entry flag.</param>
/// <param name="OperationType">The literal operation policy token.</param>
/// <param name="Fields">All fields, including extensions which callers must not silently treat as supported.</param>
public sealed record MtkScatterPartition(string Index, string Name, string FileName, bool IsDownload, string Type,
    ulong LinearStartAddress, ulong PhysicalStartAddress, ulong Size, string Region, string Storage,
    bool BoundaryCheck, bool IsReserved, string OperationType, IReadOnlyDictionary<string, string> Fields);