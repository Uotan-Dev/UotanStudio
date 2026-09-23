using System.Numerics;
using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Mtk;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public sealed class MtkRsaSlaTests
{
    private static readonly MtkDeviceInfo Device = new(0x766, 0x8A00, 0xCA01, 2, 0x17);

    [Theory]
    [InlineData(1024, 2)]
    [InlineData(1024, 116)]
    [InlineData(2048, 16)]
    [InlineData(3072, 32)]
    [InlineData(4096, 64)]
    public async Task CreateResponseAsync_ExportableDedicatedKey_PublicExponentRecoversExactTypeOneBlock(int keyBits, int length)
    {
        using var key = RSA.Create(keyBits);
        var responder = new MtkBootRomRsaSlaResponder(key, Device);
        var challenge = Enumerable.Range(0, length).Select(index => (byte)index).ToArray();
        var original = challenge.ToArray();

        var response = await responder.CreateResponseAsync(Device, challenge, TestContext.Current.CancellationToken);

        Assert.Equal(original, challenge);
        Assert.Equal(keyBits / 8, response.Length);
        var rawSignature = response.Chunk(2).SelectMany(pair => pair.Reverse()).ToArray();
        var publicKey = key.ExportParameters(false);
        // Independent public RSA operation: do not use the production encoding engine to verify itself.
        var recovered = BigInteger.ModPow(
            new BigInteger(rawSignature, isUnsigned: true, isBigEndian: true),
            new BigInteger(publicKey.Exponent!, isUnsigned: true, isBigEndian: true),
            new BigInteger(publicKey.Modulus!, isUnsigned: true, isBigEndian: true))
            .ToByteArray(isUnsigned: true, isBigEndian: true);
        var block = new byte[response.Length];
        recovered.CopyTo(block.AsSpan(block.Length - recovered.Length));
        var expected = new byte[response.Length];
        expected[1] = 1;
        expected.AsSpan(2, expected.Length - length - 3).Fill(0xFF);
        original.Chunk(2).SelectMany(pair => pair.Reverse()).ToArray().CopyTo(expected.AsSpan(expected.Length - length));
        Assert.Equal(expected, block);
        Assert.NotEmpty(key.ExportParameters(false).Modulus!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(117)]
    [InlineData(118)]
    [InlineData(4096)]
    public async Task CreateResponseAsync_InvalidChallenge_RejectsWithoutMutatingInput(int length)
    {
        using var key = RSA.Create(1024);
        var responder = new MtkBootRomRsaSlaResponder(key, Device);
        var challenge = Enumerable.Repeat((byte)0x5A, length).ToArray();

        await Assert.ThrowsAsync<ArgumentException>(() => responder.CreateResponseAsync(
            Device, challenge, TestContext.Current.CancellationToken));

        Assert.All(challenge, value => Assert.Equal(0x5A, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateResponseAsync_DifferentIdentityOrSecurityFlags_Rejects(bool securityFlags)
    {
        using var key = RSA.Create(1024);
        var responder = new MtkBootRomRsaSlaResponder(key, Device);
        var observed = securityFlags ? Device with { TargetConfiguration = 0 }
            : Device with { HardwareCode = 0x765 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => responder.CreateResponseAsync(
            observed, new byte[16], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateResponseAsync_CallerReplacesKey_RejectsInsteadOfSigningWithAnotherKey()
    {
        using var key = RSA.Create(1024);
        using var replacement = RSA.Create(1024);
        var responder = new MtkBootRomRsaSlaResponder(key, Device);
        key.ImportParameters(replacement.ExportParameters(true));

        await Assert.ThrowsAsync<InvalidOperationException>(() => responder.CreateResponseAsync(
            Device, new byte[16], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateResponseAsync_PublicOnlyKey_FailsWithoutFallback()
    {
        using var privateKey = RSA.Create(1024);
        using var publicKey = RSA.Create();
        publicKey.ImportParameters(privateKey.ExportParameters(false));
        var responder = new MtkBootRomRsaSlaResponder(publicKey, Device);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => responder.CreateResponseAsync(
            Device, new byte[16], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateResponseAsync_PreCancelled_DoesNotRequirePrivateKeyAccess()
    {
        using var privateKey = RSA.Create(1024);
        using var publicKey = RSA.Create();
        publicKey.ImportParameters(privateKey.ExportParameters(false));
        var responder = new MtkBootRomRsaSlaResponder(publicKey, Device);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responder.CreateResponseAsync(
            Device, new byte[16], cancellation.Token));
    }
}