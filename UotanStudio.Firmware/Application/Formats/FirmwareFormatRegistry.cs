using UotanStudio.Firmware.Core;
namespace UotanStudio.Firmware.Application.Formats;

/// <summary>
/// Single registration point publishing every built-in read-only format handler to <see cref="FormatDetector"/>. Detection probes extension-claiming handlers first and selects one only after its <c>CanHandle</c> check passes; otherwise every handler gets the same check in registration order.
/// <para>把全部内置只读格式处理器发布到 <see cref="FormatDetector"/> 的唯一注册入口。检测先探测声明该扩展名的处理器，且仅当其 <c>CanHandle</c> 校验通过才选中；否则按注册顺序对每个处理器执行同样校验。</para>
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

            Register(new Archive.CpioFormatHandler());
            Register(new CompressFormatHandler());

            Register(new OtaDelta.Payload.PayloadFormatHandler());
            Register(new OtaDelta.Dat.DatFormatHandler());
            Register(new OtaDelta.Diff.DiffFormatHandler());

            Register(new BlockImage.Lp.LpFormatHandler());

            Register(new BlockImage.Sparse.SparseFormatHandler());
            Register(new BlockImage.Gpt.GptFormatHandler());
            Core.Bridges.SparsePreservationAccess.Register(FileSystem.Rebuilding.FileSystemSparsePreservation.Instance);
            Register(new FileSystem.Ext4.Ext4FormatHandler());
            Register(new FileSystem.Erofs.ErofsFormatHandler());
            Register(new FileSystem.F2fs.F2fsFormatHandler());
            Register(new FileSystem.Romfs.RomfsFormatHandler());
            Register(new FileSystem.Squashfs.SquashfsFormatHandler());
            Register(new FileSystem.Fat.FatFormatHandler());
            Register(new FileSystem.Fat.ExFatFormatHandler());

            Register(new BootSecurity.Avb.AvbFormatHandler());
            Register(new BootSecurity.BootImage.BootImageFormatHandler());

            Register(new AndroidMeta.Dex.DexFormatHandler());
            Register(new AndroidMeta.AXml.AXmlFormatHandler());
            Register(new AndroidMeta.Arsc.ArscFormatHandler());
            Register(new AndroidMeta.Apk.ApkFormatHandler());

            Register(new DeviceTree.Dt.DtFormatHandler());

            Register(new VendorFirmware.NTPi.NtpiFormatHandler());
            Register(new VendorFirmware.Nb0.Nb0FormatHandler());
            Register(new VendorFirmware.Oppo.OppoFormatHandler());
            Register(new VendorFirmware.OnePlusOps.OnePlusOpsFormatHandler());
            Register(new VendorFirmware.Pac.PacFormatHandler());
            Register(new VendorFirmware.Dz.DzFormatHandler());
            Register(new VendorFirmware.HarmonyOS.HarmonyUpdateBinFormatHandler());
            Register(new VendorFirmware.UpdateApp.UpdateAppFormatHandler());
            Register(new VendorFirmware.Rockchip.RockchipFormatHandler());
            Register(new VendorFirmware.Rockchip.RockchipLoaderFormatHandler());
            Register(new VendorFirmware.Amlogic.AmlogicFormatHandler());
            Register(new VendorFirmware.SonySin.SonySinFormatHandler());
            Register(new VendorFirmware.Meizu.Mpk.MpkFormatHandler());
            Register(new VendorFirmware.Lenovo.Qsb.QsbFormatHandler());
            Register(new VendorFirmware.Coolpad.Cpb.CpbFormatHandler());
            Register(new VendorFirmware.Allwinner.ImageWty.AllwinnerImageWtyFormatHandler());
            Register(new VendorFirmware.MediaTek.MtkFormatHandler());
            Register(new VendorFirmware.Oppo.OppoSplashFormatHandler());

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