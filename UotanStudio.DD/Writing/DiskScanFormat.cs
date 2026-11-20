namespace UotanStudio.DD.Writing;

/// <summary>
/// One drive as the privileged scan helper reports it, together with why the enumeration stopped early.
/// <para>提权扫描助手报告的单个驱动器，以及枚举提前停止的原因。</para>
/// </summary>
public sealed record DiskScanReport
{
    /// <summary>
    /// Gets the drives the helper enumerated.
    /// <para>获取助手枚举到的驱动器。</para>
    /// </summary>
    public required IReadOnlyList<Drives.FlashDrive> Drives { get; init; }

    /// <summary>
    /// Gets the per-drive findings the helper could not resolve, in report order.
    /// <para>获取助手无法解析的逐盘问题，按报告顺序排列。</para>
    /// </summary>
    public IReadOnlyList<string> Issues { get; init; } = [];
}

/// <summary>
/// Text form of a <see cref="DiskScanReport"/> exchanged between the host and the privileged scan helper.
/// <para>宿主与提权扫描助手之间交换的 <see cref="DiskScanReport"/> 文本形式。</para>
/// </summary>
/// <remarks>
/// The format is one record per line: a header, one <c>drive</c> line per enumerated disk with its mount points
/// as separate records, one <c>issue</c> line per unresolved finding, and a closing <c>end</c> line that proves the
/// helper finished instead of being cut off.
/// <para>
/// 每行一条记录：头、每个已枚举磁盘一行 <c>drive</c>（挂载点各占一行）、每个未解析问题一行 <c>issue</c>，
/// 以及证明助手正常结束而非中途被截断的结尾 <c>end</c> 行。
/// </para>
/// </remarks>
public static class DiskScanFormat
{
    /// <summary>
    /// The header written as the first line of every scan report.
    /// <para>每个扫描报告首行写入的头。</para>
    /// </summary>
    public const string Header = "uotan-disk-scan 1";

    /// <summary>
    /// The closing record written once the helper finished enumerating.
    /// <para>助手完成枚举后写入的结尾记录。</para>
    /// </summary>
    public const string End = "end";

    /// <summary>
    /// Renders the reply of a scan helper.
    /// <para>渲染扫描助手的回复。</para>
    /// </summary>
    /// <param name="report">Reply to render.<para>待渲染的回复。</para></param>
    /// <returns>The report text, lines terminated by <c>\n</c>.<para>报告文本，行以 <c>\n</c> 结束。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.
    /// <para><paramref name="report"/> 为 <see langword="null"/>。</para></exception>
    public static string Write(DiskScanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new System.Text.StringBuilder();
        builder.Append(Header).Append('\n');
        for (var index = 0; index < report.Drives.Count; index++)
        {
            var drive = report.Drives[index];
            builder.Append("drive ")
                .Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(' ')
                .Append(FlashTextCodec.Escape(drive.DevicePath)).Append(' ')
                .Append(FlashTextCodec.Escape(drive.DisplayName)).Append(' ')
                .Append(drive.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(' ')
                .Append(drive.IsRemovable ? '1' : '0').Append(' ')
                .Append(drive.IsReadOnly ? '1' : '0').Append(' ')
                .Append(drive.IsSystem ? '1' : '0').Append(' ')
                .Append(FlashTextCodec.Escape(drive.Description)).Append('\n');
            foreach (var mountPoint in drive.MountPoints)
            {
                builder.Append("mount ").Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(' ').Append(FlashTextCodec.Escape(mountPoint)).Append('\n');
            }
        }

        foreach (var issue in report.Issues)
        {
            builder.Append("issue ").Append(FlashTextCodec.Escape(issue)).Append('\n');
        }

        builder.Append(End).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// Reads a report rendered by <see cref="Write"/>.
    /// <para>读取由 <see cref="Write"/> 渲染的报告。</para>
    /// </summary>
    /// <param name="text">Report text.<para>报告文本。</para></param>
    /// <param name="isComplete">Whether the closing record was present.
    /// <para>结尾记录是否存在。</para></param>
    /// <returns>The parsed report.<para>解析出的报告。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FormatException">The header is missing or a record is malformed.
    /// <para>缺少头或某条记录格式错误。</para></exception>
    public static DiskScanReport Read(string text, out bool isComplete)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builders = new List<DriveBuilder>();
        var issues = new List<string>();
        var headerSeen = false;
        isComplete = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (!headerSeen)
            {
                if (!string.Equals(line, Header, StringComparison.Ordinal))
                {
                    throw new FormatException("The disk scan report header is missing or names an unsupported version.");
                }

                headerSeen = true;
                continue;
            }

            if (string.Equals(line, End, StringComparison.Ordinal))
            {
                isComplete = true;
                break;
            }

            var separator = line.IndexOf(' ', StringComparison.Ordinal);
            var keyword = separator < 0 ? line : line[..separator];
            var rest = separator < 0 ? string.Empty : line[(separator + 1)..];
            switch (keyword)
            {
                case "drive":
                    builders.Add(ReadDrive(rest));
                    break;
                case "mount":
                    ReadMount(rest, builders);
                    break;
                case "issue":
                    issues.Add(FlashTextCodec.Unescape(rest));
                    break;
                default:
                    break;
            }
        }

        if (!headerSeen)
        {
            throw new FormatException("The disk scan report header is missing or names an unsupported version.");
        }

        return new DiskScanReport
        {
            Drives = [.. builders.Select(builder => builder.Build())],
            Issues = issues,
        };
    }

    private static DriveBuilder ReadDrive(string rest)
    {
        var fields = SplitFields(rest, 8);
        if (fields.Count < 8)
        {
            throw new FormatException("A drive record needs an id, a device path, a name and four flags.");
        }

        return new DriveBuilder
        {
            DevicePath = FlashTextCodec.Unescape(fields[1]),
            DisplayName = FlashTextCodec.Unescape(fields[2]),
            Size = long.TryParse(fields[3], System.Globalization.CultureInfo.InvariantCulture, out var size) ? size : 0,
            IsRemovable = fields[4] == "1",
            IsReadOnly = fields[5] == "1",
            IsSystem = fields[6] == "1",
            Description = FlashTextCodec.Unescape(fields[7]),
        };
    }

    private static void ReadMount(string rest, List<DriveBuilder> builders)
    {
        var fields = SplitFields(rest, 2);
        if (fields.Count < 2 ||
            !int.TryParse(fields[0], System.Globalization.CultureInfo.InvariantCulture, out var index) ||
            index < 0 || index >= builders.Count)
        {
            throw new FormatException("A mount record must name the drive it belongs to.");
        }

        builders[index].MountPoints.Add(FlashTextCodec.Unescape(fields[1]));
    }

    private static List<string> SplitFields(string value, int maximum)
    {
        var fields = new List<string>(maximum);
        var index = 0;
        while (index < value.Length && fields.Count < maximum - 1)
        {
            var separator = value.IndexOf(' ', index);
            if (separator < 0)
            {
                break;
            }

            fields.Add(value[index..separator]);
            index = separator + 1;
        }

        if (index <= value.Length)
        {
            fields.Add(value[index..]);
        }

        return fields;
    }

    private sealed class DriveBuilder
    {
        internal string DevicePath { get; init; } = string.Empty;

        internal string DisplayName { get; init; } = string.Empty;

        internal long Size { get; init; }

        internal bool IsRemovable { get; init; }

        internal bool IsReadOnly { get; init; }

        internal bool IsSystem { get; init; }

        internal string Description { get; init; } = string.Empty;

        internal List<string> MountPoints { get; } = [];

        internal Drives.FlashDrive Build() => new()
        {
            DevicePath = DevicePath,
            DisplayName = DisplayName,
            Size = Size,
            IsRemovable = IsRemovable,
            IsReadOnly = IsReadOnly,
            IsSystem = IsSystem,
            Description = Description,
            MountPoints = [.. MountPoints],
        };
    }
}