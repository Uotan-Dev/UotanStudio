using System.Diagnostics;
using System.Globalization;

namespace UotanStudio.DD.Drives.Linux;

/// <summary>
/// Enumerates optical recorders from <c>/sys/class/block/sr*</c> on Linux.
/// <para>在 Linux 上从 <c>/sys/class/block/sr*</c> 枚举光驱。</para>
/// </summary>
/// <remarks>
/// Drive capabilities come from <c>/proc/sys/dev/cdrom/info</c> using the kernel's real field names
/// (<c>Can write CD-R:</c>, <c>Can read DVD:</c>, etc.). Loaded media type and writability come from
/// <c>udevadm info --query=property --name=&lt;device&gt;</c> when available; without udev properties the
/// scanner reports the drive with <see cref="FlashDrive.IsDiscWritable"/> set to <see langword="false"/>
/// so recording is refused until the media state can be determined.
/// <para>
/// 驱动器能力来自 <c>/proc/sys/dev/cdrom/info</c> 的真实内核字段名（<c>Can write CD-R:</c>、<c>Can read DVD:</c> 等）。
/// 已装入介质的类型与可写性来自 <c>udevadm info --query=property --name=&lt;device&gt;</c>；没有 udev 属性时
/// 扫描器仍上报光驱，但把 <see cref="FlashDrive.IsDiscWritable"/> 置为 <see langword="false"/>，
/// 在确定介质状态前拒绝刻录。
/// </para>
/// </remarks>
internal sealed class LinuxOpticalDriveScanner : IDriveScanner
{
    private const string SrClassRoot = "/sys/class/block";
    private const string CdromInfoPath = "/proc/sys/dev/cdrom/info";

    /// <inheritdoc />
    public async Task<IReadOnlyList<FlashDrive>> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(SrClassRoot))
        {
            return [];
        }

        var driveNames = Directory.EnumerateDirectories(SrClassRoot)
            .Select(Path.GetFileName)
            .Where(name => name is not null && name.StartsWith("sr", StringComparison.Ordinal)
                && name.Length > 2 && char.IsDigit(name[2]))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (driveNames.Length == 0)
        {
            return [];
        }

        var driveCaps = ReadCdromInfo(driveNames);
        var drives = new List<FlashDrive>(driveNames.Length);
        foreach (var name in driveNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(SrClassRoot, name);
            var devicePath = "/dev/" + name;
            var vendor = ReadAttribute(Path.Combine(directory, "device", "vendor"));
            var model = ReadAttribute(Path.Combine(directory, "device", "model"));
            var description = string.Join(' ', new[] { vendor, model }.Where(part => part.Length > 0));
            if (description.Length == 0)
            {
                description = name;
            }

            (OpticalDiscFormat Format, bool Writable) caps = driveCaps.TryGetValue(name, out var value)
                ? value
                : (OpticalDiscFormat.Unknown, false);

            var (mediaFormat, mediaWritable) = await ReadMediaStateAsync(devicePath, cancellationToken)
                .ConfigureAwait(false);
            var format = mediaFormat != OpticalDiscFormat.Unknown ? mediaFormat : caps.Format;
            var writable = mediaWritable && caps.Writable;

            drives.Add(new FlashDrive
            {
                DevicePath = devicePath,
                DisplayName = description,
                Size = 0,
                IsRemovable = true,
                IsReadOnly = false,
                IsSystem = false,
                Description = description,
                MountPoints = [],
                MediaKind = FlashMediaKind.OpticalDisc,
                DiscFormat = format,
                IsDiscWritable = writable,
            });
        }

        return drives;
    }

    /// <summary>
    /// Reads the kernel's CD-ROM capability table.
    /// <para>读取内核的 CD-ROM 能力表。</para>
    /// </summary>
    /// <param name="driveNames">Device names such as <c>sr0</c>.<para>设备名，例如 <c>sr0</c>。</para></param>
    /// <returns>Capabilities keyed by device name.<para>按设备名索引的能力表。</para></returns>
    internal static IReadOnlyDictionary<string, (OpticalDiscFormat Format, bool Writable)> ReadCdromInfo(
        IReadOnlyList<string> driveNames)
    {
        if (!File.Exists(CdromInfoPath))
        {
            return new Dictionary<string, (OpticalDiscFormat Format, bool Writable)>(StringComparer.Ordinal);
        }

        return ParseCdromInfo(File.ReadLines(CdromInfoPath), driveNames);
    }

    /// <summary>
    /// Parses kernel CD-ROM capability text into per-device capabilities.
    /// <para>把内核 CD-ROM 能力文本解析为逐设备能力。</para>
    /// </summary>
    /// <param name="lines">Lines from <c>/proc/sys/dev/cdrom/info</c>.<para><c>/proc/sys/dev/cdrom/info</c> 的各行。</para></param>
    /// <param name="driveNames">Device names such as <c>sr0</c>.<para>设备名，例如 <c>sr0</c>。</para></param>
    /// <returns>Capabilities keyed by device name.<para>按设备名索引的能力表。</para></returns>
    internal static IReadOnlyDictionary<string, (OpticalDiscFormat Format, bool Writable)> ParseCdromInfo(
        IEnumerable<string> lines,
        IReadOnlyList<string> driveNames)
    {
        string[]? names = null;
        var flags = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var values = line[(separator + 1)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (key == "drive name")
            {
                names = values;
            }
            else
            {
                flags[key] = values;
            }
        }

        var result = new Dictionary<string, (OpticalDiscFormat Format, bool Writable)>(StringComparer.Ordinal);
        if (names is null)
        {
            return result;
        }

        for (var index = 0; index < names.Length; index++)
        {
            var name = names[index];
            if (!driveNames.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            // The kernel reports DVD but no BD/CD read columns; CD capability comes from the CD-R/RW flags.
            var format = HasFlag(flags, "Can read DVD", index) ? OpticalDiscFormat.Dvd
                : HasAnyFlag(flags, index, "Can write CD-R", "Can write CD-RW") ? OpticalDiscFormat.Cd
                : OpticalDiscFormat.Unknown;
            var writable = HasAnyFlag(flags, index,
                "Can write CD-R", "Can write CD-RW", "Can write DVD-R", "Can write DVD-RAM",
                "Can write MRW", "Can write RAM");
            result[name] = (format, writable);
        }

        return result;
    }

    /// <summary>
    /// Re-reads the udev media state of one device immediately before recording.
    /// <para>在刻录前重读一个设备的 udev 介质状态。</para>
    /// </summary>
    /// <param name="devicePath">Device node such as <c>/dev/sr0</c>.<para>设备节点，例如 <c>/dev/sr0</c>。</para></param>
    /// <param name="cancellationToken">Cancels the query.<para>取消查询。</para></param>
    internal static Task<(OpticalDiscFormat Format, bool Writable)> ConfirmMediaStateAsync(
        string devicePath,
        CancellationToken cancellationToken) => ReadMediaStateAsync(devicePath, cancellationToken);

    private static async Task<(OpticalDiscFormat Format, bool Writable)> ReadMediaStateAsync(
        string devicePath,
        CancellationToken cancellationToken)
    {
        var properties = await ReadUdevPropertiesAsync(devicePath, cancellationToken).ConfigureAwait(false);
        if (properties.Count == 0)
        {
            return (OpticalDiscFormat.Unknown, false);
        }

        var format = properties.TryGetValue("ID_CDROM_MEDIA_BD", out var bd) && bd == "1"
            ? OpticalDiscFormat.BluRay
            : properties.TryGetValue("ID_CDROM_MEDIA_DVD", out var dvd) && dvd == "1"
                ? OpticalDiscFormat.Dvd
                : properties.TryGetValue("ID_CDROM_MEDIA_CD", out var cd) && cd == "1"
                    ? OpticalDiscFormat.Cd
                    : OpticalDiscFormat.Unknown;

        // A recordable disc is blank when the udev rule reports a media type but no read-only filesystem.
        var recordable = properties.TryGetValue("ID_CDROM_MEDIA", out var media) && media.Length > 0
            && (!properties.TryGetValue("ID_FS_TYPE", out var fsType) || fsType.Length == 0);
        var writable = recordable
            && (properties.TryGetValue("ID_CDROM_MEDIA_CD_R", out var cdr) && cdr == "1"
                || properties.TryGetValue("ID_CDROM_MEDIA_CD_RW", out var cdrw) && cdrw == "1"
                || properties.TryGetValue("ID_CDROM_MEDIA_DVD_R", out var dvdr) && dvdr == "1"
                || properties.TryGetValue("ID_CDROM_MEDIA_DVD_RW", out var dvdrw) && dvdrw == "1"
                || properties.TryGetValue("ID_CDROM_MEDIA_DVD_PLUS_R", out var dvdp) && dvdp == "1"
                || properties.TryGetValue("ID_CDROM_MEDIA_DVD_PLUS_RW", out var dvdpw) && dvdpw == "1"
                || properties.TryGetValue("ID_CDROM_MEDIA_BD", out var bdr) && bdr == "1"
                || properties.TryGetValue("ID_CDROM_MEDIA_BD_R", out var bdrw) && bdrw == "1");

        return (format, writable);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadUdevPropertiesAsync(
        string devicePath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "udevadm",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("info");
        startInfo.ArgumentList.Add("--query=property");
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(devicePath);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in output.Split('\n'))
            {
                var separator = line.IndexOf('=', StringComparison.Ordinal);
                if (separator > 0)
                {
                    result[line[..separator]] = line[(separator + 1)..].Trim();
                }
            }

            return result;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static bool HasFlag(
        IReadOnlyDictionary<string, string[]> flags,
        string key,
        int index) =>
        flags.TryGetValue(key, out var values) && index < values.Length && values[index] == "1";

    private static bool HasAnyFlag(
        IReadOnlyDictionary<string, string[]> flags,
        int index,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (HasFlag(flags, key, index))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadAttribute(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}