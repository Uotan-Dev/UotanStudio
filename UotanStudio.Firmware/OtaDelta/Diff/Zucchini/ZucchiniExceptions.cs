namespace UotanStudio.Firmware.OtaDelta.Diff.Zucchini;

/// <summary>
/// Base type for every failure raised while reading or applying a Chromium Zucchini patch.
/// <para>读取或应用 Chromium Zucchini 补丁过程中抛出的所有失败的基类型。</para>
/// </summary>
public class ZucchiniException : Exception
{
    /// <summary>
    /// Initializes a new instance of the ZucchiniException class with a specified message.
    /// <para>使用指定消息初始化 ZucchiniException 类的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Text describing the failure; it is surfaced through <see cref="Exception.Message"/>.
    /// <para>描述失败的文本；经 <see cref="Exception.Message"/> 呈现。</para>
    /// </param>
    public ZucchiniException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ZucchiniException class with a specified message and a reference to the inner exception.
    /// <para>使用指定消息和对内部异常的引用初始化 ZucchiniException 类的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Text describing the failure; it is surfaced through <see cref="Exception.Message"/>.
    /// <para>描述失败的文本；经 <see cref="Exception.Message"/> 呈现。</para>
    /// </param>
    /// <param name="innerException">
    /// The failure that triggered this one, or <see langword="null"/> when there is none.
    /// <para>触发本次失败的异常，无则为 <see langword="null"/>。</para>
    /// </param>
    public ZucchiniException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when a Zucchini container violates its own layout rules: bad magic or version, a truncated stream, an
/// element range outside the images, leftover patch data, or a reference pool inside a NoOp element.
/// <para>当 Zucchini 容器违反自身布局规则时抛出：魔数或版本不符、流被截断、元素范围越出镜像、补丁数据有残留，
/// 或 NoOp 元素中含引用池。</para>
/// </summary>
internal class ZucchiniFormatException : ZucchiniException
{
    /// <summary>
    /// Initializes a new instance of the ZucchiniFormatException class with a specified message.
    /// <para>使用指定消息初始化 ZucchiniFormatException 类的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Text naming the layout rule that was broken; it is surfaced through <see cref="Exception.Message"/>.
    /// <para>指明被破坏的布局规则的文本；经 <see cref="Exception.Message"/> 呈现。</para>
    /// </param>
    public ZucchiniFormatException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ZucchiniFormatException class with a specified message and a reference to the inner exception.
    /// <para>使用指定消息和对内部异常的引用初始化 ZucchiniFormatException 类的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Text naming the layout rule that was broken; it is surfaced through <see cref="Exception.Message"/>.
    /// <para>指明被破坏的布局规则的文本；经 <see cref="Exception.Message"/> 呈现。</para>
    /// </param>
    /// <param name="innerException">
    /// The failure that triggered this one, or <see langword="null"/> when there is none.
    /// <para>触发本次失败的异常，无则为 <see langword="null"/>。</para>
    /// </param>
    public ZucchiniFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when the CRC-32 stored in a Zucchini header disagrees with the old or the new image, which means the
/// inputs do not match the pair the patch was created from.
/// <para>当 Zucchini 头部记录的 CRC-32 与旧镜像或新镜像不一致时抛出，说明输入与该补丁创建时所用的镜像对不匹配。</para>
/// </summary>
internal class ZucchiniChecksumException : ZucchiniException
{
    /// <summary>
    /// Initializes a new instance of the ZucchiniChecksumException class with a specified message.
    /// <para>使用指定消息初始化 ZucchiniChecksumException 类的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Text carrying the expected and found digests; it is surfaced through <see cref="Exception.Message"/>.
    /// <para>包含期望值与实际值摘要的文本；经 <see cref="Exception.Message"/> 呈现。</para>
    /// </param>
    public ZucchiniChecksumException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ZucchiniChecksumException class with a specified message and a reference to the inner exception.
    /// <para>使用指定消息和对内部异常的引用初始化 ZucchiniChecksumException 类的新实例。</para>
    /// </summary>
    /// <param name="message">
    /// Text carrying the expected and found digests; it is surfaced through <see cref="Exception.Message"/>.
    /// <para>包含期望值与实际值摘要的文本；经 <see cref="Exception.Message"/> 呈现。</para>
    /// </param>
    /// <param name="innerException">
    /// The failure that triggered this one, or <see langword="null"/> when there is none.
    /// <para>触发本次失败的异常，无则为 <see langword="null"/>。</para>
    /// </param>
    public ZucchiniChecksumException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when a Zucchini element declares an executable type other than the supported NoOp element, so its
/// equivalence data would have to be interpreted by a model this applier does not implement.
/// <para>当 Zucchini 元素声明的类型不是受支持的 NoOp 元素时抛出，因为其等价数据需要由本应用器未实现的模型来解释。</para>
/// </summary>
internal class ZucchiniUnsupportedExecutableTypeException : ZucchiniException
{
    /// <summary>The executable-type identifier read from the element header.
    /// <para>从元素头部读出的可执行类型标识。</para></summary>
    /// <value>
    /// The raw little-endian four-character code read from the patch, which any value other than the supported
    /// NoOp code <see cref="ZucchiniExecutableType.Raw"/> rejects.
    /// <para>从补丁中读出的原始小端四字符码；除受支持的 NoOp 码 <see cref="ZucchiniExecutableType.Raw"/> 外一律拒绝。</para>
    /// </value>
    public uint ExeType { get; }

    /// <summary>Offset in the new image where the rejected element starts.
    /// <para>被拒绝元素在新镜像中的起始偏移。</para></summary>
    /// <value>
    /// Zero-based byte offset into the destination buffer, identical to the element's declared destination offset.
    /// <para>目标缓冲区中从零开始的字节偏移，与该元素声明的目标偏移一致。</para>
    /// </value>
    public uint Offset { get; }

    /// <summary>
    /// Initializes a new instance of the ZucchiniUnsupportedExecutableTypeException class with the executable type value and the element's new-image offset.
    /// <para>使用可执行类型数值与元素的新镜像偏移初始化 ZucchiniUnsupportedExecutableTypeException 类的新实例。</para>
    /// </summary>
    /// <param name="exeType">
    /// Executable-type identifier that this applier cannot handle; it is also stored in <see cref="ExeType"/>.
    /// <para>本应用器无法处理的可执行类型标识，同时存入 <see cref="ExeType"/>。</para>
    /// </param>
    /// <param name="offset">
    /// Element start offset in the new image; it is also stored in <see cref="Offset"/> and named in the message.
    /// <para>元素在新镜像中的起始偏移，同时存入 <see cref="Offset"/> 并写入消息文本。</para>
    /// </param>
    public ZucchiniUnsupportedExecutableTypeException(uint exeType, uint offset)
        : base(
            $"Zucchini patch element at new offset {offset} uses executable type {exeType}; only NoOp elements are supported. " +
            $"位于新镜像偏移 {offset} 的 Zucchini 补丁元素使用可执行类型 {exeType}；仅支持 NoOp 元素。")
    {
        ExeType = exeType;
        Offset = offset;
    }
}