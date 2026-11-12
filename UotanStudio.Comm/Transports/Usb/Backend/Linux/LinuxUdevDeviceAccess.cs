using System.Diagnostics;

namespace UotanStudio.Comm.Transports.Usb.Backend.Linux;

/// <summary>
/// The outcome of one Linux device-access configuration step: the elevated child succeeded, the authorization
/// was declined or unavailable, or the step failed for another reason.
/// <para>一次 Linux 设备访问配置步骤的结局：提权子进程成功、授权被拒绝或不可用，或步骤因其他原因失败。</para>
/// </summary>
public enum LinuxDeviceAccessStatus
{
    /// <summary>The configuration was applied. <para>配置已生效。</para></summary>
    Succeeded,

    /// <summary>The elevated step needs an authorization that was not granted.
    /// <para>提权步骤需要的授权未获得。</para></summary>
    ElevationRequired,

    /// <summary>The step failed; see the message. <para>步骤失败；详见消息。</para></summary>
    Failed,
}

/// <summary>
/// The result of one Linux device-access configuration step. <para>一次 Linux 设备访问配置步骤的结果。</para>
/// </summary>
/// <param name="Status">The outcome. <para>结局。</para></param>
/// <param name="Message">The error tail or diagnostics text, when any. <para>错误尾部或诊断文本（如有）。</para></param>
public sealed record LinuxDeviceAccessResult(LinuxDeviceAccessStatus Status, string? Message = null);

/// <summary>
/// Delegate that runs one shell script with elevated privileges in a separate child process and reports the
/// outcome. The calling process never elevates itself; the launcher owns how the privilege prompt is presented
/// (<c>pkexec</c> for graphical sessions is the default, and tests may inject a headless runner).
/// <para>以独立子进程执行一段提权 shell 脚本并上报结果的委托。调用方进程自身绝不提权；启动器决定提权提示的
/// 呈现方式（图形会话默认 <c>pkexec</c>，测试可注入无界面运行器）。</para>
/// </summary>
/// <param name="script">The shell script to run elevated.<para>要提权执行的 shell 脚本。</para></param>
/// <returns>The operation result mapped from the child's exit state.
/// <para>由子进程退出状态映射出的操作结果。</para></returns>
public delegate LinuxDeviceAccessResult ElevatedScriptRunner(string script);

/// <summary>
/// Configures kernel-side access for one USB device on Linux: an udev rule grants every user read/write access
/// to the device node, clears the node's <c>MODALIAS</c> so udev never asks for a serial-driver module, a
/// modprobe block keeps the usb-serial family from being loaded while the configuration is active, and the
/// elevated step unbinds whatever driver currently holds the device and unloads those modules, so the device is
/// usable through usbfs immediately. Unconfiguration removes the rule and the block again, restoring module
/// loadability. Everything privileged runs inside one elevated child process; this class only builds the script
/// and maps the exit state.
/// <para>在 Linux 上为一个 USB 设备配置内核侧访问：udev 规则向所有用户授予设备节点读写权限并清空该节点的
/// <c>MODALIAS</c>，使 udev 不会请求串口驱动模块；配置生效期间由 modprobe 屏蔽阻止 usb-serial 族被加载；
/// 提权步骤解绑当前占用该设备的驱动并卸载这些模块，使设备立即可经 usbfs 使用。取消配置移除规则与屏蔽，
/// 恢复模块可加载性。所有特权操作都在单个提权子进程内完成；本类只负责构建脚本并映射退出状态。</para>
/// </summary>
public static class LinuxUdevDeviceAccess
{
    /// <summary>The file name prefix of the per-device udev rule this class manages.
    /// <para>本类管理的按设备 udev 规则的文件名前缀。</para></summary>
    public const string RuleFileNamePrefix = "51-uotan-device-";

    /// <summary>The modprobe block file the configuration installs and unconfiguration removes.
    /// <para>配置安装、取消配置移除的 modprobe 屏蔽文件。</para></summary>
    public const string BlockFileName = "uotan-block-usb-serial.conf";

    /// <summary>The usb-serial driver modules that would claim EDL-style vendor-specific devices and are
    /// blocked while the configuration is active.
    /// <para>配置生效期间被屏蔽的 usb-serial 驱动模块——它们会抢占 EDL 风格的厂商专用设备。</para></summary>
    private static readonly string[] BlockedModules =
    [
        "qcserial",
        "qcaux",
        "usb_wwan",
        "usbserial",
        "usbserial_generic",
    ];

    /// <summary>
    /// Reports whether the access configuration for this vendor/product pair is currently installed, by checking
    /// for the rule file this class writes. The check is read-only and needs no elevation.
    /// <para>报告该厂商/产品组合的访问配置当前是否已安装，依据是本类写入的规则文件是否存在。检查只读且无需
    /// 提权。</para>
    /// </summary>
    /// <param name="vendorId">The USB vendor id. <para>USB 厂商 ID。</para></param>
    /// <param name="productId">The USB product id. <para>USB 产品 ID。</para></param>
    /// <returns><see langword="true"/> when the rule file exists.
    /// <para>规则文件存在时为 <see langword="true"/>。</para></returns>
    public static bool IsConfigured(ushort vendorId, ushort productId)
        => File.Exists(Path.Combine(RulesDirectory, RuleFileName(vendorId, productId)));

    /// <summary>
    /// Builds the name of the per-device rule file. <para>构建按设备规则文件的文件名。</para>
    /// </summary>
    /// <param name="vendorId">The USB vendor id. <para>USB 厂商 ID。</para></param>
    /// <param name="productId">The USB product id. <para>USB 产品 ID。</para></param>
    /// <returns>The rule file name, for example <c>51-uotan-device-05c6-9008.rules</c>.
    /// <para>规则文件名，例如 <c>51-uotan-device-05c6-9008.rules</c>。</para></returns>
    public static string RuleFileName(ushort vendorId, ushort productId)
        => $"{RuleFileNamePrefix}{vendorId:x4}-{productId:x4}.rules";

    /// <summary>
    /// Builds the rule content: world read/write access for the device node plus a cleared <c>MODALIAS</c>, so
    /// udev neither exposes a permission barrier nor asks for a claiming driver module.
    /// <para>构建规则内容：设备节点对全体用户可读写，且清空 <c>MODALIAS</c>，使 udev 既不设权限屏障也不请求
    /// 抢占型驱动模块。</para>
    /// </summary>
    /// <param name="vendorId">The USB vendor id. <para>USB 厂商 ID。</para></param>
    /// <param name="productId">The USB product id. <para>USB 产品 ID。</para></param>
    /// <returns>The single-line udev rule. <para>单行 udev 规则。</para></returns>
    public static string RuleFileContent(ushort vendorId, ushort productId)
        => $"ACTION==\"add\"|\"change\", SUBSYSTEM==\"usb\", ATTR{{idVendor}}==\"{vendorId:x4}\", " +
           $"ATTR{{idProduct}}==\"{productId:x4}\", MODE:=\"0666\", ENV{{MODALIAS}}=\"\"";

    /// <summary>
    /// Installs the access configuration for one device: writes the per-device udev rule, installs the
    /// usb-serial module block, unbinds the drivers currently holding the device, unloads the blocked modules
    /// and re-applies the rules. Everything runs inside one elevated child process launched by
    /// <paramref name="elevatedRunner"/>; the default runner invokes <c>pkexec</c>, which presents the desktop's
    /// authorization prompt.
    /// <para>为一个设备安装访问配置：写入按设备 udev 规则、安装 usb-serial 模块屏蔽、解绑当前占用设备的驱动、
    /// 卸载被屏蔽的模块并重新应用规则。所有操作都在 <paramref name="elevatedRunner"/> 启动的单个提权子进程内
    /// 完成；默认运行器调用 <c>pkexec</c>，由桌面呈现授权提示。</para>
    /// </summary>
    /// <param name="vendorId">The USB vendor id. <para>USB 厂商 ID。</para></param>
    /// <param name="productId">The USB product id. <para>USB 产品 ID。</para></param>
    /// <param name="elevatedRunner">An optional launcher override for headless or test environments; the default
    /// invokes <c>pkexec</c>. <para>可选的运行器覆盖，供无界面或测试环境使用；默认调用 <c>pkexec</c>。</para></param>
    /// <returns>The operation result; never throws. <para>操作结果；绝不抛出。</para></returns>
    public static LinuxDeviceAccessResult Configure(
        ushort vendorId, ushort productId, ElevatedScriptRunner? elevatedRunner = null)
    {
        var script = FormattableString.Invariant($"""
            set -e
            cat > {RulesDirectory}/{RuleFileName(vendorId, productId)} <<'UOTAN_RULE'
            {RuleFileContent(vendorId, productId)}
            UOTAN_RULE
            cat > {ModprobeDirectory}/{BlockFileName} <<'UOTAN_BLOCK'
            {BlockFileContent}
            UOTAN_BLOCK
            udevadm control --reload-rules
            for dev in /sys/bus/usb/devices/*; do
              [ -f "$dev/idVendor" ] || continue
              [ "$(cat "$dev/idVendor")" = "{vendorId:x4}" ] || continue
              [ "$(cat "$dev/idProduct")" = "{productId:x4}" ] || continue
              for iface in "$dev":*; do
                [ -e "$iface/driver" ] || continue
                drv=$(basename "$(readlink -f "$iface/driver")")
                echo -n "$(basename "$iface")" > "/sys/bus/usb/drivers/$drv/unbind" 2>/dev/null || true
              done
            done
            /usr/sbin/modprobe -r {string.Join(' ', BlockedModules)} 2>/dev/null || true
            udevadm trigger
            """);
        return RunElevated(script, elevatedRunner);
    }

    /// <summary>
    /// Removes the access configuration again: deletes the per-device rule and the usb-serial module block and
    /// re-applies the rules, so the kernel may load and bind claiming drivers as it did before the
    /// configuration. Everything runs inside one elevated child process.
    /// <para>移除访问配置：删除按设备规则与 usb-serial 模块屏蔽并重新应用规则，使内核可以像配置前一样加载并
    /// 绑定抢占型驱动。所有操作都在单个提权子进程内完成。</para>
    /// </summary>
    /// <param name="vendorId">The USB vendor id. <para>USB 厂商 ID。</para></param>
    /// <param name="productId">The USB product id. <para>USB 产品 ID。</para></param>
    /// <param name="elevatedRunner">An optional launcher override for headless or test environments; the default
    /// invokes <c>pkexec</c>. <para>可选的运行器覆盖，供无界面或测试环境使用；默认调用 <c>pkexec</c>。</para></param>
    /// <returns>The operation result; never throws. <para>操作结果；绝不抛出。</para></returns>
    public static LinuxDeviceAccessResult Unconfigure(
        ushort vendorId, ushort productId, ElevatedScriptRunner? elevatedRunner = null)
    {
        var script = FormattableString.Invariant($"""
            set -e
            rm -f {RulesDirectory}/{RuleFileName(vendorId, productId)}
            rm -f {ModprobeDirectory}/{BlockFileName}
            udevadm control --reload-rules
            udevadm trigger
            """);
        return RunElevated(script, elevatedRunner);
    }

    /// <summary>The directory the per-device rules are written to.
    /// <para>按设备规则的写入目录。</para></summary>
    private const string RulesDirectory = "/etc/udev/rules.d";

    /// <summary>The directory the modprobe block is written to.
    /// <para>modprobe 屏蔽的写入目录。</para></summary>
    private const string ModprobeDirectory = "/etc/modprobe.d";

    /// <summary>
    /// Reports whether the default elevation launcher exists on this system, so a caller can decide whether
    /// the configuration can actually run here.
    /// <para>报告系统上是否存在默认提权启动器，使调用方能判断配置在此处能否实际执行。</para>
    /// </summary>
    /// <value><see langword="true"/> on Linux when <c>pkexec</c> is installed.
    /// <para>Linux 上安装了 <c>pkexec</c> 时为 <see langword="true"/>。</para></value>
    public static bool IsLauncherAvailable => OperatingSystem.IsLinux() && File.Exists(PkExecPath);

    /// <summary>The path of the default elevation launcher binary.
    /// <para>默认提权启动器二进制的路径。</para></summary>
    private const string PkExecPath = "/usr/bin/pkexec";

    /// <summary>The modprobe block content, intercepting every module load request for the usb-serial family.
    /// <para>modprobe 屏蔽内容，拦截 usb-serial 族的全部模块加载请求。</para></summary>
    private static string BlockFileContent =>
        string.Join('\n', BlockedModules.Select(module => $"install {module} /bin/true")) + "\n";

    /// <summary>
    /// Runs the script through the elevated runner, defaulting to <c>pkexec</c>, and maps the child's exit state
    /// onto the operation result: success, a declined authorization, or a failure carrying the error tail.
    /// <para>经提权运行器执行脚本，默认使用 <c>pkexec</c>，并把子进程退出状态映射为操作结果：成功、被拒绝的
    /// 授权，或携带错误尾部信息的失败。</para>
    /// </summary>
    /// <param name="script">The shell script to run elevated.<para>要提权执行的 shell 脚本。</para></param>
    /// <param name="elevatedRunner">An optional launcher override. <para>可选的运行器覆盖。</para></param>
    /// <returns>The operation result; never throws. <para>操作结果；绝不抛出。</para></returns>
    private static LinuxDeviceAccessResult RunElevated(string script, ElevatedScriptRunner? elevatedRunner)
    {
        try
        {
            return elevatedRunner is not null
                ? elevatedRunner(script)
                : RunPkExec(script);
        }
        catch (Exception ex)
        {
            return new LinuxDeviceAccessResult(LinuxDeviceAccessStatus.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Runs the script through <c>pkexec /bin/sh -c</c> so the desktop's polkit agent presents the authorization
    /// prompt, and maps the known <c>pkexec</c> exit codes: an authorization that was declined or could not be
    /// presented reports elevation-required, everything else reports a failure with the error tail.
    /// <para>经 <c>pkexec /bin/sh -c</c> 执行脚本，由桌面的 polkit 代理呈现授权提示，并映射已知的
    /// <c>pkexec</c> 退出码：被拒绝或无法呈现的授权上报需要提权，其余上报带错误尾部信息的失败。</para>
    /// </summary>
    /// <param name="script">The shell script to run elevated.<para>要提权执行的 shell 脚本。</para></param>
    /// <returns>The operation result; never throws. <para>操作结果；绝不抛出。</para></returns>
    private static LinuxDeviceAccessResult RunPkExec(string script)
    {
        var start = new ProcessStartInfo
        {
            FileName = PkExecPath,
            ArgumentList =
            {
                "/bin/sh",
                "-c",
                script,
            },
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start);
        if (process is null)
        {
            return new LinuxDeviceAccessResult(LinuxDeviceAccessStatus.Failed, "pkexec could not be started.");
        }

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode switch
        {
            0 => new LinuxDeviceAccessResult(LinuxDeviceAccessStatus.Succeeded),
            126 or 127 => new LinuxDeviceAccessResult(LinuxDeviceAccessStatus.ElevationRequired, stderr),
            _ => new LinuxDeviceAccessResult(LinuxDeviceAccessStatus.Failed, stderr),
        };
    }
}