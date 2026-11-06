namespace UotanStudio.Services.Flash;

/// <summary>
/// Why the host asks the user to allow a privileged write.
/// <para>宿主请求用户允许以特权写入的原因。</para>
/// </summary>
public enum FlashElevationReason
{
    /// <summary>The current process may not open the selected drives for writing.
    /// <para>当前进程无权以写方式打开选中的驱动器。</para>
    /// </summary>
    PrivilegeMissing,

    /// <summary>The write already tried in this process was refused by the platform.
    /// <para>本进程内的写入已被平台拒绝。</para>
    /// </summary>
    AccessDenied,

    /// <summary>This host cannot raise its own privilege for a write, for example a framework-dependent build.
    /// <para>该宿主无法为写入提升自身权限，例如依赖框架启动的构建。</para>
    /// </summary>
    ElevationUnavailable,

    /// <summary>This platform has no elevated writing helper.
    /// <para>该平台没有提权写入助手。</para>
    /// </summary>
    PlatformUnsupported,
}

/// <summary>
/// Asks the user whether a flash operation may continue in a process started with administrator rights.
/// <para>询问用户是否允许在带管理员权限的进程中继续烧录。</para>
/// </summary>
public interface IFlashElevationPrompt
{
    /// <summary>
    /// Shows the elevation request and waits for the user's decision.
    /// <para>显示提权请求并等待用户决定。</para>
    /// </summary>
    /// <param name="reason">Why elevation is needed.<para>需要提权的原因。</para></param>
    /// <param name="detail">The localized detail naming the image and the target drives.
    /// <para>点名镜像与目标驱动器的本地化说明。</para></param>
    /// <returns><see langword="true"/> when the user allowed the privileged write.
    /// <para>用户允许以特权写入时为 <see langword="true"/>。</para></returns>
    Task<bool> ConfirmElevationAsync(FlashElevationReason reason, string detail);
}

/// <summary>
/// Fallback prompt used when no host shell is attached; it never grants elevation.
/// <para>未附加宿主 Shell 时使用的回退提示；它从不授予提权。</para>
/// </summary>
internal sealed class NullFlashElevationPrompt : IFlashElevationPrompt
{
    /// <summary>
    /// The shared no-op instance.<para>共享的空实现实例。</para>
    /// </summary>
    internal static NullFlashElevationPrompt Instance { get; } = new();

    public Task<bool> ConfirmElevationAsync(FlashElevationReason reason, string detail) =>
        Task.FromResult(false);
}