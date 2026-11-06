using Avalonia.Controls;
using UotanStudio.Services.Flash;

namespace UotanStudio.Services.Ui;

/// <summary>
/// Desktop elevation prompt: shows the localized privilege request in a modal message window and reports
/// whether the user allowed the privileged write.
/// <para>桌面提权提示：在模态消息窗口中显示本地化的权限请求，并反馈用户是否允许以特权写入。</para>
/// </summary>
public sealed class FlashElevationPrompt : IFlashElevationPrompt
{
    private readonly Func<Window?> _owner;

    /// <summary>
    /// Creates the prompt that parents its window to the owner supplied by the caller.
    /// <para>创建提示，其窗口以调用方提供的所有者窗口为父窗口。</para>
    /// </summary>
    /// <param name="owner">Supplies the owner window, or <see langword="null"/> when none is available.
    /// <para>提供所有者窗口；没有可用窗口时返回 <see langword="null"/>。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is <see langword="null"/>.
    /// <para><paramref name="owner"/> 为 <see langword="null"/>。</para></exception>
    public FlashElevationPrompt(Func<Window?> owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmElevationAsync(FlashElevationReason reason, string detail)
    {
        var language = LanguageService.Instance;
        var title = language.GetString(reason switch
        {
            FlashElevationReason.AccessDenied => "DiskFlash_ElevationDeniedTitle",
            FlashElevationReason.ElevationUnavailable => "DiskFlash_ElevationUnavailableTitle",
            FlashElevationReason.PlatformUnsupported => "DiskFlash_ElevationUnavailableTitle",
            _ => "DiskFlash_ElevationTitle",
        });
        var body = language.GetString(reason switch
        {
            FlashElevationReason.AccessDenied => "DiskFlash_ElevationDeniedBody",
            FlashElevationReason.ElevationUnavailable => "DiskFlash_ElevationUnavailableBody",
            FlashElevationReason.PlatformUnsupported => "DiskFlash_ElevationUnsupportedBody",
            _ => "DiskFlash_ElevationBody",
        });
        var owner = _owner();
        return owner is not null &&
            await DialogService.ConfirmAsync(owner, title, body + Environment.NewLine + Environment.NewLine + detail,
                language.GetString("DiskFlash_ElevationContinue"),
                language.GetString("DiskFlash_ElevationCancel")).ConfigureAwait(true);
    }
}