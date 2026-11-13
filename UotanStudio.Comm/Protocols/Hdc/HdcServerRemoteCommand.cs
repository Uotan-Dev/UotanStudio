using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Builds the command text of a remote-client task: <c>&lt;command&gt; remote -cwd "&lt;host cwd&gt;" &lt;arguments&gt;</c>.
/// <para>构建 remote-client 任务的命令文本：<c>&lt;command&gt; remote -cwd "&lt;宿主工作目录&gt;" &lt;参数&gt;</c>。</para>
/// </summary>
internal static class HdcServerRemoteCommand
{
    /// <summary>
    /// Upstream marker that tells the server the client performs the local file I/O; the server strips it and echoes
    /// the task back to the client. <para>告知 server 由客户端执行本地文件 IO 的上游标记；server 会移除该标记并把任务回送给客户端。</para>
    /// </summary>
    private const string RemoteMarker = "remote";

    /// <summary>
    /// Builds one remote-client command line with the supplied host directory appended as the <c>-cwd</c>
    /// option. <para>构建一条 remote-client 命令行，并把给定宿主目录追加为 <c>-cwd</c> 选项。</para>
    /// </summary>
    /// <param name="command">Command words such as <c>file send</c>. <para>命令词，例如 <c>file send</c>。</para></param>
    /// <param name="hostDirectory">Host directory broadcast as the <c>-cwd</c> option; callers pass the directory of
    /// the local file involved so the command never depends on the process working directory. <see langword="null"/>
    /// or blank falls back to the process working directory.
    /// <para>作为 <c>-cwd</c> 选项广播的宿主目录；调用方传入本次本地文件所在目录，使命令不依赖进程工作目录。
    /// 为 <see langword="null"/> 或空白时回退到进程工作目录。</para></param>
    /// <param name="arguments">Path or option arguments appended in order; empty strings are skipped.
    /// <para>按顺序追加的路径或选项参数；空串会被跳过。</para></param>
    /// <returns>The complete command text sent NUL-terminated to the host server.
    /// <para>以 NUL 结尾发送到宿主 server 的完整命令文本。</para></returns>
    /// <exception cref="ArgumentException">An argument contains a quote or NUL character.
    /// <para>某个参数包含引号或 NUL 字符。</para></exception>
    internal static string Build(string command, string? hostDirectory, params string[] arguments)
    {
        var builder = new StringBuilder(command);
        foreach (var argument in arguments)
        {
            if (argument.Length == 0) continue;
            builder.Append(' ').Append(Quote(argument));
        }

        builder.Append(' ').Append(RemoteMarker).Append(" -cwd ").Append('"').Append(HostDirectory(hostDirectory)).Append('"');
        return builder.ToString();
    }

    /// <summary>
    /// Quotes one command argument the way the remote client expects, only when it contains whitespace.
    /// <para>按 remote 客户端约定的方式引用单个命令参数，仅在包含空白时加引号。</para>
    /// </summary>
    /// <param name="value">Argument text that must not contain a quote or NUL character.
    /// <para>参数文本；不得包含引号或 NUL 字符。</para></param>
    /// <returns>The argument, quoted when it contains whitespace. <para>包含空白时加引号的参数。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is blank, or contains a quote or NUL character.
    /// <para><paramref name="value"/> 为空，或包含引号或 NUL 字符。</para></exception>
    internal static string Quote(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('"') || value.Contains('\0'))
            throw new ArgumentException("HDC remote commands cannot carry quotes or NUL characters.", nameof(value));
        return value.Any(char.IsWhiteSpace) ? "\"" + value + "\"" : value;
    }

    private static string HostDirectory(string? requested)
    {
        var directory = string.IsNullOrWhiteSpace(requested) ? Environment.CurrentDirectory : requested;
        if (directory.Length == 0 || directory.Contains('"') || directory.Contains('\0'))
        {
            directory = Path.GetTempPath();
        }

        return directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
    }
}