using System.Security.Cryptography;
using System.Text.Json.Nodes;
using UotanStudio.Mirroring;
using UotanStudio.Projects;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Keys;
using UotanStudio.Services.Scripting;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Nodes;
using UotanStudio.Workflow.Registry;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class PluginSecurityTests
{
    [Fact]
    public void PluginSignature_RsaAndEcdsa_VerifyRoundTripAndRejectTampering()
    {
        using var rsa = RSA.Create(3072);
        var package = CreatePackage();
        PluginSignature.Sign(package, rsa, "tests.publisher", "Test publisher");
        Assert.True(package.IsSigned);
        Assert.Equal(PluginSignatureAlgorithms.RsaPssSha256, package.Manifest.Signature!.Algorithm);
        var verified = PluginSignature.Verify(package);
        Assert.True(verified.IsSigned);
        Assert.True(verified.IsValid);

        package.Manifest.Version = "9.9.9";
        var tampered = PluginSignature.Verify(package);
        Assert.True(tampered.IsSigned);
        Assert.False(tampered.IsValid);

        package.Manifest.Version = "1.0.0";
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        PluginSignature.Sign(package, ec, "tests.publisher", "Test publisher");
        Assert.Equal(PluginSignatureAlgorithms.EcdsaP256Sha256, package.Manifest.Signature!.Algorithm);
        Assert.True(PluginSignature.Verify(package).IsValid);
    }

    [Fact]
    public async Task VisualPluginFile_TamperedSignedPackage_IsRejectedOnLoad()
    {
        using var temp = new TempDirectory();
        using var rsa = RSA.Create(3072);
        var package = CreatePackage();
        PluginSignature.Sign(package, rsa, "tests.publisher", "Test publisher");
        var path = Path.Combine(temp.Path, "signed.uotanplugin.json");
        await VisualPluginFile.SaveAsync(path, package, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(VisualPluginFile.DetachedSignaturePath(path)));

        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
        json["Manifest"]!["Version"] = "2.0.0-tampered";
        await File.WriteAllTextAsync(path, json.ToJsonString(), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => VisualPluginFile.LoadAsync(path, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => VisualPluginFile.Load(path));
    }

    [Fact]
    public async Task VisualPluginFile_WritesFormatFour_AndReadsVersionThreeAsUnsigned()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "current.uotanplugin.json");
        await VisualPluginFile.SaveAsync(path, CreatePackage(), TestContext.Current.CancellationToken);
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("\"FormatVersion\": 4", text, StringComparison.Ordinal);
        Assert.False((await VisualPluginFile.LoadAsync(path, TestContext.Current.CancellationToken)).IsSigned);

        var legacy = Path.Combine(temp.Path, "legacy.uotanplugin.json");
        await File.WriteAllTextAsync(legacy,
            """{"FormatVersion":3,"Kind":"WorkflowTemplate","Manifest":{"Id":"tests.legacy","Name":"Legacy"},"Graph":{"FormatVersion":1,"Nodes":[],"Connections":[]}}""",
            TestContext.Current.CancellationToken);
        var loaded = await VisualPluginFile.LoadAsync(legacy, TestContext.Current.CancellationToken);

        Assert.Equal(3, loaded.FormatVersion);
        Assert.False(loaded.IsSigned);
        Assert.Empty(VisualPluginValidation.Validate(loaded));
    }

    [Fact]
    public void PublisherTrustStore_FirstUsePinsFingerprint_AndRotationRequiresReconfirmation()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "publishers", "trust.json");
        var store = new PublisherTrustStore(path);
        using var first = RSA.Create(3072);
        var firstPackage = CreatePackage();
        PluginSignature.Sign(firstPackage, first, "tests.publisher", "First publisher");
        Assert.Equal(PublisherTrustStatus.UnknownPublisher, store.GetStatus(firstPackage));
        Assert.Empty(store.List());

        store.Trust(firstPackage.Manifest);
        Assert.Equal(PublisherTrustStatus.Trusted, store.GetStatus(firstPackage));
        var persisted = new PublisherTrustStore(path);
        Assert.Equal(PublisherTrustStatus.Trusted, persisted.GetStatus(firstPackage));
        Assert.Equal(firstPackage.Manifest.Publisher!.Fingerprint, Assert.Single(persisted.List()).Fingerprint);

        using var rotated = RSA.Create(3072);
        var rotatedPackage = CreatePackage();
        PluginSignature.Sign(rotatedPackage, rotated, "tests.publisher", "Rotated publisher");
        Assert.Equal(PublisherTrustStatus.FingerprintMismatch, persisted.GetStatus(rotatedPackage));

        persisted.Trust(rotatedPackage.Manifest);
        Assert.Equal(PublisherTrustStatus.Trusted, persisted.GetStatus(rotatedPackage));
        Assert.True(persisted.Untrust("tests.publisher"));
        Assert.Equal(PublisherTrustStatus.UnknownPublisher, persisted.GetStatus(rotatedPackage));
        Assert.False(persisted.Untrust("tests.publisher"));
    }

    [Fact]
    public async Task Manager_SignedPackage_RequiresExplicitTrustAndPinsPublisher()
    {
        using var temp = new TempDirectory();
        NodeFactoryRegistrar.RegisterAll();
        var trust = new PublisherTrustStore(Path.Combine(temp.Path, "trust.json"));
        var manager = new VisualPluginManager(Path.Combine(temp.Path, "installed"), trustStore: trust);
        using var rsa = RSA.Create(3072);
        var package = CreatePackage("tests.trusted-install");
        PluginSignature.Sign(package, rsa, "tests.publisher", "Trusted publisher");
        var source = Path.Combine(temp.Path, "signed.uotanplugin.json");
        await VisualPluginFile.SaveAsync(source, package, TestContext.Current.CancellationToken);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.InstallAsync(source, TestContext.Current.CancellationToken));

            var registration = await manager.InstallAsync(source, TrustingConsent(package),
                TestContext.Current.CancellationToken);

            Assert.Equal(PublisherTrustStatus.Trusted, registration.TrustStatus);
            Assert.True(registration.IsAvailable);
            Assert.Equal(package.Manifest.Publisher!.Fingerprint, trust.Find("tests.publisher")!.Fingerprint);

            using var rotated = RSA.Create(3072);
            var replacement = CreatePackage("tests.trusted-install");
            PluginSignature.Sign(replacement, rotated, "tests.publisher", "Trusted publisher");
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.UpdateAsync(registration, replacement, TestContext.Current.CancellationToken));

            var updated = await manager.UpdateAsync(registration, replacement, TrustingConsent(replacement),
                TestContext.Current.CancellationToken);
            Assert.Equal(PublisherTrustStatus.Trusted, updated.TrustStatus);
            Assert.Equal(replacement.Manifest.Publisher!.Fingerprint, trust.Find("tests.publisher")!.Fingerprint);
        }
        finally
        {
            foreach (var registration in manager.Registrations.ToArray())
            {
                await manager.RemoveAsync(registration, TestContext.Current.CancellationToken);
            }
        }
    }

    [Fact]
    public async Task Manager_UnsignedPackage_RequiresLocalConsent_AndGatesDeclaredPermissions()
    {
        using var temp = new TempDirectory();
        NodeFactoryRegistrar.RegisterAll();
        var trust = new PublisherTrustStore(Path.Combine(temp.Path, "trust.json"));
        var manager = new VisualPluginManager(Path.Combine(temp.Path, "installed"), trustStore: trust);
        var package = CreatePackage("tests.unsigned-flash", "Device.Partition.Flash");
        var source = Path.Combine(temp.Path, "unsigned.uotanplugin.json");
        await VisualPluginFile.SaveAsync(source, package, TestContext.Current.CancellationToken);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.InstallAsync(source, new VisualPluginInstallConsent(), TestContext.Current.CancellationToken));

            var registration = await manager.InstallAsync(source, TestContext.Current.CancellationToken);

            Assert.Equal(PublisherTrustStatus.Unsigned, registration.TrustStatus);
            Assert.Equal(PluginPermission.DeviceFlash, registration.GrantedPermissions);
            Assert.True(registration.IsAvailable);
            Assert.True(NodeTypeRegistry.Contains(package.NodeTypeKey));

            await manager.RemoveAsync(registration, TestContext.Current.CancellationToken);

            var denied = await manager.InstallAsync(source, new VisualPluginInstallConsent { AllowUntrusted = true },
                TestContext.Current.CancellationToken);

            Assert.Equal(PluginPermission.None, denied.GrantedPermissions);
            Assert.False(denied.IsAvailable);
            Assert.NotEmpty(denied.Errors);
            Assert.False(NodeTypeRegistry.Contains(package.NodeTypeKey));
        }
        finally
        {
            foreach (var registration in manager.Registrations.ToArray())
            {
                await manager.RemoveAsync(registration, TestContext.Current.CancellationToken);
            }
        }
    }

    [Fact]
    public void PluginPermissions_DeriveFromGraphDeclarationsAndCapabilities()
    {
        var package = CreatePackage("tests.permissions", "Device.Partition.Flash", "Device.Shell.Command",
            "Network.File.Download", "File.Bytes.Write");
        package.Manifest.Permissions = ["PackageInstall"];
        package.Manifest.Capabilities = ["RequiresNetwork"];

        var derived = PluginPermissions.Derive(package);

        Assert.True(derived.HasFlag(PluginPermission.DeviceFlash));
        Assert.True(derived.HasFlag(PluginPermission.DeviceShell));
        Assert.True(derived.HasFlag(PluginPermission.Network));
        Assert.True(derived.HasFlag(PluginPermission.FileWrite));
        Assert.True(derived.HasFlag(PluginPermission.PackageInstall));
        Assert.False(derived.HasFlag(PluginPermission.DeviceRecovery));

        var script = VisualPluginPackageBuilder.CreateScript("Reader", "tests.permission-script", "return 1");
        script.Manifest.Permissions = ["DeviceFlash"];
        Assert.Equal(PluginPermission.DeviceFlash, PluginPermissions.Derive(script));
    }

    [Fact]
    public void ScriptToolCatalog_PermissionFilter_RemovesUnauthorizedTools()
    {
        using var httpClient = new HttpClient();
        var context = new SecurityTestExecutionContext(TestContext.Current.CancellationToken);
        var gateway = new SecurityStubGateway();

        var none = ScriptToolCatalog.Create(context, gateway, httpClient, PluginPermission.None);
        var flash = ScriptToolCatalog.Create(context, gateway, httpClient, PluginPermission.DeviceFlash);
        var networkOnly = ScriptToolCatalog.Create(context, gateway, httpClient, PluginPermission.Network);
        var download = ScriptToolCatalog.Create(context, gateway, httpClient,
            PluginPermission.Network | PluginPermission.FileWrite);

        Assert.DoesNotContain("Device.Partition.Flash", none.Keys);
        Assert.DoesNotContain("Network.Download.File", none.Keys);
        Assert.Contains("Device.Slot.Query", none.Keys);
        Assert.Contains("Device.Partition.Flash", flash.Keys);
        Assert.DoesNotContain("Device.Shell.Command", flash.Keys);
        Assert.DoesNotContain("Network.Download.File", networkOnly.Keys);
        Assert.Contains("Network.Download.File", download.Keys);
    }

    [Fact]
    public void KeyLibrary_PublisherKey_RoundTripsExportsAndSignsPackages()
    {
        using var temp = new TempDirectory();
        var library = new KeyLibraryService(Path.Combine(temp.Path, "keys"));

        var generated = library.Generate("release publisher", ManagedKeyKind.Publisher, 3072);

        Assert.Equal(ManagedKeyKind.Publisher, generated.Kind);
        Assert.Equal(3072, generated.Bits);
        Assert.Contains("PUBLISHER", generated.Summary, StringComparison.Ordinal);
        Assert.Equal(generated.Id, Assert.Single(library.List()).Id);
        var publicKey = library.PublisherPublicKey(generated);
        Assert.True(PluginSignature.IsValidPublicKey(publicKey));

        using var privateKey = library.LoadPublisherPrivateKey(generated);
        Assert.Equal(generated.Fingerprint, PluginSignature.ComputeFingerprint(privateKey));
        var package = CreatePackage("tests.library-signed");
        PluginSignature.Sign(package, privateKey, "tests.publisher", "Library publisher");
        Assert.True(PluginSignature.Verify(package).IsValid);

        var exportRoot = Path.Combine(temp.Path, "export");
        Directory.CreateDirectory(exportRoot);
        var exported = library.Export(generated, exportRoot, includePrivate: true);
        var imported = library.Import("imported publisher", ManagedKeyKind.Publisher,
            Path.Combine(exported, "publisher-key.pem"), "");
        Assert.Equal(generated.Fingerprint, imported.Fingerprint);

        var ec = library.Generate("ec publisher", ManagedKeyKind.Publisher, 256);
        Assert.Equal(256, ec.Bits);
        Assert.Contains("EC P-256", ec.Summary, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => library.Generate("bad publisher", ManagedKeyKind.Publisher, 2048));
    }

    [Fact]
    public void VisualPluginValidation_RejectsUnknownPermissionsAndMismatchedSignature()
    {
        var package = CreatePackage("tests.validation-permissions");
        package.Manifest.Permissions = ["DeviceFlash", "UnknownCapability"];
        Assert.Contains(VisualPluginValidation.Validate(package),
            error => error.Contains("permissions", StringComparison.OrdinalIgnoreCase));

        package.Manifest.Permissions = ["DeviceFlash", "deviceflash"];
        Assert.Contains(VisualPluginValidation.Validate(package),
            error => error.Contains("permissions", StringComparison.OrdinalIgnoreCase));

        using var rsa = RSA.Create(3072);
        var signed = CreatePackage("tests.validation-signature");
        PluginSignature.Sign(signed, rsa, "tests.publisher", "Validation publisher");
        signed.Graph.Nodes[0].TypeKey = "Data.Value.Convert";
        signed.Manifest.RequiredNodeTypes = ["Data.Value.Convert"];
        Assert.Contains(VisualPluginValidation.Validate(signed),
            error => error.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    private static VisualPluginInstallConsent TrustingConsent(VisualPluginPackage package) => new()
    {
        AllowUntrusted = true,
        TrustPublisher = true,
        Permissions = PluginPermissions.Derive(package)
    };

    private static VisualPluginPackage CreatePackage(string id = "tests.signed", params string[] nodeTypes)
    {
        string[] types = nodeTypes.Length == 0 ? ["File.Binary.Read"] : nodeTypes;
        return new VisualPluginPackage
        {
            Kind = VisualPluginKind.CompositeNode,
            Manifest = new VisualPluginManifest
            {
                Id = id,
                Name = id,
                RequiredNodeTypes = [.. types]
            },
            Graph = new WorkflowSnapshot
            {
                Nodes = [.. types.Select((type, index) => new NodeDto
                {
                    InstanceId = "node" + index,
                    TypeKey = type
                })]
            }
        };
    }

    private sealed class SecurityTestExecutionContext(CancellationToken cancellationToken)
        : INodeExecutionContext, IUnifiedLog
    {
        public IUnifiedLog Log => this;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public void ReportProgress(double progress)
        {
        }

        public void Write(LogLevel level, string source, string message)
        {
        }

        public void Write(LogLevel level, string source, string format, params object[] args)
        {
        }

        public void SetMinimumLevel(LogLevel level)
        {
        }
    }

    private sealed class SecurityStubGateway : IScriptToolGateway
    {
        private readonly DetectedDevice _device = new()
        {
            Key = "tcp:127.0.0.1:5555",
            Serial = "S1",
            Protocol = DeviceProtocolKind.Fastboot,
            Transport = DeviceTransportKind.Tcp
        };

        public IReadOnlyList<DetectedDevice> Scan() => [_device];

        public Task<string> RebootAsync(DetectedDevice device, string mode, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<FlashResult> FlashImageAsync(DetectedDevice device, string partition, string imagePath,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FlashResult> ErasePartitionAsync(DetectedDevice device, string partition,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FlashResult> GetDevicePropertyAsync(DetectedDevice device, string propertyName,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<(string Name, long Size)>> ReadPartitionTableAsync(DetectedDevice device,
            bool showAll, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<(string Name, long Size)>>([("boot", 1024L)]);

        public Task<string> GetCurrentSlotAsync(DetectedDevice device, CancellationToken cancellationToken)
            => Task.FromResult("a");

        public Task<string> SetOtherSlotAsync(DetectedDevice device, CancellationToken cancellationToken)
            => Task.FromResult("b");

        public Task<string> RecoveryCleanAsync(DetectedDevice device, string mode,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> ShellAsync(DetectedDevice device, string command,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ManagedMirrorSession StartMirror(DetectedDevice device) => throw new NotSupportedException();
    }
}