namespace UotanStudio.Firmware.NTPi.Exceptions;

/// <summary>
/// <para>NTPI 相关异常的基类。</para>
/// Base class for NTPI-related exceptions.
/// </summary>
public class NtpiException : Exception
{
    /// <summary>
    /// <para>使用指定的错误信息初始化 NtpiException 的新实例。</para>
    /// Initializes a new instance of NtpiException with the specified message.
    /// </summary>
    public NtpiException(string message) : base(message) { }

    /// <summary>
    /// <para>使用指定的错误信息和内部异常初始化 NtpiException 的新实例。</para>
    /// Initializes a new instance of NtpiException with the specified message and inner exception.
    /// </summary>
    public NtpiException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// <para>表示 NTPI 文件格式无效时抛出的异常。</para>
/// Exception thrown when the NTPI file format is invalid.
/// </summary>
public sealed class InvalidFormatException : NtpiException
{
    /// <summary>
    /// <para>使用指定的错误信息初始化 InvalidFormatException 的新实例。</para>
    /// Initializes a new instance of InvalidFormatException with the specified message.
    /// </summary>
    public InvalidFormatException(string message) : base(message) { }

    /// <summary>
    /// <para>使用指定的错误信息和内部异常初始化 InvalidFormatException 的新实例。</para>
    /// Initializes a new instance of InvalidFormatException with the specified message and inner exception.
    /// </summary>
    public InvalidFormatException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// <para>表示 NTPI 固件包版本不受支持时抛出的异常。</para>
/// Exception thrown when the NTPI firmware package version is unsupported.
/// </summary>
/// <remarks>
/// <para>使用指定的版本号初始化 UnsupportedVersionException 的新实例。</para>
/// Initializes a new instance of UnsupportedVersionException with the specified version numbers.
/// </remarks>
public sealed class UnsupportedVersionException(ulong major, ulong minor, ulong patch) : NtpiException($"Unsupported NTPI version: {major}.{minor}.{patch}. No encryption keys are registered for this version.")
{
}

/// <summary>
/// <para>表示 NTPI 文件完整性验证失败时抛出的异常。</para>
/// Exception thrown when NTPI file integrity verification fails.
/// </summary>
public sealed class IntegrityVerificationException : NtpiException
{
    /// <summary>
    /// <para>使用指定的错误信息初始化 IntegrityVerificationException 的新实例。</para>
    /// Initializes a new instance of IntegrityVerificationException with the specified message.
    /// </summary>
    public IntegrityVerificationException(string message) : base(message) { }

    /// <summary>
    /// <para>使用指定的错误信息和内部异常初始化 IntegrityVerificationException 的新实例。</para>
    /// Initializes a new instance of IntegrityVerificationException with the specified message and inner exception.
    /// </summary>
    public IntegrityVerificationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// <para>表示 LZMA 压缩或解压失败时抛出的异常。</para>
/// Exception thrown when LZMA compression or decompression fails.
/// </summary>
public sealed class Lzma2CompressionException : NtpiException
{
    /// <summary>
    /// <para>使用指定的错误信息初始化 Lzma2CompressionException 的新实例。</para>
    /// Initializes a new instance of Lzma2CompressionException with the specified message.
    /// </summary>
    public Lzma2CompressionException(string message) : base(message) { }

    /// <summary>
    /// <para>使用指定的错误信息和内部异常初始化 Lzma2CompressionException 的新实例。</para>
    /// Initializes a new instance of Lzma2CompressionException with the specified message and inner exception.
    /// </summary>
    public Lzma2CompressionException(string message, Exception innerException) : base(message, innerException) { }
}