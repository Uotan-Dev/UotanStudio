namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

public sealed class NtpiUnpackOptions
{
    public const long DefaultMaxFileOutputBytes = 32L * 1024 * 1024 * 1024;

    public const long DefaultMaxTotalOutputBytes = 128L * 1024 * 1024 * 1024;

    public long MaxFileOutputBytes { get; init; } = DefaultMaxFileOutputBytes;

    public long MaxTotalOutputBytes { get; init; } = DefaultMaxTotalOutputBytes;

    public bool DisableOutputLimits { get; init; }

    public static NtpiUnpackOptions Unlimited { get; } = new() { DisableOutputLimits = true };
}
