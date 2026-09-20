namespace UotanStudio.Firmware.BoundedContexts.Archive.Models;

public enum ArchiveFormat
{
    Unknown = 0,

    Binary,

    OldAscii,

    NewAscii,

    CrcAscii,

    Tar,

    Ustar,

    HpOldAscii,

    HpBinary,
}