using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Requests XML device-side erase of an explicitly selected, aligned physical range.
    /// <para>请求 XML 设备端擦除显式选定的、对齐的物理范围。</para></summary>
    /// <remarks>
    /// The caller must authorize this destructive operation. This profile requires initial OK,
    /// bounded progress handling, END/OK, START and the trailing OK consumed by the reference erase
    /// wrapper. No timeout probing, retry, rollback, zero-fill or secure-erasure guarantee is provided.
    /// Cancellation cannot stop an erase already accepted by the agent.
    /// </remarks>
    /// <param name="regionName">An exact XML physical-region token from the discovered geometry, excluding RPMB.
    /// <para>来自已发现几何信息的精确 XML 物理区域令牌，不含 RPMB。</para></param>
    /// <param name="offset">Nonnegative block-aligned byte offset.
    /// <para>非负、块对齐的字节偏移。</para></param>
    /// <param name="length">Positive block-aligned byte count bounded by reported capacity.
    /// <para>正数、块对齐的字节数，受上报容量限制。</para></param>
    /// <param name="timeoutMs">Cooperative whole-operation deadline, from one millisecond through 24 hours.
    /// <para>整次操作的协作式截止时间，范围从 1 毫秒到 24 小时。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and the exchange; cannot undo device-side erase.
    /// <para>用于等待与交换的取消令牌；无法撤销已发生的设备端擦除。</para></param>
    /// <returns>A task that completes when the erase exchange is acknowledged with OK.
    /// <para>在擦除交换以 OK 确认后完成的异步任务。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeoutMs"/> is out of bounds.
    /// <para><paramref name="timeoutMs"/> 越界。</para></exception>
    /// <exception cref="InvalidOperationException">Storage geometry is unknown, the range is invalid, or no XML channel is attached.
    /// <para>存储几何信息未知、范围无效，或未附加 XML 通道。</para></exception>
    /// <exception cref="MtkProtocolException">The device rejected the erase command.
    /// <para>设备拒绝了擦除命令。</para></exception>
    /// <exception cref="TimeoutException">The deadline expired; the device may still be erasing and must not be retried blindly.
    /// <para>截止时间到期；设备可能仍在擦除，不得盲目重试。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled waiting or the exchange.
    /// <para>调用方取消了等待或交换。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task EraseXmlStorageAsync(string regionName, long offset, long length,
        int timeoutMs = 120000, CancellationToken cancellationToken = default)
    {
        ValidateXmlSetupTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EraseXmlStorageOwnedAsync(regionName, offset, length, timeoutMs, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task EraseXmlStorageOwnedAsync(string regionName, long offset, long length, int timeoutMs,
        CancellationToken cancellationToken)
    {
        RequireState(MtkSessionState.XmlStorageGeometryKnown);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = XmlStorage ?? throw new InvalidOperationException("XML storage has not been discovered.");
        storage.ValidateRange(regionName, offset, length);
        RequireXmlOrdinaryRegionSnapshot(regionName);
        var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
        var command = new XElement("da", new XElement("version", "1.0"), new XElement("command", "CMD:ERASE-FLASH"),
            new XElement("arg", new XElement("partition", regionName), new XElement("offset", $"0x{offset:x}"),
                new XElement("length", $"0x{length:x}")));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        try
        {
            await SendXmlControlCommandOwnedAsync(channel, XmlHostCapabilities(download: false), token).ConfigureAwait(false);
            await SendXmlControlCommandOwnedAsync(channel, command, token).ConfigureAwait(false);
            await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            FaultXml();
            throw new TimeoutException("The XML erase deadline expired; the device may still be erasing and must not be retried blindly.", error);
        }
        catch
        {
            FaultXml();
            throw;
        }
    }
}