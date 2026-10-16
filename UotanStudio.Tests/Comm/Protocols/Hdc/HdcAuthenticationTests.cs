using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Hdc;

public sealed class HdcAuthenticationTests
{
    [Fact]
    public void DefaultIdentity_UsesTheUpstreamHostLocation_AndSurvivesReload()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harmony"),
            HdcAuthentication.DefaultKeyDirectory);
        Assert.EndsWith("hdckey", HdcAuthentication.DefaultKeyFilePath, StringComparison.Ordinal);
        var root = Path.Combine(Path.GetTempPath(), "hdc-identity-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = HdcAuthentication.LoadOrCreate(Path.Combine(root, "key"));
            using var second = HdcAuthentication.LoadOrCreate(Path.Combine(root, "key"));
            Assert.Equal(first.PublicKeyToBase64(), second.PublicKeyToBase64());
            using var verifier = RSA.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(second.PublicKeyToBase64()), out _);
            Assert.True(verifier.VerifyData("challenge"u8.ToArray(), first.SignToken("challenge"u8.ToArray()),
                HashAlgorithmName.SHA512, RSASignaturePadding.Pss));
            Assert.Empty(Directory.GetFiles(root, ".hdckey-*"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PrimarySignature_MatchesTheUpstreamRsa3072Sha512Algorithm()
    {
        using var auth = HdcAuthentication.CreateNew();
        var token = "upstream daemon challenge"u8.ToArray();
        var signature = auth.SignToken(token);

        Assert.Equal(384, signature.Length);
        using var verifier = RSA.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(auth.PublicKeyToBase64()), out _);
        Assert.True(verifier.VerifyData(token, signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pss));
        Assert.False(verifier.VerifyData(token, signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1));
        Assert.False(verifier.VerifyData(token, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void GeneratedIdentity_UsesTheUpstreamKeyBits_AndWritesTheUpstreamPemPublicKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "hdc-pubkey-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "hdckey");
            using var auth = HdcAuthentication.LoadOrCreate(path);
            using var key = RSA.Create();
            key.ImportFromPem(File.ReadAllText(path));
            Assert.Equal(3072, key.KeySize);
            Assert.Equal(3072, HdcAuthentication.KeyBits);
            Assert.Equal(new[] { 3072, 4096 }, HdcAuthentication.SupportedKeyBits);

            var text = File.ReadAllText(path + ".pub").TrimEnd('\n');
            using var publicKey = RSA.Create();
            publicKey.ImportFromPem(text);
            Assert.Equal(key.ExportSubjectPublicKeyInfo(), publicKey.ExportSubjectPublicKeyInfo());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void LegacyIdentity_UsesTheWidthEarlierReleasesCreated_AndStillLoadsAndSigns()
    {
        var root = Path.Combine(Path.GetTempPath(), "hdc-legacy-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "hdckey");
            using (var legacy = RSA.Create(4096))
            {
                File.WriteAllText(path, legacy.ExportRSAPrivateKeyPem());
            }

            using var auth = HdcAuthentication.LoadOrCreate(path);
            using var reloaded = RSA.Create();
            reloaded.ImportFromPem(File.ReadAllText(path));
            Assert.Equal(4096, reloaded.KeySize);

            var token = "legacy daemon challenge"u8.ToArray();
            var signature = auth.SignToken(token);
            Assert.Equal(512, signature.Length);
            using var verifier = RSA.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(auth.PublicKeyToBase64()), out _);
            Assert.True(verifier.VerifyData(token, signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pss));
            Assert.True(File.Exists(path + ".pub"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConcurrentIdentityCreation_PublishesOneCompleteKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "hdc-key-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            var keys = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                using var auth = HdcAuthentication.LoadOrCreate(Path.Combine(root, "key"));
                return auth.PublicKeyToBase64();
            }, TestContext.Current.CancellationToken)));
            Assert.Single(keys.Distinct());
            Assert.Empty(Directory.GetFiles(root, ".hdckey-*"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void LegacySignature_IsPrivateBlockTypeOne_NotPublicEncryption()
    {
        using var rsa = HdcTestKeys.CreateRsa();
        var key = rsa.ExportParameters(false);
        using var auth = new HdcAuthentication(rsa, HdcAuthVerifyType.RsaEncrypt);
        var token = "independent raw challenge"u8.ToArray();
        var signature = auth.SignToken(token);
        var decoded = BigInteger.ModPow(new BigInteger(signature, true, true),
            new BigInteger(key.Exponent!, true, true), new BigInteger(key.Modulus!, true, true)).ToByteArray(true, true);
        var blockLength = key.Modulus!.Length - 1;
        Assert.Equal(blockLength, decoded.Length);
        Assert.Equal(1, decoded[0]);
        Assert.All(decoded[1..^(token.Length + 1)], value => Assert.Equal(255, value));
        Assert.Equal(0, decoded[^(token.Length + 1)]);
        Assert.Equal(token, decoded[^token.Length..]);
        Assert.Throws<NotSupportedException>(() => auth.SignToken(token, HdcAuthVerifyType.PskTlsAes128GcmSha256));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthorizationProgress_DoesNotAuthorizeUntilChallengeAndFinalOk(bool denied)
    {
        using var transport = new FakeCommTransport();
        using var auth = HdcAuthentication.CreateNew();
        var signed = false;
        var token = "daemon challenge"u8.ToArray();
        byte[] Reply(byte state, byte[] value) => HdcDaemonCodec.Encode(new(HdcCommand.Handshake, 0, 0,
            HdcDaemonCodec.Handshake(state, 0, "", value, "Ver: 3.2.0f")));
        transport.OnWrite = bytes =>
        {
            var message = HdcDaemonCodec.Decode(bytes);
            if (message.Command != HdcCommand.Handshake) return [];
            var handshake = HdcDaemonCodec.ReadHandshake(message.Payload!);
            if (handshake.State == 0) return [Reply(3, Tlv("authtype", "1"))];
            if (handshake.State == 3)
                return [Reply(4, [.. Tlv("emgmsg", denied ? "[E000003]:denied" : "[E000002]:confirm"), .. Tlv("daemonauthstatus", "DAEMON_UNAUTH")]),
                    HdcDaemonCodec.Encode(new(HdcCommand.ChannelClose, 0, 0, [1])), Reply(2, token)];
            using var verifier = RSA.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(auth.PublicKeyToBase64()), out _);
            var signature = Convert.FromBase64String(Encoding.ASCII.GetString(handshake.Buffer));
            signed = verifier.VerifyData(token, signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pss);
            return [Reply(4, Tlv("daemonauthstatus", "DAEMON_AUTH")),
                HdcDaemonCodec.Encode(new(HdcCommand.ChannelClose, 0, 0, [1]))];
        };
        using var connection = new HdcConnection(transport, auth);
        if (denied)
        {
            Assert.Throws<UnauthorizedAccessException>(connection.Connect);
            Assert.False(connection.IsConnected);
            Assert.False(signed);
        }
        else
        {
            connection.Connect();
            Assert.True(signed);
            Assert.True(connection.IsConnected);
            Assert.Equal("Ver: 3.2.0f", connection.PeerVersionString);
        }
    }

    [Fact]
    public void HostIdentityFiles_UseUpstreamPkcs8PrivateAndPemSidecarWithSingleNewline()
    {
        var root = Path.Combine(Path.GetTempPath(), "hdc-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "hdckey");
            using var identity = HdcAuthentication.LoadOrCreate(path);

            var privateText = File.ReadAllText(path);
            Assert.StartsWith("-----BEGIN PRIVATE KEY-----", privateText, StringComparison.Ordinal);
            Assert.EndsWith("-----END PRIVATE KEY-----\n", privateText, StringComparison.Ordinal);
            Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", privateText, StringComparison.Ordinal);

            var publicText = File.ReadAllText(path + ".pub");
            Assert.StartsWith("-----BEGIN PUBLIC KEY-----", publicText, StringComparison.Ordinal);
            Assert.EndsWith("-----END PUBLIC KEY-----\n", publicText, StringComparison.Ordinal);
            Assert.False(publicText.EndsWith("\n\n", StringComparison.Ordinal));
            Assert.False(publicText.EndsWith("\r", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void HandshakePublicKeyMessage_CarriesHostNameFormFeedAndPemAsUpstreamSplitsIt()
    {
        using var transport = new FakeCommTransport();
        using var auth = HdcAuthentication.CreateNew();
        var token = "daemon challenge"u8.ToArray();
        byte[]? announced = null;
        byte[] Reply(byte state, byte[] value) => HdcDaemonCodec.Encode(new(HdcCommand.Handshake, 0, 0,
            HdcDaemonCodec.Handshake(state, 0, "", value, "Ver: 3.2.0f")));
        transport.OnWrite = bytes =>
        {
            var message = HdcDaemonCodec.Decode(bytes);
            if (message.Command != HdcCommand.Handshake) return [];
            var handshake = HdcDaemonCodec.ReadHandshake(message.Payload!);
            if (handshake.State == 0) return [Reply(3, Tlv("authtype", "1"))];
            if (handshake.State == 3)
            {
                announced = handshake.Buffer;
                return [Reply(2, token)];
            }

            return [Reply(4, Tlv("daemonauthstatus", "DAEMON_AUTH")),
                HdcDaemonCodec.Encode(new(HdcCommand.ChannelClose, 0, 0, [1]))];
        };

        using var connection = new HdcConnection(transport, auth);
        connection.Connect();

        Assert.NotNull(announced);
        var separator = Encoding.UTF8.GetString(announced).IndexOf('\f');
        Assert.True(separator > 0);
        var host = Encoding.UTF8.GetString(announced[..separator]);
        var pem = Encoding.UTF8.GetString(announced[(separator + 1)..]);
        Assert.Equal(Dns.GetHostName(), host);
        Assert.DoesNotContain('@', host);
        Assert.False(pem.EndsWith("\n\n", StringComparison.Ordinal));
        using var parsed = RSA.Create();
        parsed.ImportFromPem(pem);
        Assert.Equal(Convert.FromBase64String(auth.PublicKeyToBase64()), parsed.ExportSubjectPublicKeyInfo());
    }

    private static byte[] Tlv(string tag, string value)
        => Encoding.UTF8.GetBytes(tag.PadRight(16) + Encoding.UTF8.GetByteCount(value).ToString().PadRight(16) + value);
}