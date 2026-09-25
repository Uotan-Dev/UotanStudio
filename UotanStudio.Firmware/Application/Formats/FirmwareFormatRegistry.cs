using UotanStudio.Firmware.SharedKernel;
namespace UotanStudio.Firmware.Application.Formats;

/// <summary>
/// Single registration point that publishes every built-in read-only format handler to
/// <see cref="FormatDetector"/>. Detection first probes handlers whose <c>FileExtensions</c> claim
/// the file extension and only selects one after its <c>CanHandle</c> content check passes; when no
/// extension candidate validates, every registered handler gets the same content check in
/// registration order. An extension match therefore never selects a handler on its own, a handler
/// whose check throws is skipped, and re-registering an existing <c>FormatName</c> is ignored.
/// <para>把全部内置只读格式处理器发布到 <see cref="FormatDetector"/> 的唯一注册入口。检测先探测
/// <c>FileExtensions</c> 声明了该扩展名的处理器，且只有其 <c>CanHandle</c> 内容校验通过才会选中；
/// 若所有扩展名候选均未通过，则按注册顺序对每个已注册处理器执行同样的内容校验。因此仅扩展名匹配
/// 从不决定选择；校验抛出异常的处理器会被跳过；重复注册已存在的 <c>FormatName</c> 会被忽略。</para>
/// </summary>
public static class FirmwareFormatRegistry
{
    private static readonly Lock Sync = new();

    /// <summary>
    /// Registers all built-in format handlers. The call is thread-safe and idempotent; when
    /// <see cref="IsRegistered"/> is set it returns without touching the handler list, so the
    /// registration order below fixes each handler's detection priority.
    /// <para>注册全部内置格式处理器。调用线程安全且可重复执行；<see cref="IsRegistered"/> 已置位时
    /// 直接返回而不改动处理器列表，因此下述注册顺序即确定各处理器的检测优先级。</para>
    /// </summary>
    public static void RegisterAll()
    {
        lock (Sync)
        {
            if (IsRegistered)
            {
                return;
            }

            Register(new BoundedContexts.Archive.CpioFormatHandler());
            Register(new BoundedContexts.Compression.CompressFormatHandler());

            Register(new BoundedContexts.OtaDelta.Payload.PayloadFormatHandler());
            Register(new BoundedContexts.OtaDelta.Dat.DatFormatHandler());
            Register(new BoundedContexts.OtaDelta.Diff.DiffFormatHandler());

            Register(new BoundedContexts.BlockImage.Lp.LpFormatHandler());

            Register(new BoundedContexts.BlockImage.Sparse.SparseFormatHandler());
            Register(new BoundedContexts.FileSystem.Ext4.Ext4FormatHandler());
            Register(new BoundedContexts.FileSystem.Erofs.ErofsFormatHandler());
            Register(new BoundedContexts.FileSystem.F2fs.F2fsFormatHandler());

            Register(new BoundedContexts.BootSecurity.Avb.AvbFormatHandler());
            Register(new BoundedContexts.BootSecurity.BootImage.BootImageFormatHandler());

            Register(new BoundedContexts.AndroidMeta.Dex.DexFormatHandler());
            Register(new BoundedContexts.AndroidMeta.AXml.AXmlFormatHandler());
            Register(new BoundedContexts.AndroidMeta.Arsc.ArscFormatHandler());
            Register(new BoundedContexts.AndroidMeta.Apk.ApkFormatHandler());

            Register(new BoundedContexts.DeviceTree.Dt.DtFormatHandler());

            Register(new BoundedContexts.VendorFirmware.NTPi.NtpiFormatHandler());
            Register(new BoundedContexts.VendorFirmware.Nb0.Nb0FormatHandler());
            Register(new BoundedContexts.VendorFirmware.Oppo.OppoFormatHandler());
            Register(new BoundedContexts.VendorFirmware.OnePlusOps.OnePlusOpsFormatHandler());
            Register(new BoundedContexts.VendorFirmware.Pac.PacFormatHandler());
            Register(new BoundedContexts.VendorFirmware.Dz.DzFormatHandler());
            Register(new BoundedContexts.VendorFirmware.UpdateApp.UpdateAppFormatHandler());
            Register(new BoundedContexts.VendorFirmware.Rockchip.RockchipFormatHandler());
            Register(new BoundedContexts.VendorFirmware.Amlogic.AmlogicFormatHandler());
            Register(new BoundedContexts.VendorFirmware.SonySin.SonySinFormatHandler());

            IsRegistered = true;
        }
    }

    private static void Register(IReadonlyFormatHandler handler) => FormatDetector.Register(handler);

    /// <summary>
    /// Reports whether <see cref="RegisterAll"/> has finished publishing the built-in handlers.
    /// <para>指示 <see cref="RegisterAll"/> 是否已完成内置处理器的发布。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> once the built-in handlers are registered; while <see langword="false"/>,
    /// detection can only see handlers registered through other paths.
    /// <para>内置处理器注册完成后为 <see langword="true"/>；为 <see langword="false"/> 时，
    /// 检测只能看到经其他路径注册的处理器。</para>
    /// </value>
    public static bool IsRegistered { get; private set; }
}