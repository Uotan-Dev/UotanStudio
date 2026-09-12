using System.Security.Cryptography;
using UotanStudio.Services.Keys;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class DebugBridgeIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bridge-identities-" + Guid.NewGuid().ToString("N"));
    public DebugBridgeIdentityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void StartupCreatesBothIdentitiesThenReusesThemAndRepairsMissingPublicFiles()
    {
        var adb = Path.Combine(_root, "adbkey"); var hdc = Path.Combine(_root, "hdckey");
        DebugBridgeIdentityService.EnsureDefaults(adb, hdc);
        var paths = new[] { adb, hdc, adb + ".pub", hdc + ".pub" };
        var before = paths.ToDictionary(p => p, File.ReadAllBytes);
        DebugBridgeIdentityService.EnsureDefaults(adb, hdc);
        foreach (var path in paths) Assert.Equal(before[path], File.ReadAllBytes(path));
        File.Delete(adb + ".pub"); File.Delete(hdc + ".pub");
        DebugBridgeIdentityService.EnsureDefaults(adb, hdc);
        foreach (var path in paths) Assert.Equal(before[path], File.ReadAllBytes(path));
        Assert.DoesNotContain('\0', File.ReadAllText(adb + ".pub"));
        using var key = RSA.Create(); key.ImportFromPem(File.ReadAllText(hdc)); Assert.Equal(3072, key.KeySize);
        key.ImportFromPem(File.ReadAllText(adb)); Assert.Equal(2048, key.KeySize);
    }

    [Fact]
    public void InvalidExistingIdentityIsPreservedAndDoesNotPreventOtherProtocolInitialization()
    {
        var adb = Path.Combine(_root, "adbkey"); var hdc = Path.Combine(_root, "hdckey");
        File.WriteAllText(adb, "invalid original key");
        Assert.Throws<AggregateException>(() => DebugBridgeIdentityService.EnsureDefaults(adb, hdc));
        Assert.Equal("invalid original key", File.ReadAllText(adb));
        Assert.True(File.Exists(hdc)); Assert.True(File.Exists(hdc + ".pub"));
    }
}
