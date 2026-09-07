namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;

public enum ChunkType : ushort
{
    Raw = 0xCAC1,

    Fill = 0xCAC2,

    DontCare = 0xCAC3,

    Crc32 = 0xCAC4
}
