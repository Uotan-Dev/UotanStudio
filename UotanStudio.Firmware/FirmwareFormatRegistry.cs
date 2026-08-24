using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware;

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
            Register(new NTPi.NtpiFormatHandler());
            Register(new Nb0.Nb0FormatHandler());
            Register(new Oppo.OppoFormatHandler());
            Register(new BootImage.BootImageFormatHandler());
            Register(new Compress.CompressFormatHandler());
            Register(new OnePlusOps.OnePlusOpsFormatHandler());
            Register(new Pac.PacFormatHandler());
            Register(new Dz.DzFormatHandler());
            Register(new UpdateApp.UpdateAppFormatHandler());
            Register(new Dt.DtFormatHandler());

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