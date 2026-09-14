namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;

public enum CompressionType
{
    None = 0,

    Gzip = 1,

    Lz4 = 2,

    Lz4Legacy = 3,

    Lz4Lg = 4,

    Lzma = 5,

    Xz = 6,

    Bzip2 = 7,

    Zopfli = 8,

    Lzop = 9,

    Zstd = 10,

    Brotli = 11
}