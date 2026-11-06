using System.Globalization;
using System.Text;
using UotanStudio.DD.Drives;

namespace UotanStudio.DD.Writing;

/// <summary>
/// Options one flash job carries from the host to the writing process.
/// <para>烧录任务从宿主传递到写入进程的选项。</para>
/// </summary>
public sealed record FlashJobOptions
{
    /// <summary>
    /// Gets whether the written bytes are read back and compared with the source image.
    /// <para>获取写入字节是否回读并与源镜像比较。</para>
    /// </summary>
    public bool VerifyAfterWrite { get; init; } = true;

    /// <summary>
    /// Gets whether targets far larger than the image may be written.
    /// <para>获取是否允许写入远大于镜像的目标。</para>
    /// </summary>
    public bool AllowLargeDrive { get; init; }

    /// <summary>
    /// Converts the job options into flasher options that already carry the caller's overwrite confirmation.
    /// <para>把任务选项转换为已带调用方覆盖确认的烧录器选项。</para>
    /// </summary>
    /// <returns>The flasher options for this job.<para>本次任务的烧录器选项。</para></returns>
    public FlashOptions ToFlashOptions() => new()
    {
        ConfirmOverwrite = true,
        VerifyAfterWrite = VerifyAfterWrite,
        AllowLargeDrive = AllowLargeDrive,
    };
}

/// <summary>
/// One flash job: the image to write, the targets and the safety switches.
/// <para>一个烧录任务：待写入镜像、目标与安全开关。</para>
/// </summary>
public sealed record FlashJob
{
    /// <summary>
    /// Gets the absolute path of the image file.
    /// <para>获取镜像文件的绝对路径。</para>
    /// </summary>
    public required string ImagePath { get; init; }

    /// <summary>
    /// Gets the drives that receive the image.
    /// <para>获取接收镜像的驱动器。</para>
    /// </summary>
    public required IReadOnlyList<FlashDrive> Drives { get; init; }

    /// <summary>
    /// Gets the safety switches applied to the write.
    /// <para>获取应用于写入的安全开关。</para>
    /// </summary>
    public required FlashJobOptions Options { get; init; }
}

/// <summary>
/// Text form of a <see cref="FlashJob"/> exchanged between the host and the writing process.
/// <para>宿主与写入进程之间交换的 <see cref="FlashJob"/> 文本形式。</para>
/// </summary>
/// <remarks>
/// The format is one record per line: a header naming the format and version, the image path, the safety
/// switches, one line per target and one line per mount point. Every free-form value is escaped, so paths
/// containing spaces or newlines survive the round trip.
/// <para>
/// 每行一条记录：声明格式与版本的头、镜像路径、安全开关、每个目标一行、每个挂载点一行。所有自由文本都做了
/// 转义，含空格或换行的路径也能完整往返。
/// </para>
/// </remarks>
public static class FlashJobFormat
{
    /// <summary>
    /// The header written as the first line of every job file.
    /// <para>每个任务文件首行写入的头。</para>
    /// </summary>
    public const string Header = "uotan-disk-flash 1";


    /// <summary>
    /// Renders the job as the job-file text.
    /// <para>把任务渲染为任务文件文本。</para>
    /// </summary>
    /// <param name="job">Job to render.<para>待渲染的任务。</para></param>
    /// <returns>The job text, lines terminated by <c>\n</c>.<para>任务文本，行以 <c>\n</c> 结束。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="job"/> is <see langword="null"/>.
    /// <para><paramref name="job"/> 为 <see langword="null"/>。</para></exception>
    public static string Write(FlashJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var builder = new StringBuilder();
        builder.Append(Header).Append('\n');
        builder.Append("image ").Append(FlashTextCodec.Escape(job.ImagePath)).Append('\n');
        builder.Append("verify ").Append(job.Options.VerifyAfterWrite ? '1' : '0').Append('\n');
        builder.Append("allowLargeDrive ").Append(job.Options.AllowLargeDrive ? '1' : '0').Append('\n');
        for (var index = 0; index < job.Drives.Count; index++)
        {
            var drive = job.Drives[index];
            builder.Append("target ")
                .Append(index.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(FlashTextCodec.Escape(drive.DevicePath)).Append(' ')
                .Append(FlashTextCodec.Escape(drive.DisplayName)).Append(' ')
                .Append(drive.Size.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(drive.IsRemovable ? '1' : '0').Append(' ')
                .Append(drive.IsReadOnly ? '1' : '0').Append(' ')
                .Append(drive.IsSystem ? '1' : '0').Append(' ')
                .Append(FlashTextCodec.Escape(drive.Description)).Append('\n');
            foreach (var mountPoint in drive.MountPoints)
            {
                builder.Append("mount ").Append(index.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(FlashTextCodec.Escape(mountPoint)).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads a job rendered by <see cref="Write"/>.
    /// <para>读取由 <see cref="Write"/> 渲染的任务。</para>
    /// </summary>
    /// <param name="text">Job-file text.<para>任务文件文本。</para></param>
    /// <returns>The parsed job.<para>解析出的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FormatException">The header is missing or a record is malformed.
    /// <para>缺少头或某条记录格式错误。</para></exception>
    public static FlashJob Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string? imagePath = null;
        var verify = true;
        var allowLargeDrive = false;
        var builders = new List<DriveBuilder>();
        var headerSeen = false;
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
                    throw new FormatException("The flash job header is missing or names an unsupported version.");
                }

                headerSeen = true;
                continue;
            }

            var (keyword, payload) = SplitPair(line);
            switch (keyword)
            {
                case "image":
                    imagePath = FlashTextCodec.Unescape(payload);
                    break;
                case "verify":
                    verify = payload == "1";
                    break;
                case "allowLargeDrive":
                    allowLargeDrive = payload == "1";
                    break;
                case "target":
                    builders.Add(ReadTarget(payload));
                    break;
                case "mount":
                    ReadMount(payload, builders);
                    break;
                default:
                    break;
            }
        }

        if (!headerSeen || string.IsNullOrWhiteSpace(imagePath) || builders.Count == 0)
        {
            throw new FormatException("The flash job must name an image and at least one target.");
        }

        return new FlashJob
        {
            ImagePath = imagePath,
            Drives = [.. builders.Select(builder => builder.Build())],
            Options = new FlashJobOptions { VerifyAfterWrite = verify, AllowLargeDrive = allowLargeDrive },
        };
    }

    private static DriveBuilder ReadTarget(string rest)
    {
        var parts = SplitFields(rest, 8);
        if (parts.Count < 8)
        {
            throw new FormatException("A target record needs an id, a device path, a name and four flags.");
        }

        return new DriveBuilder
        {
            DevicePath = FlashTextCodec.Unescape(parts[1]),
            DisplayName = FlashTextCodec.Unescape(parts[2]),
            Size = long.TryParse(parts[3], CultureInfo.InvariantCulture, out var size) ? size : 0,
            IsRemovable = parts[4] == "1",
            IsReadOnly = parts[5] == "1",
            IsSystem = parts[6] == "1",
            Description = FlashTextCodec.Unescape(parts[7]),
        };
    }

    private static void ReadMount(string rest, List<DriveBuilder> builders)
    {
        var parts = SplitFields(rest, 2);
        if (parts.Count < 2 ||
            !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var index) ||
            index < 0 || index >= builders.Count)
        {
            throw new FormatException("A mount record must name the target it belongs to.");
        }

        builders[index].MountPoints.Add(FlashTextCodec.Unescape(parts[1]));
    }

    private static (string Keyword, string Payload) SplitPair(string line)
    {
        var separator = line.IndexOf(' ', StringComparison.Ordinal);
        return separator < 0 ? (line, string.Empty) : (line[..separator], line[(separator + 1)..]);
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

        internal FlashDrive Build() => new()
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