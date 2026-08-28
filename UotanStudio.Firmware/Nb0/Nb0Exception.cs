namespace UotanStudio.Firmware.Nb0;

/// <summary>
/// <para>NB0 固件格式操作中发生的异常的基类。</para>
/// The base exception class for errors that occur during NB0 firmware format operations.
/// </summary>
public class Nb0Exception : Exception
{
    /// <summary>
    /// <para>使用指定的错误消息初始化 Nb0Exception 的新实例。</para>
    /// Initializes a new instance of Nb0Exception with a specified error message.
    /// </summary>
    public Nb0Exception(string message) : base(message) { }

    /// <summary>
    /// <para>使用指定的错误消息和内部异常初始化 Nb0Exception 的新实例。</para>
    /// Initializes a new instance of Nb0Exception with a specified error message and inner exception.
    /// </summary>
    public Nb0Exception(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// <para>当 NB0 固件文件已损坏或格式无效时抛出的异常。</para>
/// The exception that is thrown when an NB0 firmware file is corrupted or has an invalid format.
/// </summary>
/// <remarks>
/// <para>使用指定的错误消息初始化 Nb0CorruptedException 的新实例。</para>
/// Initializes a new instance of Nb0CorruptedException with a specified error message.
/// </remarks>
public sealed class Nb0CorruptedException : Nb0Exception
{
    public Nb0CorruptedException(string message) : base(message) { }
    public Nb0CorruptedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// <para>提供 CRC-32 校验和的计算功能。</para>
/// Provides CRC-32 checksum calculation functionality.
/// </summary>
/// <remarks>
/// <para>使用多项式 0xEDB88320(即 CRC-32/ISO-HDLC)进行计算。</para>
/// Uses polynomial 0xEDB88320 (CRC-32/ISO-HDLC) for calculation.
/// </remarks>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                {
                    crc = (crc >> 1) ^ 0xEDB88320;
                }
                else
                {
                    crc >>= 1;
                }
            }
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// <para>计算指定字节数组的 CRC-32 校验和。</para>
    /// Calculates the CRC-32 checksum of the specified byte array.
    /// </summary>
    public static uint Compute(byte[] data) => Compute(data, 0, data.Length);

    /// <summary>
    /// <para>计算指定字节数组范围的 CRC-32 校验和。</para>
    /// Calculates the CRC-32 checksum of the specified byte array range.
    /// </summary>
    public static uint Compute(byte[] data, int offset, int count)
    {
        var crc = 0xFFFFFFFF;
        for (var i = offset; i < offset + count; i++)
        {
            crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF];
        }
        return crc ^ 0xFFFFFFFF;
    }
}