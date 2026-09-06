namespace UotanStudio.Firmware.Common;

public static class FirmwareFormatRegistry
{
    private static readonly Lock Sync = new();

    /// <summary>
    /// Registers all built-in format handlers.
    /// <para>注册全部内置格式处理器。</para>
    /// </summary>
    public static void RegisterAll()
    {
        lock (Sync)
        {
            if (IsRegistered)
            {
                return;
            }

            Register(new Cpio.CpioFormatHandler());
            Register(new Payload.PayloadFormatHandler());
            Register(new Sparse.SparseFormatHandler());
            Register(new Avb.AvbFormatHandler());
            Register(new Lp.LpFormatHandler());
            Register(new Ext4.Ext4FormatHandler());
            Register(new Erofs.ErofsFormatHandler());
            Register(new F2fs.F2fsFormatHandler());
            Register(new NTPi.NtpiFormatHandler());
            Register(new Nb0.Nb0FormatHandler());


            Register(new BootImage.BootImageFormatHandler());
            Register(new Oppo.OppoFormatHandler());
            Register(new Compress.CompressFormatHandler());
            Register(new OnePlusOps.OnePlusOpsFormatHandler());
            Register(new Pac.PacFormatHandler());
            Register(new Dz.DzFormatHandler());
            Register(new UpdateApp.UpdateAppFormatHandler());


            Register(new Dat.DatFormatHandler());
            Register(new Diff.DiffFormatHandler());
            Register(new Dt.DtFormatHandler());
            Register(new Dex.DexFormatHandler());
            Register(new AXml.AXmlFormatHandler());
            Register(new Arsc.ArscFormatHandler());
            Register(new Apk.ApkFormatHandler());
            Register(new Rockchip.RockchipFormatHandler());

            IsRegistered = true;
        }
    }

    private static void Register(IFormatHandler handler) => FormatDetector.Register(handler);

    public static bool IsRegistered { get; private set; }
}
