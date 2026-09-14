namespace UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model;

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