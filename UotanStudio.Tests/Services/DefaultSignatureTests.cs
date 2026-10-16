using System.Security.Cryptography;
using UotanStudio.Services.Keys;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Regression tests for the default-signature publishing and runtime identity import flow.
/// <para>默认签名发布与运行时身份导入流程的回归测试。</para>
/// </summary>
[Collection("DebugBridgeIdentitySettings")]
public sealed class DefaultSignatureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "default-signature-" + Guid.NewGuid().ToString("N"));
    private KeyLibraryService Library => new(Path.Combine(_root, "library"));

    public DefaultSignatureTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void ImportIdentity_IsIdempotent_ByFingerprintAndKind()
    {
        Directory.CreateDirectory(_root);
        using var rsa = RSA.Create(2048);
        var path = Path.Combine(_root, "runtime-adbkey");
        File.WriteAllText(path, rsa.ExportRSAPrivateKeyPem());

        var library = Library;
        var first = library.ImportIdentity("runtime", ManagedKeyKind.Adb, path);
        var second = library.ImportIdentity("runtime-again", ManagedKeyKind.Adb, path);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("runtime", Assert.Single(library.List()).Name);
        Assert.Equal("SHA256:" + Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())), first.Fingerprint);
        Assert.NotNull(library.FindByFingerprint(ManagedKeyKind.Adb, first.Fingerprint));
        using var hdcRsa = RSA.Create(UotanStudio.Comm.Protocols.Hdc.HdcAuthentication.KeyBits);
        var hdcPath = Path.Combine(_root, "runtime-hdckey");
        File.WriteAllText(hdcPath, hdcRsa.ExportRSAPrivateKeyPem());
        var hdc = library.ImportIdentity("hdc", ManagedKeyKind.Hdc, hdcPath);
        Assert.NotEqual(first.Id, hdc.Id);
        Assert.Equal(2, library.List().Count);
    }

    [Fact]
    public void PublishDefault_WritesRuntimeFilesVerifiablyAndRejectsMismatchedKinds()
    {
        Directory.CreateDirectory(_root);
        var library = Library;
        var adb = library.Generate("adb", ManagedKeyKind.Adb, 2048);
        var target = Path.Combine(_root, "runtime", "adbkey");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        library.PublishDefault(adb, target);

        Assert.Equal(File.ReadAllBytes(library.PrivatePath(adb)), File.ReadAllBytes(target));
        Assert.Equal(library.PublicText(adb), File.ReadAllText(target + ".pub"));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(target));
        Assert.Equal(2048, rsa.KeySize);
        using var auth = UotanStudio.Comm.Protocols.Adb.AdbAuthentication.LoadOrCreate(target);

        var payload = library.Generate("payload", ManagedKeyKind.Payload, 2048);
        Assert.Throws<InvalidOperationException>(() => library.PublishDefault(payload, target));
        Assert.Equal(File.ReadAllBytes(library.PrivatePath(adb)), File.ReadAllBytes(target));

        var hdc = library.Generate("hdc", ManagedKeyKind.Hdc, UotanStudio.Comm.Protocols.Hdc.HdcAuthentication.KeyBits);
        var hdcTarget = Path.Combine(_root, "runtime", "hdckey");
        library.PublishDefault(hdc, hdcTarget);
        using var hdcVerify = UotanStudio.Comm.Protocols.Hdc.HdcAuthentication.LoadPrivateKey(hdcTarget);
        Assert.False(string.IsNullOrEmpty(hdcVerify.PublicKeyToBase64()));
        Assert.Throws<InvalidOperationException>(() => library.PublishDefault(payload, hdcTarget));
        Assert.Equal(File.ReadAllBytes(library.PrivatePath(hdc)), File.ReadAllBytes(hdcTarget));
    }

    [Fact]
    public void PublishDefault_FailsWithoutTouchingExistingRuntimeFiles_WhenSourceIsBroken()
    {
        Directory.CreateDirectory(_root);
        var library = Library;
        var adb = library.Generate("adb", ManagedKeyKind.Adb, 2048);
        var target = Path.Combine(_root, "runtime", "adbkey");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        library.PublishDefault(adb, target);
        var before = File.ReadAllBytes(target);

        var publicText = library.PublicText(adb);
        File.Delete(library.PrivatePath(adb) + ".pub");
        Assert.Throws<InvalidDataException>(() => library.PublishDefault(adb, target));
        Assert.Equal(before, File.ReadAllBytes(target));

        File.WriteAllText(library.PrivatePath(adb) + ".pub", publicText);
        File.WriteAllText(library.PrivatePath(adb), "not a key");
        Assert.Throws<InvalidDataException>(() => library.PublishDefault(adb, target));
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Fact]
    public void EnsureDefaults_ImportsRuntimeIdentitiesIntoTheLibraryOnce()
    {
        var defaults = new Dictionary<string, string>();
        DebugBridgeIdentityService.LoadSettings = () => new UotanStudio.Services.AppSettings
        {
            AdbDefaultKeyId = defaults.TryGetValue("Adb", out var adbId) ? adbId : "",
            HdcDefaultKeyId = defaults.TryGetValue("Hdc", out var hdcId) ? hdcId : "",
        };
        DebugBridgeIdentityService.UpdateDefaultKeyId = (kind, id) => { defaults[kind] = id ?? ""; return true; };
        try
        {
            var adb = Path.Combine(_root, "adbkey");
            var hdc = Path.Combine(_root, "hdckey");
            var libraryRoot = Path.Combine(_root, "library");
            DebugBridgeIdentityService.EnsureDefaults(adb, hdc, libraryRoot);

            var library = new KeyLibraryService(libraryRoot);
            Assert.Equal(2, library.List().Count);
            var adbImport = library.List().Single(k => k.Kind == ManagedKeyKind.Adb);
            Assert.Equal(File.ReadAllBytes(adb), File.ReadAllBytes(library.PrivatePath(adbImport)));
            Assert.Equal(adbImport.Id, defaults["Adb"]);

            var before = new[] { adb, hdc, adb + ".pub", hdc + ".pub" }.ToDictionary(p => p, File.ReadAllBytes);
            DebugBridgeIdentityService.EnsureDefaults(adb, hdc, libraryRoot);
            Assert.Equal(2, new KeyLibraryService(libraryRoot).List().Count);
            foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            DebugBridgeIdentityService.LoadSettings = UotanStudio.Services.SettingsPersistenceService.Instance.LoadSettings;
            DebugBridgeIdentityService.UpdateDefaultKeyId = UotanStudio.Services.SettingsPersistenceService.Instance.UpdateDefaultKeyId;
        }
    }

    [Fact]
    public void DefaultKeyIdSettings_RoundTrip()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new UotanStudio.Services.AppSettings
        {
            AdbDefaultKeyId = "abc",
            HdcDefaultKeyId = "def",
        }, UotanStudio.Services.AppSettingsJsonContext.Default.AppSettings);
        var settings = System.Text.Json.JsonSerializer.Deserialize(json, UotanStudio.Services.AppSettingsJsonContext.Default.AppSettings)!;
        Assert.Equal("abc", settings.AdbDefaultKeyId);
        Assert.Equal("def", settings.HdcDefaultKeyId);

        var empty = System.Text.Json.JsonSerializer.Deserialize("{}", UotanStudio.Services.AppSettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(string.Empty, empty.AdbDefaultKeyId);
        Assert.Equal(string.Empty, empty.HdcDefaultKeyId);
    }

    [Fact]
    public async Task KeyManagerViewModel_DeletingCurrentDefault_ClearsBindingBeforeDeletion()
    {
        Directory.CreateDirectory(_root);
        var library = Library;
        var adb = library.Generate("adb", ManagedKeyKind.Adb, 2048);
        var other = library.Generate("other", ManagedKeyKind.Adb, 2048);
        var defaults = new Dictionary<string, string>();
        var model = new ViewModels.Shell.KeyManagerViewModel(library,
            () => new UotanStudio.Services.AppSettings
            {
                AdbDefaultKeyId = defaults.TryGetValue("Adb", out var adbId) ? adbId : "",
                HdcDefaultKeyId = defaults.TryGetValue("Hdc", out var hdcId) ? hdcId : "",
            },
            (kind, id) => { defaults[kind] = id ?? ""; return true; });
        await model.RefreshCommand.ExecuteAsync(null);

        model.SelectedKey = other;
        Assert.False(model.IsSelectedDefault);
        Assert.True(model.CanSetCurrentDefault);
        model.SelectedKey = adb;
        Assert.False(model.IsSelectedDefault);

        defaults["Adb"] = adb.Id;
        await model.RefreshCommand.ExecuteAsync(null);
        model.SelectedKey = adb;
        Assert.True(model.IsSelectedDefault);

        model.ConfirmDeletion = true;
        await model.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(other.Id, Assert.Single(library.List()).Id);
        Assert.Equal(string.Empty, defaults["Adb"]);
    }

    [Fact]
    public async Task KeyManagerViewModel_EmptySavedId_FallsBackToRuntimeIdentityFingerprint()
    {
        Directory.CreateDirectory(_root);
        var library = Library;
        var adb = library.Generate("adb", ManagedKeyKind.Adb, 2048);
        var hdc = library.Generate("hdc", ManagedKeyKind.Hdc, UotanStudio.Comm.Protocols.Hdc.HdcAuthentication.KeyBits);
        var other = library.Generate("other", ManagedKeyKind.Adb, 2048);
        var defaults = new Dictionary<string, string>();
        var model = new ViewModels.Shell.KeyManagerViewModel(library,
            () => new UotanStudio.Services.AppSettings
            {
                AdbDefaultKeyId = defaults.TryGetValue("Adb", out var adbId) ? adbId : "",
                HdcDefaultKeyId = defaults.TryGetValue("Hdc", out var hdcId) ? hdcId : "",
            },
            (kind, id) => { defaults[kind] = id ?? ""; return true; },
            kind => kind switch
            {
                ManagedKeyKind.Adb => adb.Fingerprint,
                ManagedKeyKind.Hdc => hdc.Fingerprint,
                _ => null,
            });
        await model.RefreshCommand.ExecuteAsync(null);

        model.SelectedKey = adb;
        Assert.True(model.IsSelectedDefault);
        Assert.True(model.CanSetCurrentDefault);
        model.SelectedKey = hdc;
        Assert.True(model.IsSelectedDefault);
        model.SelectedKey = other;
        Assert.False(model.IsSelectedDefault);

        defaults["Adb"] = other.Id;
        await model.RefreshCommand.ExecuteAsync(null);
        model.SelectedKey = adb;
        Assert.False(model.IsSelectedDefault);
        model.SelectedKey = other;
        Assert.True(model.IsSelectedDefault);

        defaults["Adb"] = "";
        var fallbackModel = new ViewModels.Shell.KeyManagerViewModel(library,
            () => new UotanStudio.Services.AppSettings
            {
                AdbDefaultKeyId = defaults.TryGetValue("Adb", out var adbId2) ? adbId2 : "",
                HdcDefaultKeyId = "",
            },
            (kind, id) => { defaults[kind] = id ?? ""; return true; },
            _ => null);
        await fallbackModel.RefreshCommand.ExecuteAsync(null);
        fallbackModel.SelectedKey = adb;
        Assert.False(fallbackModel.IsSelectedDefault);
    }

    [Fact]
    public async Task KeyManagerViewModel_StaleSavedId_FallsBackToRuntimeIdentityFingerprint()
    {
        Directory.CreateDirectory(_root);
        var library = Library;
        var adb = library.Generate("adb", ManagedKeyKind.Adb, 2048);
        var hdc = library.Generate("hdc", ManagedKeyKind.Hdc, UotanStudio.Comm.Protocols.Hdc.HdcAuthentication.KeyBits);

        var model = new ViewModels.Shell.KeyManagerViewModel(library,
            () => new UotanStudio.Services.AppSettings
            {
                AdbDefaultKeyId = "dead-adb-id",
                HdcDefaultKeyId = "dead-hdc-id",
            },
            (_, _) => true,
            kind => kind switch
            {
                ManagedKeyKind.Adb => adb.Fingerprint,
                ManagedKeyKind.Hdc => hdc.Fingerprint,
                _ => null,
            });
        await model.RefreshCommand.ExecuteAsync(null);

        model.SelectedKey = adb;
        Assert.True(model.IsSelectedDefault);
        model.SelectedKey = hdc;
        Assert.True(model.IsSelectedDefault);

        var explicitModel = new ViewModels.Shell.KeyManagerViewModel(library,
            () => new UotanStudio.Services.AppSettings { AdbDefaultKeyId = adb.Id, HdcDefaultKeyId = "" },
            (_, _) => true,
            _ => null);
        await explicitModel.RefreshCommand.ExecuteAsync(null);
        explicitModel.SelectedKey = adb;
        Assert.True(explicitModel.IsSelectedDefault);
        explicitModel.SelectedKey = hdc;
        Assert.False(explicitModel.IsSelectedDefault);
    }
}