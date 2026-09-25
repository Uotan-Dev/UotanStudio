using System.Globalization;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Optional host-side file callbacks. Must be assigned before
    /// InitializeXmlHardwareAsync to take effect; null keeps
    /// the fail-closed behaviour where device-initiated file operations fault the session.
    /// <para>可选的主机侧文件回调。必须在 InitializeXmlHardwareAsync
    /// 之前赋值才生效；为 null 时保持 fail-closed——设备发起的文件操作使会话 fault。</para>
    /// </summary>
    public IMtkXmlHostFileStore? HostFileStore { get; set; }

    private static bool IsXmlFileRequest(MtkXmlMessage message) =>
        message.Command is "CMD:DOWNLOAD-FILE" or "CMD:UPLOAD-FILE" or "CMD:FILE-SYS-OPERATION";

    /// <summary>
    /// Services one device-initiated file request through <see cref="HostFileStore"/>, using
    /// exactly the bounded acknowledgement/packet dances of the corresponding host-initiated
    /// flows. Any unresolvable request throws (faulting the session via the caller).
    /// <para>经 <see cref="HostFileStore"/> 服务一次设备发起的文件请求，使用与主机发起流程
    /// 完全相同的有界应答/分包舞步。任何无法解析的请求都会抛出（经调用方使会话 fault）。</para>
    /// </summary>
    private async Task<bool> TryServiceXmlFileRequestOwnedAsync(MtkXmlChannel channel, MtkXmlMessage request,
        CancellationToken token)
    {
        var store = HostFileStore;
        if (!IsXmlFileRequest(request))
        {
            return false;
        }

        if (store is null)
        {
            throw new MtkProtocolException($"The XML DA requested {request.Command} without a host file store.");
        }

        switch (request.Command)
        {
            case "CMD:FILE-SYS-OPERATION":
                {
                    var arguments = request.Arguments;
                    if (!arguments.TryGetValue("key", out var key) || key != "FILE-SIZE" ||
                        !arguments.TryGetValue("file_path", out var path))
                    {
                        throw new MtkProtocolException("Only the FILE-SIZE file-system operation is supported.");
                    }

                    var size = await store.GetSizeAsync(path, token).ConfigureAwait(false)
                        ?? throw new MtkProtocolException($"The host file store has no size for '{path}'.");
                    await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                    await channel.AcknowledgeValueAsync((ulong)size, token).ConfigureAwait(false);
                    return true;
                }

            case "CMD:DOWNLOAD-FILE":
                {
                    var arguments = request.Arguments;
                    if (!arguments.TryGetValue("checksum", out var checksum) || checksum != "CHK_NO" ||
                        !arguments.TryGetValue("packet_length", out var packet) ||
                        !TryXmlHexLength(packet, MtkXmlChannel.MaximumBinaryPayload, out var packetLength))
                    {
                        throw new MtkProtocolException("The XML download request does not match the bounded transfer profile.");
                    }

                    var tokenName = arguments.TryGetValue("source_file", out var source) ? source
                        : arguments.TryGetValue("target_file", out var target) ? target
                        : throw new MtkProtocolException("The XML download request carries no file token.");
                    var (content, length) = await store.OpenReadAsync(tokenName, token).ConfigureAwait(false)
                        ?? throw new MtkProtocolException($"The host file store refused '{tokenName}'.");
                    using (content)
                    {
                        await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                        await SendXmlSourceOwnedAsync(channel, content, length, packetLength, null, token).ConfigureAwait(false);
                    }

                    return true;
                }

            case "CMD:UPLOAD-FILE":
                {
                    var arguments = request.Arguments;
                    if (arguments.Count != 4
                        || !arguments.TryGetValue("checksum", out var checksum) || checksum != "CHK_NO"
                        || !arguments.TryGetValue("info", out var info) || info.Length == 0
                        || !arguments.TryGetValue("target_file", out var target)
                        || !arguments.TryGetValue("packet_length", out var packet)
                        || !TryXmlHexLength(packet, MtkXmlChannel.MaximumBinaryPayload, out var packetLength))
                    {
                        throw new MtkProtocolException("The XML upload request does not match the bounded transfer profile.");
                    }

                    var sink = await store.OpenWriteAsync(target, 0, token).ConfigureAwait(false)
                        ?? throw new MtkProtocolException($"The host file store refused the upload of '{target}'.");
                    using (sink)
                    {
                        await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                        var declared = await channel.ReceiveTextAsync(token).ConfigureAwait(false);
                        if (!TryParseXmlDeclaredLength(declared, out var length))
                        {
                            throw new MtkProtocolException("The XML DA did not declare the upload length.");
                        }

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
                                throw new MtkProtocolException("The XML upload delivered an empty data frame.");
                            }

                            await sink.WriteAsync(payload, token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                            var status = await channel.ReceiveTextAsync(token).ConfigureAwait(false);
                            transferred += payload.Length;
                            if (status == "OK")
                            {
                                await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                            }
                            else if (transferred != length)
                            {
                                throw new MtkProtocolException("The XML upload ended before the declared length.");
                            }
                        }
                    }

                    return true;
                }

            default:
                return false;
        }
    }

    private static bool TryParseXmlDeclaredLength(string text, out long length)
    {
        length = 0;
        return text.StartsWith("OK@0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(text[5..].TrimEnd('\0'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out length)
            && length > 0;
    }
}