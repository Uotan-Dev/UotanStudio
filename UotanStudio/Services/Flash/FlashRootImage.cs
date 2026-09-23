using UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher.Models;

namespace UotanStudio.Services.Flash;

/// <summary>Prepares a supported Root-patched boot image without modifying the source.</summary>
public static class FlashRootImage
{
    /// <summary>Only patchers usable without additional secrets are offered on the flash page.</summary>
    public static bool IsSupportedPackage(BootPatchDetection detection)
        => detection.IsSupported &&
            (detection.Kind is BootPatchKind.Magisk or BootPatchKind.KernelSu ||
                detection.Kind == BootPatchKind.KernelSuLkm &&
                detection.Architectures.Any(arch => arch is "arm64" or "arm32" or "x64" or "x86"));

    /// <summary>Returns a temporary image owned by the caller after native package validation.</summary>
    public static async Task<string> PrepareAsync(string packagePath, string bootPath, CancellationToken ct)
    {
        var output = Path.Combine(Path.GetTempPath(), "uotan-root-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            var patcher = new BootPatchService();
            var detection = await patcher.DetectAsync(packagePath, ct).ConfigureAwait(false);
            if (!IsSupportedPackage(detection))
            {
                throw new InvalidDataException("The selected package is not a supported Magisk or KernelSU package.");
            }

            await patcher.PatchAsync(packagePath, bootPath, output, cancellationToken: ct).ConfigureAwait(false);
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
            {
                throw new InvalidDataException("The Root patch did not produce a boot image.");
            }
            ct.ThrowIfCancellationRequested();
            return output;
        }
        catch
        {
            if (File.Exists(output)) File.Delete(output);
            throw;
        }
    }
}