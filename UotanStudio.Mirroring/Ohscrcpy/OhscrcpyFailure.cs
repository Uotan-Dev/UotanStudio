using System.Net.Sockets;

namespace UotanStudio.Mirroring;

/// <summary>
/// Classifies one HDC mirror startup failure into the three groups the mirror UI reports: the target is not ready
/// yet and may be retried, the device does not meet the UI Test/ABI requirements, or the session started but the
/// video stream failed. Callers prefix their own bilingual detail with the group text so a cold-start race and a
/// capability gap never read the same in the log.
/// <para>把一次 HDC 投屏启动失败分成投屏界面报告的三种：目标尚未就绪（可重试）、设备不满足 UI Test/ABI 要求、
/// 会话已启动但视频流失败。调用方在自己的中英双语细节前拼接分组文案，使冷启动竞态与能力缺口在日志中不再显示为
/// 同一句话。</para>
/// </summary>
internal static class OhscrcpyFailure
{
    /// <summary>Group text for a target that is not ready yet; the caller may retry the whole start.
    /// <para>目标尚未就绪的分组文案；调用方可以重试整个启动过程。</para></summary>
    internal const string NotReadyPrefix = "HDC mirror target is not ready yet (retryable / 可重试): ";

    /// <summary>Group text for a device that lacks the required UI Test version or capture-library ABI; retrying
    /// cannot help. <para>设备缺少所需 UI Test 版本或采集库 ABI 的分组文案；重试无济于事。</para></summary>
    internal const string CapabilityPrefix = "HDC mirror is unsupported on this target (not retryable / 不可重试): ";

    /// <summary>Group text for a session that started but whose video stream failed; retrying mirroring is the
    /// actionable next step. <para>会话已启动但视频流失败的分组文案；可行动的一步是重新开始投屏。</para></summary>
    internal const string VideoPrefix = "HDC mirror started but the video stream failed (retry mirroring / 可重试): ";

    /// <summary>Markers in a capture-extension log that prove the device could not load the pushed library.
    /// <para>采集扩展日志中能够证明设备无法加载推送库的标记。</para></summary>
    private static readonly string[] AbiMarkers =
    [
        "dlopen",
        "cannot locate symbol",
        "undefined symbol",
        "relocation",
        "libgrpc",
        "libprotobuf",
        "unsupported abi",
        "cannot find library",
    ];

    /// <summary>Upper bound of device log text quoted in a failure message.
    /// <para>失败消息中引用的设备日志文本上限。</para></summary>
    private const int MaximumSnippetLength = 600;

    /// <summary>
    /// Reports whether an error came from a connection that is not ready yet, which makes the surrounding step
    /// worth retrying; capability rejections are excluded even though they are raised as <see cref="IOException"/>.
    /// <para>判断错误是否来自尚未就绪的连接，从而使所在步骤值得重试；能力拒绝虽然同样以 <see cref="IOException"/>
    /// 抛出，但会被排除。</para>
    /// </summary>
    /// <param name="error">Any error from a forward, a socket open or an HTTP/2 request.
    /// <para>来自转发、套接字打开或 HTTP/2 请求的错误。</para></param>
    /// <returns><see langword="true"/> when the operation may be retried after a delay.
    /// <para>延迟后可以重试该操作时为 <see langword="true"/>。</para></returns>
    internal static bool IsConnectionFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (IsCapabilityFailure(error))
        {
            return false;
        }

        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is SocketException or HttpRequestException or TimeoutException or EndOfStreamException)
            {
                return true;
            }

            if (current is IOException &&
                (Contains(current.Message, "connection reset")
                    || Contains(current.Message, "forcibly closed")
                    || Contains(current.Message, "prematurely")
                    || Contains(current.Message, "did not accept the connection")
                    || Contains(current.Message, "An error occurred while sending the request")
                    || Contains(current.Message, "HTTP/2")
                    || Contains(current.Message, "connection was closed")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reports whether an error is a capability rejection rather than a transient connection problem.
    /// <para>判断错误是否为能力拒绝，而不是瞬态连接问题。</para>
    /// </summary>
    /// <param name="error">Any error raised while opening the device gRPC endpoint.
    /// <para>打开设备 gRPC 端点时抛出的错误。</para></param>
    /// <returns><see langword="true"/> for an unimplemented call or a non-gRPC peer, which retrying cannot fix.
    /// <para>调用未实现或对端不是 gRPC 时为 <see langword="true"/>；重试无法修复。</para></returns>
    internal static bool IsCapabilityFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is NotSupportedException)
            {
                return true;
            }

            if (MentionsUnimplementedStatus(current.Message) || Contains(current.Message, "UNIMPLEMENTED"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reports whether a device log excerpt proves that the pushed capture library could not be loaded, which is a
    /// capability gap rather than a startup race.
    /// <para>判断一段设备日志是否证明推送的采集库无法加载，从而属于能力缺口而不是启动竞态。</para>
    /// </summary>
    /// <param name="deviceLog">Capture-extension log text; <see langword="null"/> is treated as empty.
    /// <para>采集扩展日志文本；<see langword="null"/> 按空串处理。</para></param>
    /// <returns><see langword="true"/> when the log carries a dynamic-loader or ABI marker.
    /// <para>日志中带有动态加载或 ABI 标记时为 <see langword="true"/>。</para></returns>
    internal static bool LooksLikeAbiIncompatibility(string? deviceLog)
    {
        if (string.IsNullOrWhiteSpace(deviceLog))
        {
            return false;
        }

        foreach (var marker in AbiMarkers)
        {
            if (Contains(deviceLog, marker))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Wraps a video-request failure into the user-facing group message, quoting the capture-extension log so a
    /// loader/ABI failure is reported as unsupported instead of being retried forever.
    /// <para>把视频请求失败包装成面向用户的分组消息，并引用采集扩展日志，使加载器/ABI 失败被报告为不支持而不是被
    /// 无限重试。</para>
    /// </summary>
    /// <param name="error">The failure raised while opening or reading the video stream.
    /// <para>打开或读取视频流时抛出的失败。</para></param>
    /// <param name="deviceLog">Capture-extension log text, or <see langword="null"/> when unavailable.
    /// <para>采集扩展日志文本；不可用时为 <see langword="null"/>。</para></param>
    /// <returns>An unsupported-target exception for a loader/ABI failure, otherwise an IO exception that asks for a
    /// retry. <para>加载器/ABI 失败时返回不支持目标的异常，否则返回要求重试的 IO 异常。</para></returns>
    internal static Exception WrapVideoFailure(Exception error, string? deviceLog)
    {
        ArgumentNullException.ThrowIfNull(error);
        var log = Snippet(deviceLog);
        if (LooksLikeAbiIncompatibility(deviceLog))
        {
            return new NotSupportedException(
                CapabilityPrefix
                + "the pushed capture library could not be loaded by this device's UI Test runtime: "
                + $"{error.Message}. Replace the ohos/device capture library with a build that matches this system "
                + "version, or mirror from a supported device. Capture log: " + log
                + CapabilityPrefix
                + $"推送到设备的采集库无法被该设备的 UI Test 运行时加载：{error.Message}。请把 ohos/device 下的采集库换成"
                + "与该系统版本匹配的版本，或改用受支持的设备投屏。采集日志：" + log,
                error);
        }

        return new IOException(
            VideoPrefix
            + $"the gRPC video endpoint is not answering: {error.Message}. The extension may have just started; "
            + "start mirroring again. Capture log: " + log
            + VideoPrefix
            + $"视频 gRPC 端点没有响应：{error.Message}。采集扩展可能刚刚启动，请再次开始投屏。采集日志：" + log,
            error);
    }

    /// <summary>
    /// Bounds one log excerpt for a failure message and renders an empty excerpt as <c>(empty)</c>.
    /// <para>为失败消息裁剪日志片段，并把握空片段渲染为 <c>(empty)</c>。</para>
    /// </summary>
    /// <param name="text">Excerpt source, possibly <see langword="null"/>.
    /// <para>片段来源，可能为 <see langword="null"/>。</para></param>
    /// <returns>The single-line excerpt. <para>单行片段。</para></returns>
    internal static string Snippet(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        var singleLine = text.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= MaximumSnippetLength
            ? singleLine
            : singleLine[..MaximumSnippetLength] + "…";
    }

    /// <summary>
    /// Reports whether an HTTP status means the device does not implement the requested gRPC call, which is a
    /// capability gap rather than a transient failure.
    /// <para>判断某个 HTTP 状态是否表示设备没有实现所请求的 gRPC 调用，从而属于能力缺口而不是瞬态失败。</para>
    /// </summary>
    /// <param name="statusCode">HTTP status returned by the loopback gRPC endpoint.
    /// <para>回环 gRPC 端点返回的 HTTP 状态。</para></param>
    /// <returns><see langword="true"/> for 404, 405 and 501.
    /// <para>对 404、405 与 501 返回 <see langword="true"/>。</para></returns>
    internal static bool IsUnimplementedStatus(int statusCode) => statusCode is 404 or 405 or 501;

    private static bool MentionsUnimplementedStatus(string text) =>
        Contains(text, "HTTP 404") || Contains(text, "HTTP 405") || Contains(text, "HTTP 501");

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}