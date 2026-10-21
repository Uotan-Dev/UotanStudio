using System.Security.Cryptography;
using System.Text.Json;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Projects.Schemes;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Projects;

/// <summary>
/// Scheme signing and verification: canonical payload coverage, RSA-PSS and ECDSA round trips, tamper rejection,
/// detached signature fallback and publisher trust-on-first-use with key rotation.
/// <para>Scheme 签名与验签：规范化载荷覆盖、RSA-PSS 与 ECDSA 往返、篡改拒绝、分离签名回退，以及首次信任与密钥轮换。</para>
/// </summary>
public sealed class SchemeSignatureTests : IDisposable
{
    private const string PublisherId = "studio.publisher.one";
    private const string PublisherName = "Publisher One";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Sign_WithRsa_RecordsPublisherAndVerifies()
    {
        using var key = RSA.Create(2048);
        var package = CreatePackage();

        SchemeSigner.Sign(package, key, PublisherId, PublisherName);

        Assert.True(package.IsSigned);
        Assert.Equal(PublisherId, package.Publisher!.Id);
        Assert.Equal(PublisherName, package.Publisher.Name);
        Assert.StartsWith("SHA256:", package.Publisher.Fingerprint, StringComparison.Ordinal);
        Assert.Equal(PluginSignatureAlgorithms.RsaPssSha256, package.Signature!.Algorithm);
        var verification = SchemeSigner.Verify(package);
        Assert.True(verification.IsSigned);
        Assert.True(verification.IsValid);
        Assert.Null(verification.Error);
    }

    [Fact]
    public void Sign_WithEcdsaP256_UsesTheEcdsaAlgorithmAndVerifies()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePackage();

        SchemeSigner.Sign(package, key, PublisherId, PublisherName);

        Assert.Equal(PluginSignatureAlgorithms.EcdsaP256Sha256, package.Signature!.Algorithm);
        Assert.True(SchemeSigner.Verify(package).IsValid);
    }

    [Fact]
    public void Verify_AfterNameEdit_RejectsTheSignature()
    {
        using var key = RSA.Create(2048);
        var package = CreatePackage();
        SchemeSigner.Sign(package, key, PublisherId, PublisherName);

        package.Name = "Tampered";

        var verification = SchemeSigner.Verify(package);
        Assert.True(verification.IsSigned);
        Assert.False(verification.IsValid);
        Assert.NotNull(verification.Error);
    }

    [Fact]
    public void Verify_AfterStageBindingEdit_RejectsTheSignature()
    {
        using var key = RSA.Create(2048);
        var package = CreatePackage();
        SchemeSigner.Sign(package, key, PublisherId, PublisherName);

        package.Stages[0].Inputs.Add(new SchemeStageInputBinding
        {
            InputId = "payload",
            NodeInstanceId = "n1",
            PortId = "in"
        });

        Assert.False(SchemeSigner.Verify(package).IsValid);
    }

    [Fact]
    public void Verify_UnsignedPackage_IsNeitherSignedNorInvalid()
    {
        var verification = SchemeSigner.Verify(CreatePackage());

        Assert.False(verification.IsSigned);
        Assert.True(verification.IsValid);
    }

    [Fact]
    public void Sign_InvalidPublisherId_Throws()
    {
        using var key = RSA.Create(2048);
        var package = CreatePackage();

        Assert.Throws<ArgumentException>(() => SchemeSigner.Sign(package, key, "Bad Id", PublisherName));
    }

    [Fact]
    public async Task SaveAsync_SignedPackage_WritesDetachedSignatureAndLoadRestoresIt()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = RSA.Create(2048);
        var package = CreatePackage();
        SchemeSigner.Sign(package, key, PublisherId, PublisherName);
        var path = Path.Combine(_temp.Path, "signed.uotanscheme");

        Assert.True(await SchemeFile.SaveAsync(path, package, token));

        Assert.True(File.Exists(SchemeFile.GetSignaturePath(path)));
        var inlineStripped = CreatePackage();
        inlineStripped.Publisher = package.Publisher;
        inlineStripped.Signature = null;
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(inlineStripped, SchemeJsonContext.Default.SchemePackage), token);

        var restored = Assert.IsType<SchemePackage>(await SchemeFile.LoadAsync(path, token));

        Assert.True(restored.IsSigned);
        Assert.True(SchemeSigner.Verify(restored).IsValid);
    }

    [Fact]
    public async Task SaveAsync_MalformedDetachedSignature_LoadsAsUnsigned()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = RSA.Create(2048);
        var package = CreatePackage();
        SchemeSigner.Sign(package, key, PublisherId, PublisherName);
        var path = Path.Combine(_temp.Path, "sidecar.uotanscheme");
        Assert.True(await SchemeFile.SaveAsync(path, package, token));
        SchemePackage? inlineStripped;
        await using (var unsignedStream = File.OpenRead(path))
        {
            inlineStripped = await JsonSerializer.DeserializeAsync(
                unsignedStream, SchemeJsonContext.Default.SchemePackage, token);
        }
        Assert.NotNull(inlineStripped);
        inlineStripped.Signature = null;
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(inlineStripped, SchemeJsonContext.Default.SchemePackage), token);
        await File.WriteAllTextAsync(SchemeFile.GetSignaturePath(path), "{ not json", token);

        var restored = Assert.IsType<SchemePackage>(await SchemeFile.LoadAsync(path, token));

        Assert.False(restored.IsSigned);
        Assert.NotNull(await SchemeFile.LoadVerifiedAsync(path, token));
    }

    [Fact]
    public async Task SaveAsync_StaleSignature_RefusesTheWrite()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = RSA.Create(2048);
        var package = CreatePackage();
        SchemeSigner.Sign(package, key, PublisherId, PublisherName);
        var path = Path.Combine(_temp.Path, "stale.uotanscheme");
        Assert.True(await SchemeFile.SaveAsync(path, package, token));
        var original = await File.ReadAllTextAsync(path, token);

        package.Name = "Changed after signing";

        Assert.False(await SchemeFile.SaveAsync(path, package, token));
        Assert.Equal(original, await File.ReadAllTextAsync(path, token));
    }

    [Fact]
    public async Task SaveAsync_UnsignedPackage_RemovesTheStaleDetachedSignature()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = RSA.Create(2048);
        var signed = CreatePackage();
        SchemeSigner.Sign(signed, key, PublisherId, PublisherName);
        var path = Path.Combine(_temp.Path, "unsigned.uotanscheme");
        Assert.True(await SchemeFile.SaveAsync(path, signed, token));
        Assert.True(File.Exists(SchemeFile.GetSignaturePath(path)));

        Assert.True(await SchemeFile.SaveAsync(path, CreatePackage(), token));

        Assert.False(File.Exists(SchemeFile.GetSignaturePath(path)));
        Assert.False(Assert.IsType<SchemePackage>(await SchemeFile.LoadAsync(path, token)).IsSigned);
    }

    [Fact]
    public async Task LoadVerifiedAsync_TamperedSignedFile_ReturnsNull()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = RSA.Create(2048);
        var package = CreatePackage();
        SchemeSigner.Sign(package, key, PublisherId, PublisherName);
        var path = Path.Combine(_temp.Path, "tampered.uotanscheme");
        Assert.True(await SchemeFile.SaveAsync(path, package, token));
        var text = await File.ReadAllTextAsync(path, token);
        await File.WriteAllTextAsync(path, text.Replace("Signed scheme", "Tampered scheme", StringComparison.Ordinal), token);

        Assert.Null(await SchemeFile.LoadVerifiedAsync(path, token));
        Assert.NotNull(await SchemeFile.LoadAsync(path, token));
        Assert.False((await SchemeFile.VerifyAsync(path, token)).IsValid);
    }

    [Fact]
    public async Task SignAsync_UnsignedFile_PersistsBothSignatureForms()
    {
        var token = TestContext.Current.CancellationToken;
        using var key = RSA.Create(2048);
        var path = Path.Combine(_temp.Path, "sign-in-place.uotanscheme");
        Assert.True(await SchemeFile.SaveAsync(path, CreatePackage(), token));

        Assert.True(await SchemeFile.SignAsync(path, key, PublisherId, PublisherName, token));

        Assert.True(File.Exists(SchemeFile.GetSignaturePath(path)));
        var verification = await SchemeFile.VerifyAsync(path, token);
        Assert.True(verification.IsSigned);
        Assert.True(verification.IsValid);
        Assert.NotNull(await SchemeFile.LoadVerifiedAsync(path, token));
    }

    [Fact]
    public void GetTrustStatus_FirstUseThenRotation_TracksPinsAndFingerprintChanges()
    {
        var store = new PublisherTrustStore(Path.Combine(_temp.Path, "trust.json"));
        using var firstKey = RSA.Create(2048);
        using var rotatedKey = RSA.Create(2048);
        var package = CreatePackage();
        SchemeSigner.Sign(package, firstKey, PublisherId, PublisherName);

        Assert.Equal(PublisherTrustStatus.UnknownPublisher, SchemeSigner.GetTrustStatus(package, store));

        var pin = SchemeSigner.TrustPublisher(package, store);

        Assert.Equal(package.Publisher!.Fingerprint, pin.Fingerprint);
        Assert.Equal(PublisherTrustStatus.Trusted, SchemeSigner.GetTrustStatus(package, store));

        SchemeSigner.Sign(package, rotatedKey, PublisherId, PublisherName);

        Assert.Equal(PublisherTrustStatus.FingerprintMismatch, SchemeSigner.GetTrustStatus(package, store));

        var rotatedPin = SchemeSigner.TrustPublisher(package, store);

        Assert.Equal(package.Publisher.Fingerprint, rotatedPin.Fingerprint);
        Assert.Equal(PublisherTrustStatus.Trusted, SchemeSigner.GetTrustStatus(package, store));
    }

    [Fact]
    public void GetTrustStatus_UnsignedPackage_ReportsUnsigned()
    {
        var store = new PublisherTrustStore(Path.Combine(_temp.Path, "trust.json"));

        Assert.Equal(PublisherTrustStatus.Unsigned,
            SchemeSigner.GetTrustStatus(CreatePackage(), store));
        Assert.Throws<InvalidOperationException>(() => SchemeSigner.TrustPublisher(CreatePackage(), store));
    }

    [Fact]
    public async Task LoadAsync_LegacyUnsignedJson_ReadsWithoutSignatureFields()
    {
        var token = TestContext.Current.CancellationToken;
        var path = _temp.Write("legacy.uotanscheme", """
            {
              "Id": "legacy",
              "Name": "Legacy",
              "Stages": [
                { "Id": "only", "Kind": "Generic", "Graph": { "FormatVersion": 1, "Nodes": [], "Connections": [] } }
              ]
            }
            """);

        var package = Assert.IsType<SchemePackage>(await SchemeFile.LoadAsync(path, token));

        Assert.False(package.IsSigned);
        Assert.Null(package.Publisher);
        Assert.Null(package.Signature);
        Assert.NotNull(await SchemeFile.LoadVerifiedAsync(path, token));
    }

    private static SchemePackage CreatePackage() => new()
    {
        Id = "signing",
        Name = "Signed scheme",
        Version = "1.2.3",
        Inputs = [new SchemeInputDefinition { Id = "payload", Name = "Payload", Type = SchemeInputType.Path }],
        Stages =
        [
            new SchemeStage
            {
                Id = "only",
                Kind = SchemeStageKind.Patch,
                Graph = new WorkflowSnapshot
                {
                    Nodes = [new NodeDto { InstanceId = "n1", TypeKey = "Test.Scheme.Echo" }],
                    Connections = []
                }
            }
        ]
    };
}