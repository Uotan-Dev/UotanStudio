using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Services.Keys;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class KeyLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-keys-test-" + Guid.NewGuid().ToString("N"));
    private KeyLibraryService Library => new(Path.Combine(_root, "library"));

    [Theory]
    [InlineData(ManagedKeyKind.Adb, 2048)]
    [InlineData(ManagedKeyKind.Hdc, 3072)]
    [InlineData(ManagedKeyKind.Payload, 2048)]
    [InlineData(ManagedKeyKind.Payload, 4096)]
    public void Generate_ProtocolKeys_SignAndVerify(ManagedKeyKind kind, int bits)
    {
        var library = Library;
        var key = library.Generate("sample", kind, bits);
        using var privateKey = RSA.Create();
        privateKey.ImportFromPem(File.ReadAllText(library.PrivatePath(key)));
        using var publicKey = RSA.Create();
        if (kind == ManagedKeyKind.Adb)
        {
            var encoded = library.PublicText(key);
            Assert.DoesNotContain('\0', encoded);
            var wire = Convert.FromBase64String(encoded.Split(' ')[0]);
            Assert.Equal(524, wire.Length);
            Assert.Equal(64u, BinaryPrimitives.ReadUInt32LittleEndian(wire));
            var modulus = wire[8..264];
            Array.Reverse(modulus);
            publicKey.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = [1, 0, 1] });
            Assert.Equal(65537u, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(520)));
        }
        else publicKey.ImportFromPem(library.PublicText(key));
        var data = RandomNumberGenerator.GetBytes(32);
        var signature = privateKey.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.True(publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        data[0] ^= 1;
        Assert.False(publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        if (kind == ManagedKeyKind.Hdc)
        {
            using var hdc = HdcAuthentication.LoadPrivateKey(library.PrivatePath(key));
            Assert.True(publicKey.VerifyData(data, hdc.SignToken(data), HashAlgorithmName.SHA512, RSASignaturePadding.Pss));
        }
        Assert.Equal(key, Assert.Single(new KeyLibraryService(library.Root).List()));
    }

    [Fact]
    public void Management_RenameExportRemove_PreservesIdentityAndSource()
    {
        var library = Library;
        var key = library.Generate("original", ManagedKeyKind.Payload, 2048);
        var bytes = File.ReadAllBytes(library.PrivatePath(key));
        var renamed = library.Rename(key, "renamed");
        Assert.Equal(key.Fingerprint, renamed.Fingerprint);
        Assert.Equal("renamed", Assert.Single(library.List()).Name);
        var output = Path.Combine(_root, "export");
        Directory.CreateDirectory(output);
        var exported = library.Export(renamed, output, true);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(exported, "payload-key.pem")));
        Assert.Throws<IOException>(() => library.Export(renamed, output, true));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(exported, "payload-key.pem")));
        library.Remove(renamed);
        Assert.Empty(library.List());
        Assert.True(File.Exists(Path.Combine(exported, "payload-key.pem")));
        Assert.Single(Directory.GetDirectories(Path.Combine(library.Root, ".deleted")));
    }

    [Fact]
    public void Import_EncryptedPem_ValidatesPasswordAndPrivateMaterial()
    {
        Directory.CreateDirectory(_root);
        using var rsa = RSA.Create(2048);
        var path = Path.Combine(_root, "input.pem");
        File.WriteAllText(path, rsa.ExportEncryptedPkcs8PrivateKeyPem("password", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10000)));
        Assert.ThrowsAny<CryptographicException>(() => Library.Import("bad", ManagedKeyKind.Payload, path, "wrong"));
        Assert.Empty(Library.List());
        var key = Library.Import("imported", ManagedKeyKind.Payload, path, "password");
        Assert.Equal("SHA256:" + Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())), key.Fingerprint);
        File.WriteAllText(path, rsa.ExportSubjectPublicKeyInfoPem());
        Assert.ThrowsAny<CryptographicException>(() => Library.Import("public", ManagedKeyKind.Payload, path, ""));
        Assert.Single(Library.List());
    }

    [Fact]
    public void InvalidInputs_DoNotPublishKeys()
    {
        Assert.Throws<ArgumentException>(() => Library.Generate(" ", ManagedKeyKind.Adb, 2048));
        Assert.Throws<ArgumentException>(() => Library.Generate("wrong", ManagedKeyKind.Hdc, 2048));
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "large.pem");
        File.WriteAllBytes(path, new byte[65537]);
        Assert.Throws<InvalidDataException>(() => Library.Import("large", ManagedKeyKind.Payload, path, ""));
        Assert.Empty(Library.List());
    }

    [Fact]
    public async Task NewAndDelete_RequireConfirmation_KeepOtherKeysAndExports()
    {
        var library = Library;
        var first = library.Generate("first", ManagedKeyKind.Payload, 2048);
        var second = library.Generate("second", ManagedKeyKind.Adb, 2048);
        var exported = library.Export(first, _root, true);
        var model = new UotanStudio.ViewModels.Shell.KeyManagerViewModel(library);
        await model.RefreshCommand.ExecuteAsync(null);
        model.SelectedKey = first;
        await model.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(2, library.List().Count);
        model.ConfirmDeletion = true;
        model.NewKeyCommand.Execute(null);
        Assert.Null(model.SelectedKey);
        Assert.False(model.ConfirmDeletion);
        Assert.Empty(model.Name);
        Assert.Equal(2, library.List().Count);
        model.SelectedKey = first;
        model.ConfirmDeletion = true;
        await model.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(second.Id, Assert.Single(library.List()).Id);
        Assert.False(File.Exists(library.PrivatePath(first)));
        Assert.True(File.Exists(Path.Combine(exported, "payload-key.pem")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}