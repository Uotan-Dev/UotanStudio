namespace UotanStudio.Firmware.Cpio.Model;

public enum CpioFileType
{
    Unknown = 0,

    Regular,

    Directory,

    SymbolicLink,

    HardLink,

    CharacterDevice,

    BlockDevice,

    Fifo,

    Socket,
}
