using System.Security.Cryptography;
using UotanStudio.Services.Keys;
using Xunit;

namespace UotanStudio.Tests.Services;

[Collection("DebugBridgeIdentitySettings")]
public sealed class DebugBridgeIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bridge-identities-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> _defaults = new();
    public DebugBridgeIdentityTests() => Directory.CreateDirectory(_root);

    private void UseIsolatedSettings()
    {
        DebugBridgeIdentityService.LoadSettings = () => new UotanStudio.Services.AppSettings
        {
            AdbDefaultKeyId = _defaults.TryGetValue("Adb", out var adb) ? adb : "",
            HdcDefaultKeyId = _defaults.TryGetValue("Hdc", out var hdc) ? hdc : "",
        };
        DebugBridgeIdentityService.UpdateDefaultKeyId = (kind, id) => { _defaults[kind] = id ?? ""; return true; };
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
        DebugBridgeIdentityService.LoadSettings = () => UotanStudio.Services.SettingsPersistenceService.Instance.LoadSettings();
        DebugBridgeIdentityService.UpdateDefaultKeyId = UotanStudio.Services.SettingsPersistenceService.Instance.UpdateDefaultKeyId;
    }

    [Fact]
    public void StartupCreatesBothIdentitiesThenReusesThemAndRepairsMissingPublicFiles()
    {
        UseIsolatedSettings();
        var adb = Path.Combine(_root, "adbkey"); var hdc = Path.Combine(_root, "hdckey");
        var libraryRoot = Path.Combine(_root, "library");
        DebugBridgeIdentityService.EnsureDefaults(adb, hdc, libraryRoot);
        var paths = new[] { adb, hdc, adb + ".pub", hdc + ".pub" };
        var before = paths.ToDictionary(p => p, File.ReadAllBytes);
        DebugBridgeIdentityService.EnsureDefaults(adb, hdc, libraryRoot);
        foreach (var path in paths) Assert.Equal(before[path], File.ReadAllBytes(path));
        File.Delete(adb + ".pub"); File.Delete(hdc + ".pub");
        DebugBridgeIdentityService.EnsureDefaults(adb, hdc, libraryRoot);
        foreach (var path in paths) Assert.Equal(before[path], File.ReadAllBytes(path));
        Assert.DoesNotContain('\0', File.ReadAllText(adb + ".pub"));
        using var key = RSA.Create(); key.ImportFromPem(File.ReadAllText(hdc)); Assert.Equal(3072, key.KeySize);
        key.ImportFromPem(File.ReadAllText(adb)); Assert.Equal(2048, key.KeySize);
    }

    [Fact]
    public void InvalidExistingIdentityIsPreservedAndDoesNotPreventOtherProtocolInitialization()
    {
        UseIsolatedSettings();
        var adb = Path.Combine(_root, "adbkey"); var hdc = Path.Combine(_root, "hdckey");
        File.WriteAllText(adb, "invalid original key");
        Assert.Throws<AggregateException>(() => DebugBridgeIdentityService.EnsureDefaults(adb, hdc, Path.Combine(_root, "library")));
        Assert.Equal("invalid original key", File.ReadAllText(adb));
        Assert.True(File.Exists(hdc)); Assert.True(File.Exists(hdc + ".pub"));
    }

    [Fact]
    public void StartupImport_BackfillsEmptyDefaultIds_AndPreservesExplicitChoices()
    {
        UseIsolatedSettings();
        var adb = Path.Combine(_root, "adbkey"); var hdc = Path.Combine(_root, "hdckey");
        var libraryRoot = Path.Combine(_root, "library");

        DebugBridgeIdentityService.EnsureDefaults(adb, hdc, libraryRoot);
        var library = new KeyLibraryService(libraryRoot);
        Assert.Equal(2, library.List().Count);
        Assert.Equal(library.List().Single(k => k.Kind == ManagedKeyKind.Adb).Id, _defaults["Adb"]);
        Assert.Equal(library.List().Single(k => k.Kind == ManagedKeyKind.Hdc).Id, _defaults["Hdc"]);

        _defaults["Adb"] = "user-choice";
        DebugBridgeIdentityService.EnsureDefaults(adb, hdc, libraryRoot);
        Assert.Equal("user-choice", _defaults["Adb"]);
    }
}

[CollectionDefinition("DebugBridgeIdentitySettings", DisableParallelization = true)]
public sealed class DebugBridgeIdentitySettingsCollection;