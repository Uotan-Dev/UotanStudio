namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Exceptions;

public class NtpiException : Exception
{
    /// <summary>
    /// Initializes a new instance of NtpiException with the specified message.
    /// <para>使用指定的错误信息初始化 NtpiException 的新实例。</para>
    /// </summary>
    public NtpiException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of NtpiException with the specified message and inner exception.
    /// <para>使用指定的错误信息和内部异常初始化 NtpiException 的新实例。</para>
    /// </summary>
    public NtpiException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class InvalidFormatException : NtpiException
{
    /// <summary>
    /// Initializes a new instance of InvalidFormatException with the specified message.
    /// <para>使用指定的错误信息初始化 InvalidFormatException 的新实例。</para>
    /// </summary>
    public InvalidFormatException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of InvalidFormatException with the specified message and inner exception.
    /// <para>使用指定的错误信息和内部异常初始化 InvalidFormatException 的新实例。</para>
    /// </summary>
    public InvalidFormatException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class UnsupportedVersionException(ulong major, ulong minor, ulong patch) : NtpiException($"Unsupported NTPI version: {major}.{minor}.{patch}. No encryption keys are registered for this version.")
{
}

public sealed class IntegrityVerificationException : NtpiException
{
    /// <summary>
    /// Initializes a new instance of IntegrityVerificationException with the specified message.
    /// <para>使用指定的错误信息初始化 IntegrityVerificationException 的新实例。</para>
    /// </summary>
    public IntegrityVerificationException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of IntegrityVerificationException with the specified message and inner exception.
    /// <para>使用指定的错误信息和内部异常初始化 IntegrityVerificationException 的新实例。</para>
    /// </summary>
    public IntegrityVerificationException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class Lzma2CompressionException : NtpiException
{
    /// <summary>
    /// Initializes a new instance of Lzma2CompressionException with the specified message.
    /// <para>使用指定的错误信息初始化 Lzma2CompressionException 的新实例。</para>
    /// </summary>
    public Lzma2CompressionException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of Lzma2CompressionException with the specified message and inner exception.
    /// <para>使用指定的错误信息和内部异常初始化 Lzma2CompressionException 的新实例。</para>
    /// </summary>
    public Lzma2CompressionException(string message, Exception innerException) : base(message, innerException) { }
}