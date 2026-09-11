using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public sealed class HdcAuthenticationTests
{
    [Fact]
    public void DefaultIdentity_IsApplicationOwned_AndSurvivesReload()
    {
        Assert.Contains("UotanStudio", HdcAuthentication.DefaultKeyDirectory);
        Assert.DoesNotContain(".harmony", HdcAuthentication.DefaultKeyFilePath);
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
        using var rsa = RSA.Create(3072);
        var key = rsa.ExportParameters(false);
        using var auth = new HdcAuthentication(rsa, HdcAuthVerifyType.RsaEncrypt);
        var token = "independent raw challenge"u8.ToArray();
        var signature = auth.SignToken(token);
        var decoded = BigInteger.ModPow(new BigInteger(signature, true, true),
            new BigInteger(key.Exponent!, true, true), new BigInteger(key.Modulus!, true, true)).ToByteArray(true, true);
        // BigInteger omits the leading zero from 00 01 FF..FF 00 token.
        Assert.Equal(383, decoded.Length);
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
            signed = verifier.VerifyData(token, Convert.FromBase64String(Encoding.ASCII.GetString(handshake.Buffer)),
                HashAlgorithmName.SHA512, RSASignaturePadding.Pss);
            return [Reply(4, Tlv("daemonauthstatus", "DAEMON_AUTH"))];
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

    private static byte[] Tlv(string tag, string value)
        => Encoding.UTF8.GetBytes(tag.PadRight(16) + Encoding.UTF8.GetByteCount(value).ToString().PadRight(16) + value);
}
