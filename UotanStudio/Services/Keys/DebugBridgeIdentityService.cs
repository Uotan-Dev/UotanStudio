using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;

namespace UotanStudio.Services.Keys;

/// <summary>Prepares the persistent debug-bridge identities a native session needs before device discovery.
/// <para>在设备发现之前，为原生会话准备所需的持久化调试桥身份。</para></summary>
internal static class DebugBridgeIdentityService
{
    internal static Func<AppSettings> LoadSettings = () => UotanStudio.Services.SettingsPersistenceService.Instance.LoadSettings();
    internal static Func<string, string?, bool> UpdateDefaultKeyId = UotanStudio.Services.SettingsPersistenceService.Instance.UpdateDefaultKeyId;

    internal static void EnsureDefaults(string? adbPath = null, string? hdcPath = null, string? libraryRoot = null)
    {
        List<Exception> failures = [];
        try
        {
            using var adb = AdbAuthentication.LoadOrCreate(adbPath);
            ImportDefault("Keys_DefaultAutoName", "Default ADB key", ManagedKeyKind.Adb, adbPath, libraryRoot);
        }
        catch (Exception ex) { failures.Add(new InvalidOperationException("ADB identity initialization failed.", ex)); }
        try
        {
            using var hdc = HdcAuthentication.LoadOrCreate(hdcPath);
            ImportDefault("Keys_DefaultAutoNameHdc", "Default HDC key", ManagedKeyKind.Hdc, hdcPath, libraryRoot);
        }
        catch (Exception ex) { failures.Add(new InvalidOperationException("HDC identity initialization failed.", ex)); }
        if (failures.Count > 0) throw new AggregateException("Debug bridge identity initialization failed.", failures);
    }

    private static void ImportDefault(string resourceKey, string fallbackName, ManagedKeyKind kind, string? path, string? libraryRoot)
    {
        try
        {
            var name = UotanStudio.Services.LanguageService.Instance.GetString(resourceKey);
            if (string.IsNullOrEmpty(name) || name == resourceKey) name = fallbackName;
            var library = new KeyLibraryService(libraryRoot);
            var imported = library.ImportIdentity(name, kind, path ??
                (kind == ManagedKeyKind.Adb ? AdbAuthentication.DefaultKeyFilePath : HdcAuthentication.DefaultKeyFilePath));
            BackfillDefaultKeyId(kind, imported.Id);
        }
        catch (Exception ex)
        {
            UotanStudio.Services.LogService.Instance.Write(UotanStudio.Services.LogLevel.Warn, "Keys", ex.Message);
        }
    }

    private static void BackfillDefaultKeyId(ManagedKeyKind kind, string keyId)
    {
        var settingsKind = kind == ManagedKeyKind.Adb ? "Adb" : "Hdc";
        var settings = LoadSettings();
        var existing = kind == ManagedKeyKind.Adb ? settings.AdbDefaultKeyId : settings.HdcDefaultKeyId;
        if (!string.IsNullOrEmpty(existing)) return;
        UpdateDefaultKeyId(settingsKind, keyId);
    }
}