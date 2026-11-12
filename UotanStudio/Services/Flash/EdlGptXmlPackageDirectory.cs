using System.Globalization;
using System.Text;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Owns the temporary directory that receives one generated partition-table package: one directory per read under the
/// host temporary path, the previous one removed when a new read starts, and every tracked directory removed when the
/// process exits.
/// <para>管理接收一次生成分区表包的临时目录：每次读取在主机临时路径下新建一个，新读取开始时删除上一个，进程退出时删除
/// 全部已登记目录。</para>
/// </summary>
internal static class EdlGptXmlPackageDirectory
{
    private static readonly Lock Gate = new();
    private static readonly List<string> Tracked = [];
    private static string? _current;

    static EdlGptXmlPackageDirectory()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteAll();
    }

    /// <summary>
    /// Creates the directory for one read, replacing the previous one, and registers it for exit cleanup.
    /// <para>为一次读取创建目录并替换上一个，同时登记退出清理。</para>
    /// </summary>
    /// <param name="deviceKey">Key of the device the table belongs to; only its safe characters are kept.
    /// <para>分区表所属设备的键；只保留其中可安全用于路径的字符。</para></param>
    /// <returns>The full path of the new, empty directory. <para>新建空目录的完整路径。</para></returns>
    /// <exception cref="ArgumentException">The key is blank. <para>键为空白。</para></exception>
    internal static string Create(string deviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        var root = Path.Combine(Path.GetTempPath(), "UotanStudio", "gpt");
        var name = SafeStem(deviceKey) + "-" +
            DateTimeOffset.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        lock (Gate)
        {
            if (_current is { Length: > 0 } previous)
            {
                Delete(previous);
                Tracked.Remove(previous);
            }

            var directory = Path.Combine(root, name);
            for (var attempt = 2; Directory.Exists(directory); attempt++)
            {
                directory = Path.Combine(root, FormattableString.Invariant($"{name}-{attempt}"));
            }

            Directory.CreateDirectory(directory);
            _current = directory;
            Tracked.Add(directory);
            return directory;
        }
    }

    private static string SafeStem(string deviceKey)
    {
        var builder = new StringBuilder(deviceKey.Length);
        foreach (var ch in deviceKey)
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-');
        }

        var stem = builder.ToString().Trim('-', '.');
        return stem.Length switch
        {
            0 => "device",
            > 48 => stem[..48],
            _ => stem
        };
    }

    private static void DeleteAll()
    {
        lock (Gate)
        {
            foreach (var directory in Tracked)
            {
                Delete(directory);
            }

            Tracked.Clear();
            _current = null;
        }
    }

    private static void Delete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}