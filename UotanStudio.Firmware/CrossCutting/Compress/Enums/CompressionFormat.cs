namespace UotanStudio.Firmware.CrossCutting.Compress;

public enum CompressionFormat
{
    None = 0,

    Gzip = 1,

    Zlib = 2,

    Deflate = 3,

    Brotli = 4,

    Lz4 = 5,

    Lz4Legacy = 6,

    Lzma = 7,

    Xz = 8,

    Bzip2 = 9,

    Zstd = 10,

    Lzop = 11,

    Zopfli = 12
}