namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps.Logging;

/// <summary>
/// Silent <see cref="IOnePlusLogger"/> used as the fallback when a caller supplies no sink. Every
/// member returns immediately, so nothing is written, buffered or allocated for the message.
/// <para>当调用方未提供接收端时使用的静默 <see cref="IOnePlusLogger"/>。每个成员都立即返回，
/// 因此不会写入、缓冲消息，也不会为此分配内存。</para>
/// </summary>
public class NullLogger : IOnePlusLogger
{
    /// <summary>Shared stateless instance. <para>共享的无状态实例。</para></summary>
    public static readonly NullLogger Instance = new();

    /// <summary>
    /// Does nothing
    /// <para>不执行任何操作</para>
    /// </summary>
    /// <param name="message">Message text discarded without being inspected; <see langword="null"/>
    /// is accepted. <para>直接丢弃、不做检查的消息文本；允许为 <see langword="null"/>。</para></param>
    public void Trace(string message) { }

    /// <summary>
    /// Does nothing
    /// <para>不执行任何操作</para>
    /// </summary>
    /// <param name="message">Message text discarded without being inspected; <see langword="null"/>
    /// is accepted. <para>直接丢弃、不做检查的消息文本；允许为 <see langword="null"/>。</para></param>
    public void Debug(string message) { }

    /// <summary>
    /// Does nothing
    /// <para>不执行任何操作</para>
    /// </summary>
    /// <param name="message">Message text discarded without being inspected; <see langword="null"/>
    /// is accepted. <para>直接丢弃、不做检查的消息文本；允许为 <see langword="null"/>。</para></param>
    public void Information(string message) { }

    /// <summary>
    /// Does nothing
    /// <para>不执行任何操作</para>
    /// </summary>
    /// <param name="message">Message text discarded without being inspected; <see langword="null"/>
    /// is accepted. <para>直接丢弃、不做检查的消息文本；允许为 <see langword="null"/>。</para></param>
    public void Warning(string message) { }

    /// <summary>
    /// Does nothing
    /// <para>不执行任何操作</para>
    /// </summary>
    /// <param name="message">Message text discarded without being inspected; <see langword="null"/>
    /// is accepted. <para>直接丢弃、不做检查的消息文本；允许为 <see langword="null"/>。</para></param>
    public void Error(string message) { }

    /// <summary>
    /// Does nothing
    /// <para>不执行任何操作</para>
    /// </summary>
    /// <param name="message">Message text discarded without being inspected; <see langword="null"/>
    /// is accepted. <para>直接丢弃、不做检查的消息文本；允许为 <see langword="null"/>。</para></param>
    public void Critical(string message) { }

    /// <summary>
    /// Does nothing
    /// <para>不执行任何操作</para>
    /// </summary>
    /// <param name="level">Severity that would gate output; it is not read.
    /// <para>本应决定是否输出的严重级别；此处不会读取。</para></param>
    /// <param name="message">Message text discarded without being inspected; <see langword="null"/>
    /// is accepted. <para>直接丢弃、不做检查的消息文本；允许为 <see langword="null"/>。</para></param>
    public void Log(LogLevel level, string message) { }
}