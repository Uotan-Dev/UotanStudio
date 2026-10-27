using System.Globalization;
using System.Text;
using UotanStudio.Comm.Contracts;
using HdcDeviceServices = UotanStudio.Comm.Protocols.Hdc.HdcDeviceServices;

namespace UotanStudio.Mirroring;

/// <summary>
/// Runs one device shell command through <see cref="IHdcDeviceSession.ExecuteCommand"/> and reports its exit
/// status, because the HDC shell channel carries no process exit code.
/// <para>经 <see cref="IHdcDeviceSession.ExecuteCommand"/> 执行一条设备 shell 命令并报告其退出状态，因为 HDC
/// shell 通道不携带进程退出码。</para>
/// </summary>
/// <remarks>
/// The command text is extended with an <c>echo</c> of a private marker followed by <c>$?</c>, so the device
/// shell itself serializes the exit status into stdout; this works on direct and server-backed HDC routes
/// alike, including routes that merge streams and report <c>-1</c> as the transport exit code. The last marker
/// occurrence wins, so a command whose own output contains the marker text cannot confuse the parse.
/// <para>命令文本会被追加一条 <c>echo</c>：私有标记加 <c>$?</c>，由设备 shell 自己把退出状态写进 stdout；直连与
/// server 路由（包括合并两路输出并把传输退出码报为 <c>-1</c> 的路由）都适用。解析取最后一次出现的标记，
/// 命令自身输出中包含标记文本也不会干扰解析。</para>
/// </remarks>
internal static class OhscrcpyShell
{
    /// <summary>Marker echoed after the command to expose the device exit status.
    /// <para>命令后回显、用于暴露设备退出状态的标记。</para></summary>
    private const string ExitMarker = "__UOTAN_HDC_MIRROR_EXIT__";

    /// <summary>Upper bound of device output quoted in an exception message.
    /// <para>异常消息中引用的设备输出上限。</para></summary>
    private const int MaximumDiagnosticLength = 4096;

    /// <summary>Runs a shell command and returns its trimmed standard output.
    /// <para>执行一条 shell 命令并返回去除首尾空白后的标准输出。</para>
    /// </summary>
    /// <param name="session">Session whose route executes the command; must not be <see langword="null"/>.
    /// <para>执行该命令的会话路由；不得为 <see langword="null"/>。</para></param>
    /// <param name="command">Command text without a trailing separator; blank text is rejected.
    /// <para>不带结尾分隔符的命令文本；空白文本会被拒绝。</para></param>
    /// <param name="timeoutMs">Overall response deadline in milliseconds; must be positive.
    /// <para>总体响应时限（毫秒）；必须为正。</para></param>
    /// <returns>The device output without the exit marker line and without surrounding whitespace.
    /// <para>去掉退出标记行与首尾空白后的设备输出。</para></returns>
    /// <exception cref="IOException">The command exited non-zero, or the device returned no readable exit marker.
    /// <para>命令以非零状态结束，或设备没有返回可解析的退出标记。</para></exception>
    internal static string Run(IHdcDeviceSession session, string command, int timeoutMs = 15000)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutMs, 1);
        var result = session.ExecuteCommand(command + "; echo " + ExitMarker + "$?", timeoutMs);
        var output = Encoding.UTF8.GetString(result.Stdout);
        var markerIndex = output.LastIndexOf(ExitMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new IOException(
                $"The device shell command returned no exit marker. 设备 shell 命令没有返回退出标记。Output: {Truncate(output)}");
        }

        var body = output[..markerIndex].Trim();
        var tail = output.AsSpan(markerIndex + ExitMarker.Length);
        var digits = 0;
        while (digits < tail.Length && char.IsAsciiDigit(tail[digits]))
        {
            digits++;
        }

        if (!int.TryParse(tail[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out var exitCode))
        {
            throw new IOException(
                $"The device shell command returned an unreadable exit code. 设备 shell 命令的退出码无法解析。Output: {Truncate(output)}");
        }

        if (exitCode == 0)
        {
            return body;
        }

        throw new IOException(body.Length == 0
            ? $"The device shell command failed with exit code {exitCode}. 设备 shell 命令执行失败，退出码 {exitCode}。"
            : $"The device shell command failed with exit code {exitCode}. 设备 shell 命令执行失败，退出码 {exitCode}。Output: {Truncate(body)}");
    }

    /// <summary>Quotes one argument for the device shell. <para>为设备 shell 引用一个参数。</para></summary>
    /// <param name="value">Raw argument text; must not contain a NUL byte.
    /// <para>原始参数文本；不得包含 NUL 字节。</para></param>
    /// <returns>The value wrapped in single quotes with embedded quotes escaped for the device shell.
    /// <para>用单引号包裹、并按设备 shell 规则转义内嵌引号后的值。</para></returns>
    internal static string Quote(string value) => HdcDeviceServices.QuoteShellArgument(value);

    private static string Truncate(string value) =>
        value.Length <= MaximumDiagnosticLength ? value : value[..MaximumDiagnosticLength] + "…";
}