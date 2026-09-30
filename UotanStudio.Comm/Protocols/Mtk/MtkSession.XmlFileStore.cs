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
    /// <value>The attached host file store, or null while device-initiated file operations fault the session.
    /// <para>已附加的主机文件存储；为 null 时设备发起的文件操作会使会话 fault。</para></value>
    public IMtkXmlHostFileStore? HostFileStore { get; set; }

    /// <summary>Gets or sets the byte limit for each device-initiated host file transfer.
    /// <para>获取或设置每次设备发起的主机文件传输的字节上限。</para></summary>
    /// <value>A positive explicit limit, defaulting to 64 MiB. Set before starting hardware initialization.
    /// <para>显式的正数上限，默认为 64 MiB。在开始硬件初始化之前设置。</para></value>
    public long MaximumHostFileTransferBytes
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 64 * 1024 * 1024;

    private const int MaximumXmlHostFileTokenLength = 512;

    private static bool IsXmlFileRequest(MtkXmlMessage message) =>
        message.Command is "CMD:DOWNLOAD-FILE" or "CMD:UPLOAD-FILE" or "CMD:FILE-SYS-OPERATION";

    /// <summary>
    /// Rejects the traversal shapes a device-authored host file token must never carry before it
    /// reaches <see cref="IMtkXmlHostFileStore"/>: rooted or absolute paths, drive letters,
    /// NTFS alternate data streams and other scheme-like colons, back slashes, any <c>..</c>
    /// segment, control characters and over-long names. The host store still owns the
    /// authoritative containment decision; this only removes the obvious escaping forms from the
    /// untrusted input.
    /// <para>在设备提供的主机文件令牌到达 <see cref="IMtkXmlHostFileStore"/> 之前，拒绝它绝不应携带的
    /// 越权形态：根路径或绝对路径、盘符、NTFS 备用数据流以及其他带冒号的协议式写法、反斜杠、任意
    /// <c>..</c> 片段、控制字符以及超长名称。真正的包含式判定仍由主机存储负责；此处只从不可信输入中
    /// 剔除明显的越界形式。</para>
    /// </summary>
    /// <param name="token">The device-authored file token. 设备提供的主机文件令牌。</param>
    /// <exception cref="MtkProtocolException">The token is not a bounded relative file name.
    /// <para>该令牌不是有界的相对文件名。</para></exception>
    internal static void ValidateXmlHostFileToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaximumXmlHostFileTokenLength
            || token.Contains(':')
            || token.Contains('\\')
            || token.Contains("..", StringComparison.Ordinal)
            || token.StartsWith('/')
            || Path.IsPathRooted(token)
            || token.Any(static value => char.IsControl(value)))
        {
            throw new MtkProtocolException("The XML DA host file token is not a bounded relative file name.");
        }
    }

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

                    ValidateXmlHostFileToken(path);
                    var size = await store.GetSizeAsync(path, token).ConfigureAwait(false)
                        ?? throw new MtkProtocolException($"The host file store has no size for '{path}'.");
                    if (size < 0 || size > MaximumHostFileTransferBytes)
                    {
                        throw new MtkProtocolException("The host file size exceeds the selected transfer limit.");
                    }
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
                    ValidateXmlHostFileToken(tokenName);
                    var (content, length) = await store.OpenReadAsync(tokenName, token).ConfigureAwait(false)
                        ?? throw new MtkProtocolException($"The host file store refused '{tokenName}'.");
                    using (content)
                    {
                        if (length <= 0 || length > MaximumHostFileTransferBytes || !content.CanRead
                            || (content.CanSeek && content.Length - content.Position < length))
                        {
                            throw new MtkProtocolException("The host download source exceeds its bounded transfer contract.");
                        }
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

                    ValidateXmlHostFileToken(target);
                    await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                    var declared = await channel.ReceiveTextAsync(token).ConfigureAwait(false);
                    if (!TryParseXmlDeclaredLength(declared, out var length) || length > MaximumHostFileTransferBytes)
                    {
                        throw new MtkProtocolException("The XML DA upload length is invalid or exceeds the selected transfer limit.");
                    }
                    RequireBoundedXmlPacketCount(length, packetLength);
                    var sink = await store.OpenWriteAsync(target, length, token).ConfigureAwait(false)
                        ?? throw new MtkProtocolException($"The host file store refused the upload of '{target}'.");
                    using (sink)
                    {
                        if (!sink.CanWrite)
                        {
                            throw new MtkProtocolException("The host file store returned an unwritable sink.");
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
                            else if (status != "OK!EOT" || transferred != length)
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