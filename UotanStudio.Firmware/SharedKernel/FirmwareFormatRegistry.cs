namespace UotanStudio.Firmware.SharedKernel;

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

            // Archive
            Register(new BoundedContexts.Archive.Cpio.CpioFormatHandler());
            Register(new CrossCutting.Compress.CompressFormatHandler());

            // OtaDelta
            Register(new BoundedContexts.OtaDelta.Payload.PayloadFormatHandler());
            Register(new BoundedContexts.OtaDelta.Dat.DatFormatHandler());
            Register(new BoundedContexts.OtaDelta.Diff.DiffFormatHandler());

            // Identify logical partitions before their sparse storage wrapper.
            Register(new BoundedContexts.LogicalPartition.Lp.LpFormatHandler());

            // FileSystem
            Register(new BoundedContexts.FileSystem.Sparse.SparseFormatHandler());
            Register(new BoundedContexts.FileSystem.Ext4.Ext4FormatHandler());
            Register(new BoundedContexts.FileSystem.Erofs.ErofsFormatHandler());
            Register(new BoundedContexts.FileSystem.F2fs.F2fsFormatHandler());

            // BootSecurity
            Register(new BoundedContexts.BootSecurity.Avb.AvbFormatHandler());
            Register(new BoundedContexts.BootSecurity.BootImage.BootImageFormatHandler());


            // AndroidMeta
            Register(new BoundedContexts.AndroidMeta.Dex.DexFormatHandler());
            Register(new BoundedContexts.AndroidMeta.AXml.AXmlFormatHandler());
            Register(new BoundedContexts.AndroidMeta.Arsc.ArscFormatHandler());
            Register(new BoundedContexts.AndroidMeta.Apk.ApkFormatHandler());

            // DeviceTree
            Register(new BoundedContexts.DeviceTree.Dt.DtFormatHandler());

            // VendorFirmware
            Register(new BoundedContexts.VendorFirmware.NTPi.NtpiFormatHandler());
            Register(new BoundedContexts.VendorFirmware.Nb0.Nb0FormatHandler());
            Register(new BoundedContexts.VendorFirmware.Oppo.OppoFormatHandler());
            Register(new BoundedContexts.VendorFirmware.OnePlusOps.OnePlusOpsFormatHandler());
            Register(new BoundedContexts.VendorFirmware.Pac.PacFormatHandler());
            Register(new BoundedContexts.VendorFirmware.Dz.DzFormatHandler());
            Register(new BoundedContexts.VendorFirmware.UpdateApp.UpdateAppFormatHandler());
            Register(new BoundedContexts.VendorFirmware.Rockchip.RockchipFormatHandler());

            IsRegistered = true;
        }
    }

    private static void Register(IReadonlyFormatHandler handler) => FormatDetector.Register(handler);

    public static bool IsRegistered { get; private set; }
}