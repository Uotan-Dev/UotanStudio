namespace UotanStudio.Projects;

/// <summary>
/// Declarative capability a visual-plugin package may request and a user may authorize at install time.
/// <para>可视化插件包可以声明、用户可以在安装时授权的声明式能力。</para>
/// </summary>
/// <remarks>
/// Permissions gate script tool entries and the execution of packages whose graph touches the matching
/// host surface. They are declarations, not a sandbox: code running in this process can still do anything
/// the process can do.
/// <para>权限用于约束脚本工具条目以及图触及对应宿主表面的包的执行。它们是声明，不是沙箱：同一进程内的代码仍能完成进程本身能完成的一切。</para>
/// </remarks>
[Flags]
public enum PluginPermission
{
    /// <summary>No capability is granted. <para>不授予任何能力。</para></summary>
    None = 0,

    /// <summary>Writes one image to a device partition. <para>向设备分区写入镜像。</para></summary>
    DeviceFlash = 1 << 0,

    /// <summary>Erases a device partition. <para>擦除设备分区。</para></summary>
    DeviceErase = 1 << 1,

    /// <summary>Runs a shell command on a device. <para>在设备上执行 shell 命令。</para></summary>
    DeviceShell = 1 << 2,

    /// <summary>Wipes data through the device recovery path. <para>通过设备恢复路径清除数据。</para></summary>
    DeviceRecovery = 1 << 3,

    /// <summary>Reboots a device or switches its active slot. <para>重启设备或切换活动槽位。</para></summary>
    DeviceControl = 1 << 4,

    /// <summary>Downloads remote content over the network. <para>通过网络下载远端内容。</para></summary>
    Network = 1 << 5,

    /// <summary>Writes files into the project or workspace. <para>向项目或工作区写入文件。</para></summary>
    FileWrite = 1 << 6,

    /// <summary>Installs a package onto a device. <para>向设备安装软件包。</para></summary>
    PackageInstall = 1 << 7,

    /// <summary>Every declared capability. <para>全部声明式能力。</para></summary>
    All = DeviceFlash | DeviceErase | DeviceShell | DeviceRecovery | DeviceControl | Network | FileWrite | PackageInstall
}

/// <summary>
/// Derivation and name handling for <see cref="PluginPermission"/> values.
/// <para><see cref="PluginPermission"/> 值的推导与名称处理。</para>
/// </summary>
public static class PluginPermissions
{
    private static readonly PluginPermission[] OrderedPermissions =
    [
        PluginPermission.DeviceFlash,
        PluginPermission.DeviceErase,
        PluginPermission.DeviceShell,
        PluginPermission.DeviceRecovery,
        PluginPermission.DeviceControl,
        PluginPermission.Network,
        PluginPermission.FileWrite,
        PluginPermission.PackageInstall
    ];

    /// <summary>
    /// Lists every single permission in declaration order.
    /// <para>按声明顺序列出每一个单项权限。</para>
    /// </summary>
    /// <value>The eight declared permissions, excluding <see cref="PluginPermission.None"/> and <see cref="PluginPermission.All"/>.
    /// <para>八个已声明权限，不含 <see cref="PluginPermission.None"/> 与 <see cref="PluginPermission.All"/>。</para></value>
    public static IReadOnlyList<PluginPermission> Known => OrderedPermissions;

    /// <summary>
    /// Derives the permissions requested by a package from its declared permissions, required node types and capabilities.
    /// <para>由包声明的权限、必需节点类型与能力推导该包请求的权限。</para>
    /// </summary>
    /// <param name="package">Package to inspect; embedded composite scopes are included.
    /// <para>待检查的包；内嵌复合作用域一并计入。</para></param>
    /// <returns>The union of declared permissions and graph-derived permissions, <see cref="PluginPermission.None"/> when the package requests nothing.
    /// <para>声明权限与图推导权限的并集；包未请求任何能力时为 <see cref="PluginPermission.None"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 为 <see langword="null"/>。</para></exception>
    public static PluginPermission Derive(VisualPluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var result = PluginPermission.None;
        foreach (var scope in Scopes(package, 0))
        {
            result |= DeriveScope(scope);
        }
        return result;
    }

    /// <summary>
    /// Returns the permissions that a required set still lacks.
    /// <para>返回所需权限集合仍然缺少的权限。</para>
    /// </summary>
    /// <param name="required">Permissions the package needs. <para>包需要的权限。</para></param>
    /// <param name="granted">Permissions the user authorized. <para>用户已授权的权限。</para></param>
    /// <returns>Every bit set in <paramref name="required"/> but not in <paramref name="granted"/>.
    /// <para>在 <paramref name="required"/> 中置位但不在 <paramref name="granted"/> 中的全部位。</para></returns>
    public static PluginPermission Missing(PluginPermission required, PluginPermission granted) => required & ~granted;

    /// <summary>
    /// Formats permissions as a comma-separated list of stable names.
    /// <para>把权限格式化为以逗号分隔的稳定名称列表。</para>
    /// </summary>
    /// <param name="permissions">Permissions to format. <para>待格式化的权限。</para></param>
    /// <returns>The names in declaration order, or an empty string for <see cref="PluginPermission.None"/>.
    /// <para>按声明顺序排列的名称；<see cref="PluginPermission.None"/> 时为空字符串。</para></returns>
    public static string Format(PluginPermission permissions) => string.Join(", ",
        OrderedPermissions.Where(permission => permissions.HasFlag(permission)).Select(permission => permission.ToString()));

    /// <summary>
    /// Parses one stable permission name without regard to case.
    /// <para>按不区分大小写的方式解析单个稳定权限名称。</para>
    /// </summary>
    /// <param name="name">Name to parse. <para>待解析的名称。</para></param>
    /// <param name="permission">Parsed permission, or <see cref="PluginPermission.None"/> when the name is unknown.
    /// <para>解析得到的权限；名称未知时为 <see cref="PluginPermission.None"/>。</para></param>
    /// <returns><see langword="true"/> when <paramref name="name"/> names exactly one permission.
    /// <para><paramref name="name"/> 恰好对应一个权限时为 <see langword="true"/>。</para></returns>
    public static bool TryParse(string? name, out PluginPermission permission)
    {
        foreach (var candidate in OrderedPermissions)
        {
            if (string.Equals(candidate.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                permission = candidate;
                return true;
            }
        }
        permission = PluginPermission.None;
        return false;
    }

    private static PluginPermission DeriveScope(VisualPluginPackage scope)
    {
        var result = PluginPermission.None;
        if (scope.Manifest?.Permissions is { } declared)
        {
            foreach (var name in declared)
            {
                if (TryParse(name, out var permission))
                {
                    result |= permission;
                }
            }
        }
        if (scope.Manifest?.RequiredNodeTypes is { } nodeTypes)
        {
            foreach (var typeKey in nodeTypes)
            {
                result |= FromNodeType(typeKey);
            }
        }
        if (scope.Manifest?.Capabilities is { } capabilities)
        {
            foreach (var capability in capabilities)
            {
                result |= FromCapability(capability);
            }
        }
        return result;
    }

    private static IEnumerable<VisualPluginPackage> Scopes(VisualPluginPackage package, int depth)
    {
        if (depth > 32)
        {
            yield break;
        }
        yield return package;
        if (package.Graph?.Nodes is not { } nodes)
        {
            yield break;
        }
        foreach (var node in nodes)
        {
            if (node?.CompositePackage is not { } child)
            {
                continue;
            }
            foreach (var scope in Scopes(child, depth + 1))
            {
                yield return scope;
            }
        }
    }

    private static PluginPermission FromNodeType(string? typeKey)
    {
        if (string.IsNullOrWhiteSpace(typeKey))
        {
            return PluginPermission.None;
        }
        return typeKey switch
        {
            "Device.Partition.Flash" => PluginPermission.DeviceFlash,
            "Device.Partition.Erase" or "Device.Storage.EraseAll" => PluginPermission.DeviceErase,
            "Device.Shell.Command" => PluginPermission.DeviceShell,
            "Device.Recovery.Clean" => PluginPermission.DeviceRecovery,
            "Device.Power.Reboot" or "Device.Slot.Switch" => PluginPermission.DeviceControl,
            "File.Bytes.Write" or "File.Text.Publish" or "File.Artifact.Publish"
                or "Workspace.File.Delete" or "Workspace.File.Import" => PluginPermission.FileWrite,
            _ when typeKey.StartsWith("Network.", StringComparison.Ordinal) => PluginPermission.Network,
            _ when typeKey.StartsWith("Package.", StringComparison.Ordinal) => PluginPermission.PackageInstall,
            _ => PluginPermission.None
        };
    }

    private static PluginPermission FromCapability(string? capability) => capability switch
    {
        "RequiresNetwork" => PluginPermission.Network,
        "PublishesFile" => PluginPermission.FileWrite,
        _ => PluginPermission.None
    };
}