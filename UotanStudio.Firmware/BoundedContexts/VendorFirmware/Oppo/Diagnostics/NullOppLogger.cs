namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Diagnostics;

/// <summary>
/// An <see cref="IOppLogger"/> that discards every message; used when diagnostics are not collected.
/// <para>丢弃所有消息的 <see cref="IOppLogger"/>；在不收集诊断信息时使用。</para>
/// </summary>
public sealed class NullOppLogger : IOppLogger
{
    /// <summary>Shared instance; the logger holds no state, so one instance serves all callers.
    /// <para>共享实例；该记录器不保存状态，一个实例即可服务所有调用方。</para></summary>
    /// <value>The single stateless <see cref="NullOppLogger"/>. <para>唯一的无状态 <see cref="NullOppLogger"/> 实例。</para></value>
    public static NullOppLogger Instance { get; } = new();

    private NullOppLogger()
    {
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    /// <param name="message">The message to discard. <para>被丢弃的消息。</para></param>
    public void Trace(string message)
    {
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    /// <param name="message">The message to discard. <para>被丢弃的消息。</para></param>
    public void Debug(string message)
    {
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    /// <param name="message">The message to discard. <para>被丢弃的消息。</para></param>
    public void Information(string message)
    {
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    /// <param name="message">The message to discard. <para>被丢弃的消息。</para></param>
    public void Warning(string message)
    {
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    /// <param name="message">The message to discard. <para>被丢弃的消息。</para></param>
    /// <param name="exception">The exception to discard. <para>被丢弃的异常。</para></param>
    public void Warning(string message, Exception exception)
    {
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    /// <param name="message">The message to discard. <para>被丢弃的消息。</para></param>
    public void Error(string message)
    {
    }

    /// <summary>
    /// Performs no operation.
    /// <para>不执行任何操作。</para>
    /// </summary>
    /// <param name="message">The message to discard. <para>被丢弃的消息。</para></param>
    /// <param name="exception">The exception to discard. <para>被丢弃的异常。</para></param>
    public void Error(string message, Exception exception)
    {
    }
}