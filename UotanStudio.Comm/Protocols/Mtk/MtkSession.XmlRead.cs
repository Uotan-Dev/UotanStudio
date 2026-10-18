using System.Globalization;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Reads an explicitly selected, aligned XML physical-storage range to a caller-owned stream.
    /// <para>将显式选定的、对齐的 XML 物理存储范围读取到调用方拥有的流中。</para></summary>
    /// <remarks>The destination may contain partial bytes after failure. No padding, clamping,
    /// automatic authentication or retry is performed. Device authorization remains enforced by the
    /// agent. Full byte progress does not imply the final START handshake or durable file publication.
    /// CHK_NO callbacks do not provide cryptographic integrity or independently verified storage
    /// identity.</remarks>
    /// <param name="regionName">An exact XML physical-region token from the discovered geometry, excluding RPMB.
    /// <para>来自已发现几何信息的精确 XML 物理区域令牌，不含 RPMB。</para></param>
    /// <param name="offset">Nonnegative block-aligned byte offset.<para>非负、块对齐的字节偏移。</para></param>
    /// <param name="length">Positive block-aligned byte count, bounded by reported capacity.
    /// <para>正数、块对齐的字节数，受上报容量限制。</para></param>
    /// <param name="destination">Writable output positioned at the intended location; remains caller-owned.
    /// <para>定位在预期位置的可写输出流；所有权仍属于调用方。</para></param>
    /// <param name="timeoutMs">Cooperative whole-operation deadline, from one millisecond through 24 hours.
    /// <para>整次操作的协作式截止时间，范围从 1 毫秒到 24 小时。</para></param>
    /// <param name="progress">Cumulative bytes delivered to the destination and acknowledged in the exchange.
    /// <para>已交付给目标并在交换中被确认的累计字节数。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting, negotiation, reception and completion.
    /// <para>用于等待、协商、接收与完成阶段的取消令牌。</para></param>
    /// <returns>A task that completes after every selected byte was written and the final START was acknowledged.
    /// <para>在全部选定字节写入完成且最终 START 被确认后完成的异步任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.
    /// <para><paramref name="destination"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not writable.
    /// <para><paramref name="destination"/> 不可写。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeoutMs"/> is out of bounds.
    /// <para><paramref name="timeoutMs"/> 越界。</para></exception>
    /// <exception cref="InvalidOperationException">Storage geometry is unknown, no XML channel is attached, or the session stage is wrong.
    /// <para>存储几何信息未知、未附加 XML 通道，或会话阶段不符。</para></exception>
    /// <exception cref="MtkProtocolException">The exchange failed, the declared length mismatched, or a data frame arrived empty or late.
    /// <para>交换失败、声明长度不匹配，或收到空/超前的数据帧。</para></exception>
    /// <exception cref="TimeoutException">The deadline expired; the session faults and must be reconnected before continuing.
    /// <para>截止时间到期；会话进入 fault 状态，续操作前必须重连。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task ReadXmlStorageAsync(string regionName, long offset, long length, Stream destination,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The XML storage destination is not writable.", nameof(destination));
        }
        ValidateXmlSetupTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReadXmlStorageOwnedAsync(regionName, offset, length, destination, timeoutMs, progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task ReadXmlStorageOwnedAsync(string regionName, long offset, long length, Stream destination,
        int timeoutMs, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        RequireState(MtkSessionState.XmlStorageGeometryKnown);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = XmlStorage ?? throw new InvalidOperationException("XML storage has not been discovered.");
        storage.ValidateRange(regionName, offset, length);
        RequireXmlOrdinaryRegionSnapshot(regionName);
        var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        var command = new XElement("da", new XElement("version", "1.0"), new XElement("command", "CMD:READ-FLASH"),
            new XElement("arg", new XElement("partition", regionName), new XElement("offset", $"0x{offset:x}"),
                new XElement("length", $"0x{length:x}"), new XElement("target_file", "ROM_0")));
        try
        {
            await SendXmlControlCommandOwnedAsync(channel, XmlHostCapabilities(download: false, upload: true), token).ConfigureAwait(false);
            await channel.SendCommandAsync(command, token).ConfigureAwait(false);
            await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
            var request = await ReceiveXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
            var packetLength = ValidateXmlUploadRequest(request, "ROM_0", MtkXmlChannel.MaximumBinaryPayload);
            await channel.AcknowledgeAsync(token).ConfigureAwait(false);
            RequireXmlReadLength(await channel.ReceiveTextAsync(token).ConfigureAwait(false), length);
            await channel.AcknowledgeAsync(token).ConfigureAwait(false);
            await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
            await channel.AcknowledgeAsync(token).ConfigureAwait(false);
            long transferred = 0;
            while (transferred < length)
            {
                var limit = (int)Math.Min(packetLength, length - transferred);
                var payload = await channel.ReceivePayloadAsync(limit, token).ConfigureAwait(false);
                if (payload.Length == 0)
                {
                    throw new MtkProtocolException("The XML storage read returned an empty data frame.");
                }
                ThrowIfDisposed();
                await destination.WriteAsync(payload, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                var status = await channel.ReceiveTextAsync(token).ConfigureAwait(false);
                transferred += payload.Length;
                if (status == "OK")
                {
                    await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                }
                else if (transferred == length && !status.StartsWith("ERR!", StringComparison.Ordinal))
                {
                    var end = ParseXmlMessageOwned(status);
                    await AcknowledgeXmlEndOwnedAsync(channel, end, token).ConfigureAwait(false);
                }
                else
                {
                    throw new MtkProtocolException("The XML storage read ended or failed before all selected bytes were received.");
                }
                progress?.Report(transferred);
            }
            await AcknowledgeXmlStartOwnedAsync(channel, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            FaultXml();
            throw new TimeoutException("The XML storage read deadline expired; reconnect before continuing.", error);
        }
        catch
        {
            FaultXml();
            throw;
        }
    }

    private static void RequireXmlReadLength(string text, long expected)
    {
        if (text.Length is < 6 or > 21 || !text.StartsWith("OK@", StringComparison.Ordinal)
            || !text.AsSpan(3, 2).Equals("0x", StringComparison.OrdinalIgnoreCase)
            || !ulong.TryParse(text.AsSpan(5), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            || value != (ulong)expected)
        {
            throw new MtkProtocolException("The XML storage read length does not match the selected range.");
        }
    }
}