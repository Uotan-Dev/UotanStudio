namespace UotanStudio.Services.Mirroring;

/// <summary>
/// <para>Locates the bundled scrcpy-server jar next to the running executable. The lookup is shared by
/// the mirror editor and the mirroring session tool so both deploy the exact jar the client protocol
/// targets.</para>
/// <para>在运行的可执行文件旁定位随附的 scrcpy 服务端 jar。定位逻辑由投屏编辑器与投屏会话工具共用，
/// 两者因此部署同一份客户端协议所对应的 jar。</para>
/// </summary>
public static class ScrcpyServerLocator
{
    /// <summary>
    /// <para>Returns the absolute path of the first existing candidate under the application base
    /// directory, preferring <c>Assets/scrcpy-server.jar</c> over a sibling copy next to the executable.</para>
    /// <para>返回应用基目录下首个存在的候选的绝对路径，优先 <c>Assets/scrcpy-server.jar</c>，其次才是可执行文件旁的副本。</para>
    /// </summary>
    /// <returns><para>定位到的 jar 绝对路径。</para>The absolute path of the located jar.</returns>
    /// <exception cref="FileNotFoundException">
    /// <para>未找到任何候选 jar 时抛出。</para>Raised when no candidate jar exists.</exception>
    public static string Resolve()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "scrcpy-server.jar"),
            Path.Combine(AppContext.BaseDirectory, "scrcpy-server.jar"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"scrcpy server jar not found. Place scrcpy-server {ScrcpyConstants.ServerVersion} at Assets/scrcpy-server.jar. "
            + $"未找到 scrcpy 服务端 jar。请将 scrcpy-server {ScrcpyConstants.ServerVersion} 放置到 Assets/scrcpy-server.jar。");
    }
}