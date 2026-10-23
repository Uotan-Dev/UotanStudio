using System.Text;
using UotanStudio.Comm.Contracts;

namespace UotanStudio.Mirroring;

/// <summary>
/// Deploys, installs and launches the ohscrcpy HAP on an HDC target. The HDC session and its lease
/// belong to the caller; this class only issues the device-side bundle, ability and package commands.
/// <para>在 HDC 目标上部署、安装并启动 ohscrcpy HAP。HDC 会话与其租约由调用方持有；本类只发送设备侧的
/// 包管理、Ability 与安装命令。</para>
/// </summary>
internal static class OhscrcpyServerManager
{
    /// <summary>Checks whether the ohscrcpy device package is installed.
    /// <para>检查 ohscrcpy 设备包是否已安装。</para>
    /// </summary>
    /// <param name="session">Connected HDC session. <para>已连接的 HDC 会话。</para></param>
    /// <returns><see langword="true"/> when the bundle manager reports the package.
    /// <para>包管理器报告该包时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    internal static bool IsInstalled(IHdcDeviceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var output = Decode(session.ExecuteCommand("bm dump -n " + OhscrcpyConstants.BundleName, 15_000).Stdout);
        return !output.Contains("error:", StringComparison.OrdinalIgnoreCase) &&
               output.Contains(OhscrcpyConstants.BundleName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Starts the installed <c>ScrcpyService</c> ability and returns the device's confirmation text.
    /// <para>启动已安装的 <c>ScrcpyService</c> Ability，并返回设备的确认文本。</para>
    /// </summary>
    /// <param name="session">Connected HDC session. <para>已连接的 HDC 会话。</para></param>
    /// <param name="serial">Target connect key used in failure messages. <para>失败信息中使用的目标连接键。</para></param>
    /// <returns>The successful ability-start output. <para>成功的 Ability 启动输出。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="IOException">Every launch form was refused. <para>所有启动形式都被拒绝。</para></exception>
    internal static string StartServer(IHdcDeviceSession session, string serial)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        string[] commands =
        [
            $"aa start -a {OhscrcpyConstants.ServiceAbilityName} -b {OhscrcpyConstants.BundleName}",
            $"aa start -b {OhscrcpyConstants.BundleName}",
        ];
        var failure = new StringBuilder();
        foreach (var command in commands)
        {
            var output = Decode(session.ExecuteCommand(command, 15_000).Stdout);
            if (!output.Contains("error:", StringComparison.OrdinalIgnoreCase) &&
                !output.Contains("[Fail]", StringComparison.OrdinalIgnoreCase))
            {
                return output;
            }

            failure.AppendLine(output);
        }

        throw new IOException(
            $"The device refused to start {OhscrcpyConstants.BundleName}: {failure.ToString().Trim()}. "
            + "The device must be unlocked and the package installed. 设备拒绝启动 "
            + $"{OhscrcpyConstants.BundleName}：{failure.ToString().Trim()}。设备须处于解锁状态且已安装该包。");
    }

    /// <summary>Installs one signed HAP over the connected HDC session.
    /// <para>经已连接的 HDC 会话安装一个已签名 HAP。</para>
    /// </summary>
    /// <param name="session">Connected HDC session. <para>已连接的 HDC 会话。</para></param>
    /// <param name="hapPath">Absolute path of the HAP. <para>HAP 的绝对路径。</para></param>
    /// <returns>The bundle-manager completion text. <para>包管理器的完成文本。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.
    /// <para><paramref name="session"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="hapPath"/> is empty.
    /// <para><paramref name="hapPath"/> 为空。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="hapPath"/> does not exist.
    /// <para><paramref name="hapPath"/> 不存在。</para></exception>
    /// <exception cref="NotSupportedException">The HDC server route does not expose the package channel.
    /// <para>HDC server 路由没有开放包传输通道。</para></exception>
    internal static string InstallHap(IHdcDeviceSession session, string hapPath)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(hapPath);
        if (!File.Exists(hapPath))
        {
            throw new FileNotFoundException("The ohscrcpy HAP does not exist.", hapPath);
        }
        try
        {
            return session.CreateAppClient().Install(hapPath);
        }
        catch (NotSupportedException error)
        {
            throw new NotSupportedException(
                $"The HDC server route cannot transfer {Path.GetFileName(hapPath)}; run "
                + $"'hdc install -r \"{hapPath}\"' once, then retry the mirror.", error);
        }
    }

    private static string Decode(byte[] stdout)
        => Encoding.UTF8.GetString(stdout).TrimEnd('\0', '\r', '\n', ' ');
}