namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Exception thrown when an EDL protocol operation fails.
/// <para>EDL 协议操作失败时抛出的异常。</para>
/// </summary>
internal class EdlException : Exception
{
    /// <summary>
    /// Gets the error code.
    /// <para>获取错误码。</para>
    /// </summary>
    public EdlErrorCode ErrorCode { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="EdlException"/> class.
    /// <para>初始化 <see cref="EdlException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="errorCode">The error code. 错误码。</param>
    /// <param name="message">The error message. 错误消息。</param>
    public EdlException(EdlErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EdlException"/> class.
    /// <para>初始化 <see cref="EdlException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="errorCode">The error code. 错误码。</param>
    /// <param name="message">The error message. 错误消息。</param>
    /// <param name="innerException">The inner exception. 内部异常。</param>
    public EdlException(EdlErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EdlException"/> class with a default message.
    /// <para>使用默认消息初始化 <see cref="EdlException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="errorCode">The error code. 错误码。</param>
    public EdlException(EdlErrorCode errorCode)
        : base(GetDefaultMessage(errorCode))
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Returns the default message for a given error code.
    /// <para>返回给定错误码的默认消息。</para>
    /// </summary>
    /// <param name="code">The error code. 错误码。</param>
    /// <returns>The default message. 默认消息。</returns>
    private static string GetDefaultMessage(EdlErrorCode code) => code switch
    {
        EdlErrorCode.Success => "Operation completed successfully.",
        EdlErrorCode.InvalidParameter => "Invalid parameter provided.",
        EdlErrorCode.NotSupported => "Operation not supported.",
        EdlErrorCode.TransportTimeout => "Transport operation timed out.",
        EdlErrorCode.ReadPortTimeout => "Read operation timed out.",
        EdlErrorCode.WritePortTimeout => "Write operation timed out.",
        EdlErrorCode.NotInitialized => "Component not initialized.",
        EdlErrorCode.FileIO => "File I/O error.",
        EdlErrorCode.NoMemory => "Insufficient memory.",
        EdlErrorCode.TargetNak => "Target returned NAK.",
        EdlErrorCode.SaharaProtocolError => "Sahara protocol error.",
        EdlErrorCode.FirehoseProtocolError => "Firehose protocol error.",
        EdlErrorCode.ConfigureFailed => "Firehose configure negotiation failed.",
        EdlErrorCode.ProgramFailed => "Program operation failed.",
        EdlErrorCode.ReadFailed => "Read operation failed.",
        EdlErrorCode.EraseFailed => "Erase operation failed.",
        EdlErrorCode.ConnectionLost => "Connection to device lost.",
        EdlErrorCode.OperationCancelled => "Operation was cancelled.",
        EdlErrorCode.DeviceNotFound => "EDL device not found.",
        EdlErrorCode.PeekFailed => "Peek (memory read) operation failed.",
        EdlErrorCode.PokeFailed => "Poke (memory write) operation failed.",
        EdlErrorCode.MemoryCommandFailed => "Memory command failed.",
        EdlErrorCode.UfsProvisioningFailed => "UFS provisioning operation failed.",
        EdlErrorCode.OnTargetVerificationFailed => "On-target verification failed.",
        EdlErrorCode.SetActivePartitionFailed => "Setting the active A/B partition failed.",
        EdlErrorCode.XmlParseError => "XML parsing failed.",
        _ => $"Unknown error code: {code}"
    };
}

/// <summary>
/// Exception thrown when a Sahara protocol operation fails.
/// <para>Sahara 协议操作失败时抛出的异常。</para>
/// </summary>
internal class SaharaException : EdlException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SaharaException"/> class.
    /// <para>初始化 <see cref="SaharaException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message. 错误消息。</param>
    public SaharaException(string message) : base(EdlErrorCode.SaharaProtocolError, message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SaharaException"/> class.
    /// <para>初始化 <see cref="SaharaException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message. 错误消息。</param>
    /// <param name="inner">The inner exception. 内部异常。</param>
    public SaharaException(string message, Exception inner) : base(EdlErrorCode.SaharaProtocolError, message, inner) { }
}

/// <summary>
/// Exception thrown when a Firehose protocol operation fails.
/// <para>Firehose 协议操作失败时抛出的异常。</para>
/// </summary>
internal class FirehoseException : EdlException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FirehoseException"/> class.
    /// <para>初始化 <see cref="FirehoseException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message. 错误消息。</param>
    public FirehoseException(string message) : base(EdlErrorCode.FirehoseProtocolError, message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="FirehoseException"/> class.
    /// <para>初始化 <see cref="FirehoseException"/> 类的新实例。</para>
    /// </summary>
    /// <param name="message">The error message. 错误消息。</param>
    /// <param name="inner">The inner exception. 内部异常。</param>
    public FirehoseException(string message, Exception inner) : base(EdlErrorCode.FirehoseProtocolError, message, inner) { }
}