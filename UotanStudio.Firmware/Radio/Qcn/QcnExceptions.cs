namespace UotanStudio.Firmware.Radio.Qcn;

/// <summary>
/// Base type for Qualcomm QCN NV backup parsing, verification and patching failures thrown by this library.
/// <para>本库抛出的 Qualcomm QCN NV 备份解析、校验与补丁失败的基类型。</para>
/// </summary>
public class QcnException : Exception
{
    /// <summary>
    /// Initializes a new instance of the QcnException class with a specified error message.
    /// <para>使用指定的错误消息初始化 QcnException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    public QcnException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the QcnException class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// <para>使用指定的错误消息和对作为此异常原因的内部异常的引用，初始化 QcnException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the failure. <para>描述该失败的错误消息。</para></param>
    /// <param name="innerException">The exception that caused this failure. <para>导致此异常的内部异常。</para></param>
    public QcnException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when an input is not a Qualcomm QCN NV backup or its NV record stream violates the format.
/// <para>当输入不是 Qualcomm QCN NV 备份，或其 NV 记录流违反格式时抛出。</para>
/// </summary>
public class InvalidQcnFormatException : QcnException
{
    /// <summary>
    /// Initializes a new instance of the InvalidQcnFormatException class with a specified error message.
    /// <para>使用指定的错误消息初始化 InvalidQcnFormatException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the defect. <para>描述该缺陷的错误消息。</para></param>
    public InvalidQcnFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Thrown when the items supplied to <see cref="QcnWriter.PatchAsync"/> do not originate from the
/// file being patched, so a safe in-place patch cannot be computed.
/// <para>当提供给 <see cref="QcnWriter.PatchAsync"/> 的条目并非源自被补丁的文件，无法安全计算原位
/// 补丁时抛出。</para>
/// </summary>
public class QcnPatchMismatchException : QcnException
{
    /// <summary>
    /// Initializes a new instance of the QcnPatchMismatchException class with a specified error message.
    /// <para>使用指定的错误消息初始化 QcnPatchMismatchException 类的新实例。</para>
    /// </summary>
    /// <param name="message">Error message describing the mismatch. <para>描述该不匹配的错误消息。</para></param>
    public QcnPatchMismatchException(string message)
        : base(message)
    {
    }
}