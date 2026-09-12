using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;

namespace UotanStudio.Services.Keys;

/// <summary>Prepares the persistent identities used by native sessions before desktop device discovery.</summary>
internal static class DebugBridgeIdentityService
{
    internal static void EnsureDefaults(string? adbPath = null, string? hdcPath = null)
    {
        // Try both protocols even if one identity is unreadable. Never replace an invalid existing key.
        List<Exception> failures = [];
        try { using var adb = AdbAuthentication.LoadOrCreate(adbPath); }
        catch (Exception ex) { failures.Add(new InvalidOperationException("ADB identity initialization failed.", ex)); }
        try { using var hdc = HdcAuthentication.LoadOrCreate(hdcPath); }
        catch (Exception ex) { failures.Add(new InvalidOperationException("HDC identity initialization failed.", ex)); }
        if (failures.Count > 0) throw new AggregateException("Debug bridge identity initialization failed.", failures);
    }
}
