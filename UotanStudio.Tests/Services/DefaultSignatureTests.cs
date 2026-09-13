using System.Security.Cryptography;
using UotanStudio.Services.Keys;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Regression tests for the default-signature publishing and runtime identity import flow.</summary>
/// <summary>默认签名发布与运行时身份导入流程的回归测试。</summary>
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
        // Same fingerprint under a different purpose is a distinct entry (valid 3072-bit HDC key).
        // 同指纹不同用途是独立条目（合法的 3072 位 HDC 密钥）。
        using var hdcRsa = RSA.Create(3072);
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

        // A wrong-purpose key must never reach the runtime path.
        // 用途不符的密钥不得写入运行时路径。
        var payload = library.Generate("payload", ManagedKeyKind.Payload, 2048);
        Assert.Throws<InvalidOperationException>(() => library.PublishDefault(payload, target));
        Assert.Equal(File.ReadAllBytes(library.PrivatePath(adb)), File.ReadAllBytes(target));

        // A valid HDC key publishes to its own runtime path; Payload never reaches any runtime path.
        // 合法的 HDC 密钥发布到自己的运行时路径；Payload 永远不得写入运行时路径。
        var hdc = library.Generate("hdc", ManagedKeyKind.Hdc, 3072);
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

        // Missing public key must abort without replacing the runtime identity.
        // 缺少公钥必须中止且不替换运行时身份。
        var publicText = library.PublicText(adb);
        File.Delete(library.PrivatePath(adb) + ".pub");
        Assert.Throws<InvalidDataException>(() => library.PublishDefault(adb, target));
        Assert.Equal(before, File.ReadAllBytes(target));

        // Corrupted private key must abort the same way (public key restored first,
        // otherwise the missing-public-key check fires before the key is loaded).
        // 私钥损坏时同样中止（先恢复公钥文件，否则先命中缺少公钥的检查）。
        File.WriteAllText(library.PrivatePath(adb) + ".pub", publicText);
        File.WriteAllText(library.PrivatePath(adb), "not a key");
        Assert.ThrowsAny<CryptographicException>(() => library.PublishDefault(adb, target));
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Fact]
    public void EnsureDefaults_ImportsRuntimeIdentitiesIntoTheLibraryOnce()
    {
        // Isolate the settings store: the import backfill must never touch real user settings.
        // 隔离设置存储：导入回填绝不触碰真实用户设置。
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

            // Second startup must not duplicate entries; identities are unchanged.
            // 第二次启动不得重复入库；运行时身份保持不变。
            var before = new[] { adb, hdc, adb + ".pub", hdc + ".pub" }.ToDictionary(p => p, File.ReadAllBytes);
            DebugBridgeIdentityService.EnsureDefaults(adb, hdc, libraryRoot);
            Assert.Equal(2, new KeyLibraryService(libraryRoot).List().Count);
            foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            DebugBridgeIdentityService.LoadSettings = () => UotanStudio.Services.SettingsPersistenceService.Instance.LoadSettings();
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
        var model = new UotanStudio.ViewModels.Shell.KeyManagerViewModel(library,
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

        // Point the persisted binding at the first key, refresh, and verify the badge state.
        // 把持久化绑定指向第一个密钥并刷新，验证徽标状态。
        defaults["Adb"] = adb.Id;
        await model.RefreshCommand.ExecuteAsync(null);
        model.SelectedKey = adb;
        Assert.True(model.IsSelectedDefault);

        // Deleting the default key clears the binding first, keeping the other key.
        // 删除默认密钥前先清除绑定，保留另一密钥。
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
        var hdc = library.Generate("hdc", ManagedKeyKind.Hdc, 3072);
        var other = library.Generate("other", ManagedKeyKind.Adb, 2048);
        var defaults = new Dictionary<string, string>();
        var model = new UotanStudio.ViewModels.Shell.KeyManagerViewModel(library,
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

        // Empty persisted ids resolve to the runtime identity entries matched by fingerprint.
        // 持久化 Id 为空时回退到按指纹匹配的运行时身份条目。
        model.SelectedKey = adb;
        Assert.True(model.IsSelectedDefault);
        Assert.True(model.CanSetCurrentDefault);
        model.SelectedKey = hdc;
        Assert.True(model.IsSelectedDefault);
        model.SelectedKey = other;
        Assert.False(model.IsSelectedDefault);

        // An explicit saved choice still wins over the runtime fingerprint.
        // 显式保存的选择优先于运行时指纹。
        defaults["Adb"] = other.Id;
        await model.RefreshCommand.ExecuteAsync(null);
        model.SelectedKey = adb;
        Assert.False(model.IsSelectedDefault);
        model.SelectedKey = other;
        Assert.True(model.IsSelectedDefault);

        // A missing runtime identity falls back to no default at all.
        // 运行时身份缺失时不判定任何默认。
        defaults["Adb"] = "";
        var fallbackModel = new UotanStudio.ViewModels.Shell.KeyManagerViewModel(library,
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
        var hdc = library.Generate("hdc", ManagedKeyKind.Hdc, 3072);

        // The persisted ids point at entries that no longer exist in the library.
        // 持久化 Id 指向库中已不存在的条目。
        var model = new UotanStudio.ViewModels.Shell.KeyManagerViewModel(library,
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

        // A stale id that still resolves must keep the explicit choice (no runtime key for ADB here).
        // 仍能解析的持久化 Id 保留显式选择（此处 ADB 无运行时密钥）。
        var explicitModel = new UotanStudio.ViewModels.Shell.KeyManagerViewModel(library,
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
