namespace UotanStudio.Firmware.Common;

/// <summary>
/// <para>注册 UotanStudio.Firmware 全部格式处理器到 FormatDetector。</para>
/// Registers all UotanStudio.Firmware format handlers with the FormatDetector.
/// </summary>
public static class FirmwareFormatRegistry
{
    private static readonly Lock Sync = new();

    /// <summary>
    /// <para>注册全部内置格式处理器。幂等,可多次调用。</para>
    /// Registers all built-in format handlers. Idempotent; may be called multiple times.
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
            // Boot/Vendor boot images are detected by their leading magic
            // ("ANDROID!" / "VNDRBOOT"), a strong head signal, so they must be tried
            // before the Oppo tail heuristics to avoid misrouting such images.
            Register(new BootImage.BootImageFormatHandler());
            Register(new Oppo.OppoFormatHandler());
            Register(new Compress.CompressFormatHandler());
            Register(new OnePlusOps.OnePlusOpsFormatHandler());
            Register(new Pac.PacFormatHandler());
            Register(new Dz.DzFormatHandler());
            Register(new UpdateApp.UpdateAppFormatHandler());
            // Android OTA 相关格式：DAT 镜像对（transfer.list + .new.dat）与 bsdiff 二进制差分补丁
            // 均为 OTA/更新生态产物，与 UpdateApp 归类在一起；两者的扩展名/魔数都与其他处理器无冲突。
            // Android OTA related formats: DAT image pairs (transfer.list + .new.dat) and bsdiff binary
            // delta patches belong to the OTA/update ecosystem, grouped here next to UpdateApp; neither's
            // extensions nor magic overlap with any other handler.
            Register(new Dat.DatFormatHandler());
            Register(new Diff.DiffFormatHandler());
            Register(new Dt.DtFormatHandler());
            Register(new Dex.DexFormatHandler());
            Register(new AXml.AXmlFormatHandler());
            Register(new Arsc.ArscFormatHandler());
            Register(new Apk.ApkFormatHandler());

            IsRegistered = true;
        }
    }

    private static void Register(IFormatHandler handler) => FormatDetector.Register(handler);

    /// <summary>
    /// <para>获取是否已注册。</para>
    /// Gets whether registration has completed.
    /// </summary>
    public static bool IsRegistered { get; private set; }
}