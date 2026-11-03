using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace UotanStudio.Views.Editors;

/// <summary>
/// Opt-in text trace for the screen-mirror attach and presentation path.
/// <para>投屏附着与呈现路径的可选文本 trace。</para>
/// </summary>
/// <remarks>
/// Disabled unless <see cref="EnvironmentVariable"/> is set, because the render path emits many events per
/// second and an always-on sink would format and append text nobody reads. When the variable names a boolean,
/// text goes to <c>%TEMP%\uotan-mirror-trace.log</c>; any other value is taken as the destination path. Writing
/// stops after <see cref="MaximumBytes"/> so a long session cannot fill the disk.
/// <para>只有设置了 <see cref="EnvironmentVariable"/> 才会启用：呈现路径每秒产生大量事件，常开汇会格式化并追加
/// 无人读取的文本。变量值为布尔文本时写入 <c>%TEMP%\uotan-mirror-trace.log</c>，其它取值直接作为目标路径。
/// 写入超过 <see cref="MaximumBytes"/> 后停止，避免长时间会话填满磁盘。</para>
/// </remarks>
internal static class MirrorTrace
{
    /// <summary>Environment variable that turns the trace on and optionally names its file.
    /// <para>用于开启 trace 并可指定文件的环境变量。</para></summary>
    internal const string EnvironmentVariable = "UOTAN_MIRROR_TRACE";

    /// <summary>Total bytes written before the trace stops and records one truncation marker.
    /// <para>停止写入并记录一次截断标记前的累计字节数。</para></summary>
    internal const long MaximumBytes = 4L * 1024 * 1024;

    private static readonly Lock Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long _written;

    internal static readonly string? FilePath = ResolvePath();

    /// <summary>Gets whether the trace writes anything at all.
    /// <para>获取 trace 是否会写出任何内容。</para></summary>
    internal static bool Enabled => FilePath is not null;

    /// <summary>Writes one line, formatting nothing when the trace is disabled.
    /// <para>写入一行；trace 关闭时不做任何格式化。</para></summary>
    /// <param name="message">Interpolated message; no text is built when the trace is off.
    /// <para>插值消息；trace 关闭时不构建文本。</para></param>
    internal static void Write([InterpolatedStringHandlerArgument] ref MirrorTraceMessage message)
    {
        if (!message.IsEnabled)
        {
            return;
        }

        Append(message.ToStringAndClear());
    }

    /// <summary>Writes one already-rendered line.
    /// <para>写入一行已渲染完成的文本。</para></summary>
    /// <param name="message">The line text. <para>行文本。</para></param>
    internal static void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        Append(message);
    }

    private static void Append(string message)
    {
        lock (Gate)
        {
            var path = FilePath;
            if (path is null)
            {
                return;
            }

            if (_written >= MaximumBytes)
            {
                return;
            }

            var line = $"{Clock.ElapsedMilliseconds,8} {message}{Environment.NewLine}";
            var bytes = Encoding.UTF8.GetByteCount(line);
            if (_written + bytes > MaximumBytes)
            {
                _written = MaximumBytes;
                line += $"       trace truncated at {MaximumBytes} bytes{Environment.NewLine}";
            }
            else
            {
                _written += bytes;
            }

            try
            {
                File.AppendAllText(path, line);
            }
            catch
            {
            }
        }
    }

    /// <summary>Resolves the destination, or null when the trace is not requested.
    /// <para>解析目标路径；未请求 trace 时返回 null。</para></summary>
    private static string? ResolvePath()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" or "all" =>
                Path.Combine(Path.GetTempPath(), "uotan-mirror-trace.log"),
            "0" or "false" or "no" or "off" or "none" => null,
            _ => Path.GetFullPath(value),
        };
    }

    /// <summary>
    /// Formats an interpolated trace line only while the trace is enabled, so a disabled call neither formats
    /// nor allocates.
    /// <para>仅在 trace 启用时格式化插值行，使被关闭的调用既不格式化也不分配。</para>
    /// </summary>
    [InterpolatedStringHandler]
    internal ref struct MirrorTraceMessage
    {
        private DefaultInterpolatedStringHandler _builder;
        private readonly bool _enabled;

        public MirrorTraceMessage(int literalLength, int formattedCount)
        {
            _enabled = Enabled;
            _builder = _enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        internal readonly bool IsEnabled => _enabled;

        public void AppendLiteral(string value)
        {
            if (_enabled)
            {
                _builder.AppendLiteral(value);
            }
        }

        public void AppendFormatted<T>(T value)
        {
            if (_enabled)
            {
                _builder.AppendFormatted(value);
            }
        }

        public void AppendFormatted<T>(T value, int alignment)
        {
            if (_enabled)
            {
                _builder.AppendFormatted(value, alignment);
            }
        }

        public void AppendFormatted<T>(T value, string? format)
        {
            if (_enabled)
            {
                _builder.AppendFormatted(value, format);
            }
        }

        public void AppendFormatted<T>(T value, int alignment, string? format)
        {
            if (_enabled)
            {
                _builder.AppendFormatted(value, alignment, format);
            }
        }

        public void AppendFormatted(string? value)
        {
            if (_enabled)
            {
                _builder.AppendFormatted(value);
            }
        }

        public void AppendFormatted(object? value, int alignment = 0, string? format = null)
        {
            if (_enabled)
            {
                _builder.AppendFormatted(value, alignment, format);
            }
        }

        internal string ToStringAndClear() => _enabled ? _builder.ToStringAndClear() : string.Empty;
    }
}