using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Mtk;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed class MtkDirectoryKeyStoreTests : IDisposable
{
    private static readonly MtkDeviceInfo Device = new(0x766, 0x8A00, 0xCA01, 2, 0x17);

    private readonly string _directory = Directory.CreateTempSubdirectory("uotan-mtk-keys-").FullName;

    [Fact]
    public async Task ResolveSlaResponder_MatchingKeyFile_KeepsImportedKeyUsableAfterTheStoreReturns()
    {
        using var key = RSA.Create(1024);
        File.WriteAllText(Path.Combine(_directory, "sla_0766.pem"), ToPkcs8Pem(key));
        var store = new MtkDirectoryKeyStore(_directory);
        var responder = store.ResolveSlaResponder(Device);
        Assert.NotNull(responder);

        var response = await responder!.CreateResponseAsync(
            Device, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 }, TestContext.Current.CancellationToken);

        Assert.Equal(128, response.Length);
        Assert.True(VerifiesUnderPublicKey(key, response));
        Assert.NotEmpty(key.ExportParameters(false).Modulus!);
    }

    [Fact]
    public async Task ResolveSlaResponder_ResponderHeldAsInterface_DisposeRefusesFurtherResponses()
    {
        using var key = RSA.Create(1024);
        File.WriteAllText(Path.Combine(_directory, "sla_0766.pem"), ToPkcs8Pem(key));
        var store = new MtkDirectoryKeyStore(_directory);
        IMtkBootRomSlaResponder responder = store.ResolveSlaResponder(Device)!;
        var response = await responder.CreateResponseAsync(
            Device, new byte[] { 0x00, 0x01, 0x02, 0x03 }, TestContext.Current.CancellationToken);
        Assert.Equal(128, response.Length);

        responder.Dispose();
        responder.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => responder.CreateResponseAsync(
            Device, new byte[] { 0x00, 0x01, 0x02, 0x03 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveSlaResponder_DisposedResponder_ReleasesOnlyItsOwnKeyAndKeepsTheStoreUsable()
    {
        using var key = RSA.Create(1024);
        File.WriteAllText(Path.Combine(_directory, "sla_0766_8a00.pem"), ToPkcs8Pem(key));
        var store = new MtkDirectoryKeyStore(_directory);
        var first = store.ResolveSlaResponder(Device)!;
        var second = store.ResolveSlaResponder(Device)!;

        first.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.CreateResponseAsync(
            Device, new byte[] { 0x00, 0x01, 0x02, 0x03 }, TestContext.Current.CancellationToken));

        var response = await second.CreateResponseAsync(
            Device, new byte[] { 0x00, 0x01, 0x02, 0x03 }, TestContext.Current.CancellationToken);
        Assert.Equal(128, response.Length);
        second.Dispose();
    }

    [Fact]
    public void ResolveSlaResponder_UnsupportedKeyWidth_ThrowsAfterReleasingTheImportedKey()
    {
        using var key = RSA.Create(1536);
        File.WriteAllText(Path.Combine(_directory, "sla_0766.pem"), ToPkcs8Pem(key));
        var store = new MtkDirectoryKeyStore(_directory);

        Assert.Throws<ArgumentException>(() => store.ResolveSlaResponder(Device));
    }

    [Fact]
    public void ResolveSlaResponder_MalformedPem_ThrowsWithoutProducingAResponder()
    {
        File.WriteAllText(Path.Combine(_directory, "sla_0766.pem"), "-----BEGIN PRIVATE KEY-----\nnot-base64\n-----END PRIVATE KEY-----\n");
        var store = new MtkDirectoryKeyStore(_directory);

        Assert.Throws<ArgumentException>(() => store.ResolveSlaResponder(Device));
    }

    [Fact]
    public void ResolveSlaResponder_AbsentKeyFile_ReturnsNull()
    {
        Assert.Null(new MtkDirectoryKeyStore(_directory).ResolveSlaResponder(Device));
    }

    [Fact]
    public async Task OpenAsync_MatchingResourceFile_ReturnsOwnedCopyOfTheFileBytes()
    {
        var expected = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
        File.WriteAllBytes(Path.Combine(_directory, "daa_auth_0766.bin"), expected);
        var store = new MtkDirectoryKeyStore(_directory);

        using var resource = await store.OpenAsync(Device, MtkBootResourceKind.Authentication,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected.Length, resource.Length);
        var actual = new byte[resource.Length];
        Assert.Equal(expected.Length, await resource.Stream.ReadAsync(actual, TestContext.Current.CancellationToken));
        Assert.Equal(expected, actual);
        File.Delete(Path.Combine(_directory, "daa_auth_0766.bin"));
        Assert.Equal(expected.Length, resource.Length);
    }

    [Fact]
    public async Task OpenAsync_AbsentResourceFile_ThrowsWithHardwareCode()
    {
        var store = new MtkDirectoryKeyStore(_directory);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.OpenAsync(
            Device, MtkBootResourceKind.RootCertificate, TestContext.Current.CancellationToken));

        Assert.Contains("0x0766", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAsync_UnsupportedKind_RejectsBeforeTouchingTheDirectory()
    {
        var store = new MtkDirectoryKeyStore(_directory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.OpenAsync(
            Device, (MtkBootResourceKind)9, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private static string ToPkcs8Pem(RSA key)
    {
        var body = Convert.ToBase64String(key.ExportPkcs8PrivateKey(), Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN PRIVATE KEY-----\n{body}\n-----END PRIVATE KEY-----\n";
    }

    private static bool VerifiesUnderPublicKey(RSA key, byte[] response)
    {
        var publicKey = key.ExportParameters(false);
        var rawSignature = response.Chunk(2).SelectMany(pair => pair.Reverse()).ToArray();
        var recovered = System.Numerics.BigInteger.ModPow(
            new System.Numerics.BigInteger(rawSignature, isUnsigned: true, isBigEndian: true),
            new System.Numerics.BigInteger(publicKey.Exponent!, isUnsigned: true, isBigEndian: true),
            new System.Numerics.BigInteger(publicKey.Modulus!, isUnsigned: true, isBigEndian: true))
            .ToByteArray(isUnsigned: true, isBigEndian: true);
        var block = new byte[response.Length];
        recovered.CopyTo(block.AsSpan(block.Length - recovered.Length));
        return block[0] == 0x00 && block[1] == 0x01;
    }
}