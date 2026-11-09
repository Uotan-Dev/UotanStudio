using System.Globalization;

namespace UotanStudio.DD.Writing;

/// <summary>
/// Text form of <see cref="FlashProgress"/>, one line per report, exchanged between the writing process and
/// the host that displays it.
/// <para>宿主与写入进程之间交换的 <see cref="FlashProgress"/> 文本形式，每次上报一行。</para>
/// </summary>
/// <remarks>
/// A report starts with <c>progress</c>; a successful finish starts with <c>done</c>; a failure starts with
/// <c>error</c> and carries the failure kind, the message and the innermost cause. Every value is escaped,
/// so messages containing newlines stay on one line.
/// <para>
/// 进度以 <c>progress</c> 开头；成功结束以 <c>done</c> 开头；失败以 <c>error</c> 开头并携带失败类别、消息与
/// 最内层原因。所有值都做了转义，含换行的消息仍保持在一行内。
/// </para>
/// </remarks>
public static class FlashProgressFormat
{
    /// <summary>
    /// Renders one progress report.
    /// <para>渲染一次进度上报。</para>
    /// </summary>
    /// <param name="progress">Report to render.<para>待渲染的上报。</para></param>
    /// <returns>One line without a line terminator.<para>不含行结束符的单行文本。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="progress"/> is <see langword="null"/>.
    /// <para><paramref name="progress"/> 为 <see langword="null"/>。</para></exception>
    public static string WriteProgress(FlashProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        return string.Join(' ',
            "progress",
            progress.Stage.ToString(),
            Format(progress.BytesProcessed),
            progress.TotalBytes is { } total ? Format(total) : "-",
            progress.Percentage is { } percentage ? percentage.ToString("0.####", CultureInfo.InvariantCulture) : "-",
            progress.BytesPerSecond.ToString("0.###", CultureInfo.InvariantCulture),
            progress.RemainingTime is { } remaining
                ? remaining.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                : "-",
            progress.TargetCount.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Renders the successful outcome of a job.
    /// <para>渲染任务的成功结果。</para>
    /// </summary>
    /// <param name="result">Completed operation.<para>已完成的操作。</para></param>
    /// <returns>One line without a line terminator.<para>不含行结束符的单行文本。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.
    /// <para><paramref name="result"/> 为 <see langword="null"/>。</para></exception>
    public static string WriteCompleted(FlashResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return string.Join(' ',
            "done",
            Format(result.ImageBytes),
            Format(result.BytesWritten),
            result.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            result.SourceChecksum);
    }

    /// <summary>
    /// Renders a failed job as an error report.
    /// <para>把失败的任务渲染为错误上报。</para>
    /// </summary>
    /// <param name="error">The exception that stopped the job.<para>中止任务的异常。</para></param>
    /// <returns>One line without a line terminator.<para>不含行结束符的单行文本。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.
    /// <para><paramref name="error"/> 为 <see langword="null"/>。</para></exception>
    public static string WriteError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var cause = error;
        while (cause.InnerException is { } inner)
        {
            cause = inner;
        }

        return string.Join(' ',
            "error",
            error.GetType().Name,
            FlashTextCodec.Escape(error.Message),
            FlashTextCodec.Escape(cause.GetType().Name),
            FlashTextCodec.Escape(cause.Message));
    }

    /// <summary>
    /// Parses one line rendered by this class.
    /// <para>解析本类渲染的一行文本。</para>
    /// </summary>
    /// <param name="line">Line without a terminator, as rendered by this class.
    /// <para>不含结束符的单行文本，由本类渲染。</para></param>
    /// <returns>The parsed report, or <see langword="null"/> when the line is not a recognized report.
    /// <para>解析出的上报；该行不是可识别上报时为 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.
    /// <para><paramref name="line"/> 为 <see langword="null"/>。</para></exception>
    public static FlashProgressReport? TryParse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var fields = line.TrimEnd('\r').Split(' ');
        return fields[0] switch
        {
            "progress" => ParseProgress(fields),
            "done" => ParseCompleted(fields),
            "error" => ParseError(fields),
            _ => null,
        };
    }

    private static FlashProgressReport? ParseProgress(string[] fields)
    {
        if (fields.Length < 8 || !Enum.TryParse<FlashStage>(fields[1], out var stage))
        {
            return null;
        }

        return new FlashProgressReport
        {
            Progress = new FlashProgress
            {
                Stage = stage,
                BytesProcessed = ParseLong(fields[2]) ?? 0,
                TotalBytes = ParseLong(fields[3]),
                Percentage = ParseDouble(fields[4]),
                BytesPerSecond = ParseDouble(fields[5]) ?? 0,
                RemainingTime = ParseDouble(fields[6]) is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                TargetCount = int.TryParse(fields[7], CultureInfo.InvariantCulture, out var count) ? count : 0,
            },
        };
    }

    private static FlashProgressReport? ParseCompleted(string[] fields) => fields.Length < 5
        ? null
        : new FlashProgressReport
        {
            Result = new FlashJobResult
            {
                ImageBytes = ParseLong(fields[1]) ?? 0,
                BytesWritten = ParseLong(fields[2]) ?? 0,
                Elapsed = TimeSpan.FromSeconds(ParseDouble(fields[3]) ?? 0),
                SourceChecksum = fields[4],
            },
        };

    private static FlashProgressReport? ParseError(string[] fields) => fields.Length < 5
        ? null
        : new FlashProgressReport
        {
            ErrorType = FlashTextCodec.Unescape(fields[1]),
            ErrorMessage = FlashTextCodec.Unescape(fields[2]),
            InnermostType = FlashTextCodec.Unescape(fields[3]),
            InnermostMessage = FlashTextCodec.Unescape(fields[4]),
        };

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static long? ParseLong(string value) =>
        string.Equals(value, "-", StringComparison.Ordinal) || !long.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? null
            : parsed;

    private static double? ParseDouble(string value) =>
        string.Equals(value, "-", StringComparison.Ordinal) ||
        !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
        double.IsNaN(parsed) || double.IsInfinity(parsed)
            ? null
            : parsed;
}

/// <summary>
/// One parsed line of a flash-job stream: progress, completion or failure.
/// <para>烧录任务流中解析出的一行：进度、完成或失败。</para>
/// </summary>
public sealed record FlashProgressReport
{
    /// <summary>
    /// Gets the progress snapshot carried by a <c>progress</c> line.
    /// <para>获取 <c>progress</c> 行携带的进度快照。</para>
    /// </summary>
    public FlashProgress? Progress { get; init; }

    /// <summary>
    /// Gets the outcome carried by a <c>done</c> line.
    /// <para>获取 <c>done</c> 行携带的结果。</para>
    /// </summary>
    public FlashJobResult? Result { get; init; }

    /// <summary>
    /// Gets the failing exception type carried by an <c>error</c> line.
    /// <para>获取 <c>error</c> 行携带的失败异常类型。</para>
    /// </summary>
    public string? ErrorType { get; init; }

    /// <summary>
    /// Gets the failure message carried by an <c>error</c> line.
    /// <para>获取 <c>error</c> 行携带的失败消息。</para>
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the innermost exception type carried by an <c>error</c> line.
    /// <para>获取 <c>error</c> 行携带的最内层异常类型。</para>
    /// </summary>
    public string? InnermostType { get; init; }

    /// <summary>
    /// Gets the innermost exception message carried by an <c>error</c> line.
    /// <para>获取 <c>error</c> 行携带的最内层异常消息。</para>
    /// </summary>
    public string? InnermostMessage { get; init; }

    /// <summary>
    /// Gets whether the line reports a failure.
    /// <para>获取该行是否报告失败。</para>
    /// </summary>
    public bool IsError => ErrorType is not null;
}