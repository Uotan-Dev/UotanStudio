using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;

namespace UotanStudio.Services.Keys;

/// <summary>Prepares the persistent identities used by native sessions before desktop device discovery.</summary>
internal static class DebugBridgeIdentityService
{
    // Settings access seams; tests inject isolated delegates to avoid touching real user settings.
    // 设置访问接缝；测试注入隔离委托以避免触碰真实用户设置。
    internal static Func<AppSettings> LoadSettings = () => UotanStudio.Services.SettingsPersistenceService.Instance.LoadSettings();
    internal static Func<string, string?, bool> UpdateDefaultKeyId = UotanStudio.Services.SettingsPersistenceService.Instance.UpdateDefaultKeyId;

    internal static void EnsureDefaults(string? adbPath = null, string? hdcPath = null, string? libraryRoot = null)
    {
        // Try both protocols even if one identity is unreadable. Never replace an invalid existing key.
        List<Exception> failures = [];
        try
        {
            // Keep the loaded identity alive while it is imported into the key library.
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

    // Best-effort import of a runtime identity into the key library; failures never affect startup.
    // An empty persisted default key id is backfilled with the imported entry so the auto-generated
    // identity is visible as the current default; an explicit user choice is never overwritten.
    // 尽力把运行时身份导入密钥库，失败不影响启动；默认签名为空时回填导入条目，
    // 使自动生成的身份显示为当前默认，已保存的用户选择不被覆盖。
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

    // Records the auto-imported identity as the default only when no default was saved before.
    // 仅在尚未保存默认签名时，把自动导入的身份记录为默认。
    private static void BackfillDefaultKeyId(ManagedKeyKind kind, string keyId)
    {
        var settingsKind = kind == ManagedKeyKind.Adb ? "Adb" : "Hdc";
        var settings = LoadSettings();
        var existing = kind == ManagedKeyKind.Adb ? settings.AdbDefaultKeyId : settings.HdcDefaultKeyId;
        if (!string.IsNullOrEmpty(existing)) return;
        UpdateDefaultKeyId(settingsKind, keyId);
    }
}
