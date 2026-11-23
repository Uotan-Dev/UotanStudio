namespace UotanStudio.Services.Remote;

/// <summary>
/// Factory for remote file system sessions plus the shared remote-path helpers used by the multi-column
/// file browser. Remote paths are always '/'-separated regardless of the host operating system.
/// <para>远程文件系统会话的工厂，以及多列文件浏览器共用的远程路径工具。远程路径始终以 '/' 分隔，与主机操作系统无关。</para>
/// </summary>
public sealed class RemoteFileSystemService
{
    private static readonly Lazy<RemoteFileSystemService> LazyInstance = new(() => new RemoteFileSystemService());

    /// <summary>Shared singleton instance.<para>共享单例实例。</para></summary>
    public static RemoteFileSystemService Instance => LazyInstance.Value;

    /// <summary>
    /// Test seam: when set, session creation returns this factory's result instead of opening a real connection,
    /// so column loading can be verified without a network endpoint. Never set in production.
    /// <para>测试接缝：设置后会话创建返回该工厂的结果而不建立真实连接，使列加载无需网络端点即可验证。生产代码从不设置。</para>
    /// </summary>
    internal Func<RemoteConnectionProfile, IRemoteFileSystem>? SessionFactoryOverride { get; set; }

    /// <summary>
    /// Creates a session for the given profile. SCP profiles browse and transfer over the SSH SFTP
    /// subsystem, which OpenSSH servers provide alongside the SCP command.
    /// <para>按配置档案创建会话。SCP 档案经由 SSH SFTP 子系统进行浏览与传输，OpenSSH 服务器在提供 SCP 命令的同时也提供该子系统。</para>
    /// </summary>
    /// <param name="profile">The connection profile.<para>连接配置档案。</para></param>
    /// <returns>The new session.<para>新建的会话。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The profile's protocol is unknown.<para>配置档案的协议未知。</para></exception>
    public IRemoteFileSystem CreateSession(RemoteConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (SessionFactoryOverride is { } factory)
        {
            return factory(profile);
        }

        return profile.Protocol switch
        {
            RemoteProtocol.Sftp or RemoteProtocol.Scp => new SshRemoteFileSystem(profile),
            RemoteProtocol.Ftp => new FtpRemoteFileSystem(profile),
            RemoteProtocol.WebDav => new WebDavRemoteFileSystem(profile),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile.Protocol, "Unknown remote protocol."),
        };
    }

    /// <summary>
    /// Returns the column's start path: the profile's initial path when set, otherwise the server root.
    /// <para>返回列的起始路径：配置档案指定了初始路径时使用它，否则使用服务器根目录。</para>
    /// </summary>
    /// <param name="profile">The connection profile.<para>连接配置档案。</para></param>
    /// <returns>The start path.<para>起始路径。</para></returns>
    public static string GetDefaultPath(RemoteConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var initial = profile.InitialPath?.Trim();
        return string.IsNullOrEmpty(initial) ? "/" : initial;
    }

    /// <summary>
    /// Combines a remote parent directory and an entry name; a root parent yields "/name".
    /// <para>拼接远程父目录与条目名；父目录为根时得到 "/name"。</para>
    /// </summary>
    /// <param name="parentDirectory">The parent directory path.<para>父目录路径。</para></param>
    /// <param name="name">The entry name.<para>条目名。</para></param>
    /// <returns>The combined path.<para>拼接后的路径。</para></returns>
    public static string CombinePath(string parentDirectory, string name)
    {
        ArgumentNullException.ThrowIfNull(parentDirectory);
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = parentDirectory.TrimEnd('/');
        return trimmed.Length == 0 ? "/" + name : trimmed + "/" + name;
    }

    /// <summary>
    /// Returns the parent directory of a remote path; the root's parent is the root itself.
    /// <para>返回远程路径的父目录；根目录的父目录是其自身。</para>
    /// </summary>
    /// <param name="remotePath">The remote path.<para>远程路径。</para></param>
    /// <returns>The parent path.<para>父目录路径。</para></returns>
    public static string GetParentPath(string remotePath)
    {
        ArgumentNullException.ThrowIfNull(remotePath);
        var trimmed = remotePath.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        if (index < 0)
        {
            return "/";
        }

        return index == 0 ? "/" : trimmed[..index];
    }

    /// <summary>
    /// Extracts the last segment of a remote path as the entry name; the root yields an empty string.
    /// <para>取远程路径的最后一段作为条目名；根目录返回空串。</para>
    /// </summary>
    /// <param name="remotePath">The remote path.<para>远程路径。</para></param>
    /// <returns>The entry name.<para>条目名。</para></returns>
    public static string GetEntryName(string remotePath)
    {
        ArgumentNullException.ThrowIfNull(remotePath);
        var trimmed = remotePath.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }
}