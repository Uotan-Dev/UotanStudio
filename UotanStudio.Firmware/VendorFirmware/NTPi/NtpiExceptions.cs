namespace UotanStudio.Firmware.VendorFirmware.NTPi;

/// <summary>
/// Base type for the NTPI package failures raised while reading, writing, extracting or LZMA-coding a package, so
/// callers can catch one exception type instead of the concrete subtypes.
/// <para>NTPI 包在读取、写入、提取或 LZMA 编解码过程中抛出的失败基类型，调用方可只捕获这一个异常类型，
/// 无需逐个捕获具体子类。</para>
/// </summary>
public class NtpiException : Exception
{
    /// <summary>
    /// Initializes a new instance of NtpiException with the specified message.
    /// <para>使用指定的错误信息初始化 NtpiException 的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Message describing the failed package operation; it is carried as <see cref="Exception.Message"/>.
    /// <para>描述失败包操作的信息；将作为 <see cref="Exception.Message"/> 承载。</para>
    /// </param>
    public NtpiException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of NtpiException with the specified message and inner exception.
    /// <para>使用指定的错误信息和内部异常初始化 NtpiException 的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Message describing the failed package operation; it is carried as <see cref="Exception.Message"/>.
    /// <para>描述失败包操作的信息；将作为 <see cref="Exception.Message"/> 承载。</para>
    /// </param>
    /// <param name="innerException">
    /// The lower-level failure being wrapped, or <see langword="null"/> when the failure has no cause to report.
    /// <para>被包装的底层失败；没有需要报告的原因时为 <see langword="null"/>。</para>
    /// </param>
    public NtpiException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Signals that package bytes violate the NTPI structure: a bad magic, a region or file range that exceeds its
/// container, a negative length field, or encoded blocks that do not fill a declared entry length.
/// <para>表示包内字节违反 NTPI 结构：魔数错误、区域或文件范围超出其容器、长度为负值，或编码块未能恰好填满声明的
/// 条目长度。</para>
/// </summary>
public sealed class InvalidFormatException : NtpiException
{
    /// <summary>
    /// Initializes a new instance of InvalidFormatException with the specified message.
    /// <para>使用指定的错误信息初始化 InvalidFormatException 的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Message naming the structure rule the input broke. <para>指出输入违反的结构规则的信息。</para>
    /// </param>
    public InvalidFormatException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of InvalidFormatException with the specified message and inner exception.
    /// <para>使用指定的错误信息和内部异常初始化 InvalidFormatException 的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Message naming the structure rule the input broke. <para>指出输入违反的结构规则的信息。</para>
    /// </param>
    /// <param name="innerException">
    /// The underlying read or decode failure being wrapped. <para>被包装的底层读取或解码失败。</para>
    /// </param>
    public InvalidFormatException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when no AES key set is registered for the requested NTPI version triple; the message reports the version as
/// <c>major.minor.patch</c>.
/// <para>当所请求的 NTPI 版本三元组没有登记任何 AES 密钥集时抛出；消息以 <c>major.minor.patch</c> 形式报告该版本。</para>
/// </summary>
/// <param name="major">
/// Major version component that has no registered key set. <para>没有登记密钥集的主版本号。</para>
/// </param>
/// <param name="minor">
/// Minor version component that has no registered key set. <para>没有登记密钥集的次版本号。</para>
/// </param>
/// <param name="patch">
/// Patch version component that has no registered key set. <para>没有登记密钥集的修订版本号。</para>
/// </param>
public sealed class UnsupportedVersionException(ulong major, ulong minor, ulong patch) : NtpiException($"Unsupported NTPI version: {major}.{minor}.{patch}. No encryption keys are registered for this version.")
{
}

/// <summary>
/// Signals that a decoded payload does not match what the index declares: a SHA-256 digest mismatch or an extracted
/// length that differs from the declared original length.
/// <para>表示解码后的载荷与索引声明不一致：SHA-256 摘要不匹配，或提取出的长度与声明的原始长度不同。</para>
/// </summary>
public sealed class IntegrityVerificationException : NtpiException
{
    /// <summary>
    /// Initializes a new instance of IntegrityVerificationException with the specified message.
    /// <para>使用指定的错误信息初始化 IntegrityVerificationException 的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Message reporting the expected and observed digest or length. <para>报告期望值与实测摘要或长度的信息。</para>
    /// </param>
    public IntegrityVerificationException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of IntegrityVerificationException with the specified message and inner exception.
    /// <para>使用指定的错误信息和内部异常初始化 IntegrityVerificationException 的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Message reporting the expected and observed digest or length. <para>报告期望值与实测摘要或长度的信息。</para>
    /// </param>
    /// <param name="innerException">
    /// The failure encountered while computing the digest or length. <para>计算摘要或长度过程中遇到的失败。</para>
    /// </param>
    public IntegrityVerificationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Signals that the LZMA codec could not compress or decompress a payload, including a declared plaintext or
/// compressed size that the codec output cannot satisfy.
/// <para>表示 LZMA 编解码器无法压缩或解压载荷，包括编解码输出与声明的明文或压缩长度不一致。</para>
/// </summary>
internal sealed class Lzma2CompressionException : NtpiException
{
    /// <summary>
    /// Initializes a new instance of Lzma2CompressionException with the specified message.
    /// <para>使用指定的错误信息初始化 Lzma2CompressionException 的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Message describing the compression or decompression failure. <para>描述压缩或解压失败的信息。</para>
    /// </param>
    public Lzma2CompressionException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of Lzma2CompressionException with the specified message and inner exception.
    /// <para>使用指定的错误信息和内部异常初始化 Lzma2CompressionException 的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Message describing the compression or decompression failure. <para>描述压缩或解压失败的信息。</para>
    /// </param>
    /// <param name="innerException">
    /// The codec failure being wrapped, such as a corrupt stream or an out-of-memory condition.
    /// <para>被包装的编解码器失败，例如流损坏或内存不足。</para>
    /// </param>
    public Lzma2CompressionException(string message, Exception innerException) : base(message, innerException) { }
}