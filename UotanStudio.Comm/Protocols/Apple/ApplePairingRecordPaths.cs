namespace UotanStudio.Comm.Protocols.Apple;

/// <summary>
/// Resolves the default Apple pairing-record directory so the lockdown and remote-pairing stores share one
/// writable per-user location.
/// <para>解析默认 Apple 配对记录目录，使 lockdown 与 remote-pairing 两个存储共享同一可写按用户位置。</para>
/// </summary>
/// <remarks>
/// Mirrors the host application's per-user data root rule: <c>%APPDATA%\UotanStudio</c> on Windows,
/// <c>~/Library/Application Support/UotanStudio</c> on macOS and the XDG data home on Linux, keeping the
/// records compatible with read-only install locations.
/// <para>与宿主应用的按用户数据根规则一致：Windows 为 <c>%APPDATA%\UotanStudio</c>，macOS 为
/// <c>~/Library/Application Support/UotanStudio</c>，Linux 为 XDG 数据目录，保证配对记录与只读安装位置兼容。</para>
/// </remarks>
internal static class ApplePairingRecordPaths
{
    /// <summary>Default pairing-record directory. <para>默认配对记录目录。</para></summary>
    /// <value>
    /// <c>&lt;per-user data root&gt;/apple-lockdown</c> for the running platform.
    /// <para>当前平台的 <c>&lt;按用户数据根&gt;/apple-lockdown</c>。</para>
    /// </value>
    internal static string DefaultDirectory { get; } = OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "UotanStudio", "apple-lockdown")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UotanStudio", "apple-lockdown");
}