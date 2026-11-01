using System.Diagnostics;
using System.IO;
using System.Text;
using UotanStudio.Comm.Contracts;

namespace UotanStudio.Services.Devices;

/// <summary>
/// A file or directory entry on a remote ADB or HDC device.
/// <para>远程 ADB 或 HDC 设备上的文件或目录条目。</para>
/// </summary>
public sealed class DeviceFileSystemEntry
{
    /// <summary>Entry name (file or folder name).<para>条目名称（文件或文件夹名）。</para></summary>
    public required string Name { get; init; }

    /// <summary>Absolute path on the device.<para>设备上的绝对路径。</para></summary>
    public required string FullPath { get; init; }

    /// <summary>Whether this entry is a directory.<para>是否为目录。</para></summary>
    public required bool IsDirectory { get; init; }

    /// <summary>Whether this entry is a symbolic link.<para>是否为软链接。</para></summary>
    public bool IsSymlink { get; init; }

    /// <summary>Link destination when this entry is a symbolic link.<para>软链接指向的目标路径（如果是符号链接）。</para></summary>
    public string? LinkTarget { get; init; }

    /// <summary>File size in bytes; -1 for directories.<para>文件大小（字节）；目录为 -1。</para></summary>
    public long Size { get; init; } = -1;

    /// <summary>Unix permission string, e.g. <c>drwxr-xr-x</c>.<para>Unix 权限字符串，例如 <c>drwxr-xr-x</c>。</para></summary>
    public string Permissions { get; init; } = string.Empty;

    /// <summary>Date and time of last modification.<para>最后修改时间字符串。</para></summary>
    public string ModificationTime { get; init; } = string.Empty;
}

/// <summary>
/// Service providing file management operations (list, push, pull, delete, rename, mkdir) for ADB and HDC devices.
/// <para>为 ADB 与 HDC 设备提供文件管理操作（枚举、上传、下载、删除、重命名、新建目录）的服务。</para>
/// </summary>
public sealed class DeviceFileSystemService
{
    private static readonly Lazy<DeviceFileSystemService> LazyInstance = new(() => new DeviceFileSystemService());

    /// <summary>Shared singleton instance.<para>共享单例实例。</para></summary>
    public static DeviceFileSystemService Instance => LazyInstance.Value;

    private const int CommandTimeoutMs = 15_000;

    /// <summary>
    /// Escapes a remote shell path argument inside single quotes to avoid injection and space issues.
    /// <para>使用单引号转义远程 shell 路径参数，防止空格与特殊字符导致的注入与截断。</para>
    /// </summary>
    /// <param name="path">The remote path to escape.<para>要转义的远程路径。</para></param>
    /// <returns>The escaped argument wrapped in single quotes.<para>用单引号包裹并完成转义的参数。</para></returns>
    public static string EscapeShellArgument(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return "'" + path.Replace("'", "'\\''") + "'";
    }

    /// <summary>
    /// Computes the parent directory path for a remote POSIX-style path.
    /// <para>计算远程 POSIX 风格路径的父目录路径。</para>
    /// </summary>
    /// <param name="remotePath">The current remote path.<para>当前远程路径。</para></param>
    /// <returns>The parent directory path.<para>父目录路径。</para></returns>
    public static string GetParentPath(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath) || remotePath == "/")
        {
            return "/";
        }

        var normalized = remotePath.TrimEnd('/');
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            return "/";
        }

        return normalized[..lastSlash];
    }

    /// <summary>
    /// Combines a base remote directory and a child name or relative path.
    /// <para>拼接远程基础目录与子项名称或相对路径。</para>
    /// </summary>
    /// <param name="basePath">The base remote directory.<para>基础远程目录。</para></param>
    /// <param name="subPath">The child entry name.<para>子条目名称。</para></param>
    /// <returns>The combined remote path.<para>拼接后的远程路径。</para></returns>
    /// <summary>
    /// Combines a base remote path with a child name.
    /// <para>拼接远程基础路径与子项名称。</para>
    /// </summary>
    public static string CombinePath(string basePath, string subPath) => CombineRemotePath(basePath, subPath);

    public static string CombineRemotePath(string basePath, string subPath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return "/" + subPath.TrimStart('/');
        }

        var trimmedBase = basePath.TrimEnd('/');
        var trimmedSub = subPath.TrimStart('/');
        return trimmedBase.Length == 0 ? "/" + trimmedSub : $"{trimmedBase}/{trimmedSub}";
    }

    /// <summary>
    /// Gets the recommended default browsing directory for the specified device.
    /// <para>获取指定设备的推荐默认浏览目录。</para>
    /// </summary>
    /// <param name="device">The target device.<para>目标设备。</para></param>
    /// <returns>Default path, e.g. <c>/sdcard</c> for Android and Docs for OpenHarmony.<para>默认路径，Android 通常为 <c>/sdcard</c>，OpenHarmony 为用户文档目录。</para></returns>
    public static string GetDefaultPath(DetectedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Protocol == DeviceProtocolKind.Hdc
            ? "/storage/media/100/local/files/Docs"
            : "/sdcard";
    }

    /// <summary>
    /// Runs a remote shell command and returns its standard output text.
    /// <para>执行远程 shell 命令并返回其标准输出文本。</para>
    /// </summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="command">Command string.<para>命令字符串。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>Standard output of the command.<para>命令的标准输出内容。</para></returns>
    public async Task<string> RunShellAsync(DetectedDevice device, string command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrEmpty(command);

        if (device.Protocol == DeviceProtocolKind.Hdc)
        {
            return await HdcDeviceService.FromSettings().ShellAsync(device, command, cancellationToken).ConfigureAwait(false);
        }

        return await App.DeviceMonitor.RunAdbAsync(device, async session =>
        {
            var result = await session.CreateShellClient(command)
                .ExecuteAsync(CommandTimeoutMs, cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetString(result.Stdout);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Enumerates directory contents on the remote device via <c>ls -la</c>.
    /// <para>通过 <c>ls -la</c> 枚举远程设备指定目录的内容。</para>
    /// </summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="remotePath">Remote directory path to enumerate.<para>要枚举的远程目录路径。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>List of parsed file system entries.<para>解析出的文件系统条目列表。</para></returns>
    public async Task<IReadOnlyList<DeviceFileSystemEntry>> EnumerateDirectoryAsync(
        DetectedDevice device,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var path = string.IsNullOrWhiteSpace(remotePath) ? "/" : remotePath.Trim();

        var listPath = path.EndsWith('/') ? path : path + "/";
        var output = await RunShellAsync(device, $"ls -la {EscapeShellArgument(listPath)}", cancellationToken).ConfigureAwait(false);

        if (output.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("opendir failed", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return ParseLsOutput(output, path);
    }

    /// <summary>
    /// Parses the standard <c>ls -la</c> output into file system entries.
    /// <para>将标准的 <c>ls -la</c> 输出解析为文件系统条目。</para>
    /// </summary>
    /// <param name="output">Raw command output.<para>命令原始输出。</para></param>
    /// <param name="baseDirectory">The parent directory path.<para>父目录路径。</para></param>
    /// <returns>Parsed entries.<para>解析后的条目。</para></returns>
    public static IReadOnlyList<DeviceFileSystemEntry> ParseLsOutput(string output, string baseDirectory)
    {
        var entries = new List<DeviceFileSystemEntry>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return entries;
        }

        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 7)
            {
                continue;
            }

            var perm = parts[0];
            if (perm.Length < 10)
            {
                continue;
            }

            var typeChar = perm[0];
            var isDir = typeChar == 'd';
            var isSymlink = typeChar == 'l';

            var dateIndex = -1;
            for (var i = 4; i < parts.Length - 1; i++)
            {
                if (parts[i].Contains(':') || parts[i].Contains('-'))
                {
                    dateIndex = i;
                    break;
                }
            }

            long size = -1;
            string rawName;
            string modTime = string.Empty;

            if (dateIndex >= 5 && dateIndex < parts.Length - 1)
            {
                _ = long.TryParse(parts[dateIndex - 1], out size);
                modTime = parts[dateIndex];
                var nameStartIndex = dateIndex + 1;
                if (dateIndex + 2 < parts.Length && parts[dateIndex + 1].Contains(':'))
                {
                    modTime += " " + parts[dateIndex + 1];
                    nameStartIndex = dateIndex + 2;
                }

                rawName = string.Join(' ', parts.Skip(nameStartIndex));
            }
            else
            {
                rawName = parts[^1];
            }

            if (string.IsNullOrWhiteSpace(rawName) || rawName is "." or "..")
            {
                continue;
            }

            string? linkTarget = null;
            if (isSymlink && rawName.Contains(" -> "))
            {
                var linkParts = rawName.Split([" -> "], StringSplitOptions.None);
                rawName = linkParts[0].Trim();
                linkTarget = linkParts.Length > 1 ? linkParts[1].Trim() : null;
            }

            var fullPath = CombineRemotePath(baseDirectory, rawName);
            entries.Add(new DeviceFileSystemEntry
            {
                Name = rawName,
                FullPath = fullPath,
                IsDirectory = isDir,
                IsSymlink = isSymlink,
                LinkTarget = linkTarget,
                Size = isDir ? -1 : size,
                Permissions = perm,
                ModificationTime = modTime
            });
        }

        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Pushes a local file to the remote device.
    /// <para>将本地文件上传推送到远程设备。</para>
    /// </summary>
    /// <summary>
    /// Pushes a local file to a remote destination (directory or file path).
    /// <para>将本地文件上传推送到远程目标（目录或文件路径）。</para>
    /// </summary>
    public async Task PushAsync(
        DetectedDevice device,
        string localPath,
        string remoteDestination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrEmpty(localPath);
        ArgumentException.ThrowIfNullOrEmpty(remoteDestination);

        var targetRemote = remoteDestination.EndsWith('/')
            ? CombineRemotePath(remoteDestination, Path.GetFileName(localPath))
            : remoteDestination;
        await PushFileAsync(device, localPath, targetRemote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls a remote file to a local destination directory or file path.
    /// <para>将远程文件拉取到本地目标目录或文件路径。</para>
    /// </summary>
    public async Task PullAsync(
        DetectedDevice device,
        string remotePath,
        string localDestination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrEmpty(remotePath);
        ArgumentException.ThrowIfNullOrEmpty(localDestination);

        var targetLocal = Directory.Exists(localDestination)
            ? Path.Combine(localDestination, Path.GetFileName(remotePath.TrimEnd('/')))
            : localDestination;
        await PullFileAsync(device, remotePath, targetLocal, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls a remote file to a local temporary file and returns the local file path.
    /// <para>将远程文件拉取到本地临时文件并返回本地文件路径。</para>
    /// </summary>
    /// <param name="device">Target device.<para>目标设备。</para></param>
    /// <param name="remotePath">Remote file path.<para>远程文件路径。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The local temporary file path.<para>本地临时文件路径。</para></returns>
    public async Task<string> PullToTempAsync(
        DetectedDevice device,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrEmpty(remotePath);

        var fileName = Path.GetFileName(remotePath.TrimEnd('/'));
        if (string.IsNullOrEmpty(fileName))
        {
            fileName = "downloaded_file";
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "UotanStudio", "DeviceTemp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var localPath = Path.Combine(tempDir, fileName);
        await PullFileAsync(device, remotePath, localPath, cancellationToken).ConfigureAwait(false);
        return localPath;
    }

    public async Task PushFileAsync(
        DetectedDevice device,
        string localPath,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrEmpty(localPath);
        ArgumentException.ThrowIfNullOrEmpty(remotePath);

        if (device.Protocol == DeviceProtocolKind.Hdc)
        {
            await HdcDeviceService.FromSettings().SendFileAsync(device, localPath, remotePath, cancellationToken).ConfigureAwait(false);
            return;
        }

        await App.DeviceMonitor.PushAdbFileAsync(device, localPath, remotePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls a remote file from the device to a local path.
    /// <para>将远程设备上的文件拉取下载到本地路径。</para>
    /// </summary>
    public async Task PullFileAsync(
        DetectedDevice device,
        string remotePath,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrEmpty(remotePath);
        ArgumentException.ThrowIfNullOrEmpty(localPath);

        if (device.Protocol == DeviceProtocolKind.Hdc)
        {
            await HdcDeviceService.FromSettings().ReceiveFileAsync(device, remotePath, localPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        await App.DeviceMonitor.PullAdbFileAsync(device, remotePath, localPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a file or directory on the remote device.
    /// <para>删除远程设备上的文件或目录。</para>
    /// </summary>
    public async Task DeleteAsync(
        DetectedDevice device,
        string remotePath,
        bool isDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var escaped = EscapeShellArgument(remotePath);
        var cmd = isDirectory ? $"rm -rf {escaped}" : $"rm {escaped}";
        var output = await RunShellAsync(device, cmd, cancellationToken).ConfigureAwait(false);
        if (output.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Failed to delete remote path: {output.Trim()}");
        }
    }

    /// <summary>
    /// Renames or moves a file or directory on the remote device.
    /// <para>在远程设备上重命名或移动文件/目录。</para>
    /// </summary>
    public async Task RenameAsync(
        DetectedDevice device,
        string oldRemotePath,
        string newRemotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var oldEscaped = EscapeShellArgument(oldRemotePath);
        var newEscaped = EscapeShellArgument(newRemotePath);
        var output = await RunShellAsync(device, $"mv {oldEscaped} {newEscaped}", cancellationToken).ConfigureAwait(false);
        if (output.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Failed to rename remote path: {output.Trim()}");
        }
    }

    /// <summary>
    /// Creates a directory on the remote device.
    /// <para>在远程设备上创建目录。</para>
    /// </summary>
    public async Task CreateDirectoryAsync(
        DetectedDevice device,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var escaped = EscapeShellArgument(remotePath);
        var output = await RunShellAsync(device, $"mkdir -p {escaped}", cancellationToken).ConfigureAwait(false);
        if (output.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Failed to create remote directory: {output.Trim()}");
        }
    }
}