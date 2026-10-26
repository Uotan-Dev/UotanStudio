namespace UotanStudio.Services.Devices;

/// <summary>
/// Reads whether the device grants <c>android.permission.INJECT_EVENTS</c> to <c>com.android.shell</c>, which is
/// the identity every ADB shell command and the deployed scrcpy server run as. Touch and key control cannot work
/// without it, and some vendor ROMs gate the grant behind a developer option instead of including it in the shell
/// package's install permissions.
/// <para>读取设备是否把 <c>android.permission.INJECT_EVENTS</c> 授予 <c>com.android.shell</c>——所有 ADB shell
/// 命令与部署的 scrcpy 服务端都以该身份运行。缺少该权限时触摸与按键控制无法工作，而部分厂商 ROM 会把它放到
/// 开发者选项后面，而不是随 shell 包一起安装授予。</para>
/// </summary>
internal static class InputInjectionProbe
{
    /// <summary>
    /// Shell command that prints the shell package header and every permission line mentioning the injection
    /// permission; the device-side filter keeps the dump small without losing the header that proves the probe ran.
    /// <para>打印 shell 包标题以及提及注入权限的每一行的 shell 命令；设备侧过滤保持转储较小，同时保留证明探测确实执行过的包标题。</para>
    /// </summary>
    internal const string Command = "dumpsys package com.android.shell | grep -E 'Package \\[|INJECT_EVENTS'";

    /// <summary>Deadline for the probe command; a slow package dump is treated as an unavailable probe.
    /// <para>探测命令的期限；转储过慢按探测不可用处理。</para></summary>
    internal const int TimeoutMs = 5000;

    private const string PackageHeader = "Package [com.android.shell]";
    private const string PermissionName = "android.permission.INJECT_EVENTS";
    private const string GrantedMarker = "granted=true";

    /// <summary>
    /// Parses the filtered package dump into a capability verdict: the package header must be present for the
    /// output to count as a probe at all, and a <c>granted=true</c> line is the only positive signal. A missing
    /// permission line therefore reports <see langword="false"/>, because a shell package that does not request
    /// the permission cannot inject either.
    /// <para>把过滤后的包转储解析为能力结论：只有出现包标题才视为有效探测，只有 <c>granted=true</c> 行才是
    /// 肯定信号。缺少权限行因此报告 <see langword="false"/>，因为未申请该权限的 shell 包同样无法注入。</para>
    /// </summary>
    /// <param name="output">Filtered <c>dumpsys package com.android.shell</c> output.
    /// <para>过滤后的 <c>dumpsys package com.android.shell</c> 输出。</para></param>
    /// <returns><see langword="true"/> when the grant is present, <see langword="false"/> when the probe ran but
    /// the grant is absent, and <see langword="null"/> when no package header makes the output unusable.
    /// <para>授权存在时为 <see langword="true"/>；探测执行但未授权时为 <see langword="false"/>；
    /// 输出缺少包标题而不可用时为 <see langword="null"/>。</para></returns>
    internal static bool? Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var sawPackage = false;
        var granted = false;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains(PackageHeader, StringComparison.Ordinal))
            {
                sawPackage = true;
                continue;
            }

            var index = line.IndexOf(PermissionName, StringComparison.Ordinal);
            if (index >= 0 &&
                line.AsSpan(index + PermissionName.Length).Contains(GrantedMarker, StringComparison.Ordinal))
            {
                granted = true;
            }
        }

        return sawPackage ? granted : null;
    }
}