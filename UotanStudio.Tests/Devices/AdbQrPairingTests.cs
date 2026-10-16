using System.Buffers.Binary;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Adb.Pairing;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class AdbQrPairingTests
{
    [Fact]
    public void GenerateCredentials_RandomAlphanumeric_MatchesAdbWifiLayout()
    {
        var credentials = AdbQrPairing.GenerateCredentials();

        Assert.Matches("^ADB_WIFI_[0-9A-Za-z]{14}-[0-9A-Za-z]{6}$", credentials.ServiceName);
        Assert.Matches("^[0-9A-Za-z]{21}$", credentials.Password);
    }

    [Fact]
    public void GenerateCredentials_RepeatedCalls_ProduceDistinctValues()
    {
        var first = AdbQrPairing.GenerateCredentials();
        var second = AdbQrPairing.GenerateCredentials();

        Assert.NotEqual(first.ServiceName, second.ServiceName);
        Assert.NotEqual(first.Password, second.Password);
    }

    [Fact]
    public void BuildPayload_KnownCredentials_ProducesWifiProvisioningFormat()
    {
        var payload = AdbQrPairing.BuildPayload(new AdbQrPairingCredentials("ADB_WIFI_aaaaaaaaaaaaaa-bbbbbb", "password123"));

        Assert.Equal("WIFI:T:ADB;S:ADB_WIFI_aaaaaaaaaaaaaa-bbbbbb;P:password123;;", payload);
    }

    [Fact]
    public void ConnectPortException_Construction_PreservesRouteHostAndTimeoutBase()
    {
        var exception = new AdbQrPairingConnectPortException(AdbQrPairingRoute.Direct, "192.0.2.1", "no connect service");

        Assert.IsAssignableFrom<TimeoutException>(exception);
        Assert.Equal(AdbQrPairingRoute.Direct, exception.Route);
        Assert.Equal("192.0.2.1", exception.Host);
    }

    [Fact]
    public async Task ConnectPairedAsync_DirectRoute_ReturnsEndpointWithoutNetwork()
    {
        var endpoint = await AdbQrPairingService.ConnectPairedAsync(
            AdbQrPairingRoute.Direct, "192.0.2.1", 37177, TestContext.Current.CancellationToken);

        Assert.Equal("192.0.2.1:37177", endpoint);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task ConnectPairedAsync_PortOutOfRange_ThrowsArgumentOutOfRange(int port)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AdbQrPairingService.ConnectPairedAsync(
            AdbQrPairingRoute.Direct, "192.0.2.1", port, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectPairedAsync_BlankHost_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => AdbQrPairingService.ConnectPairedAsync(
            AdbQrPairingRoute.Direct, " ", 5555, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectPairedAsync_ServerRoute_RefusedEndpoint_ThrowsIOException()
    {
        await Assert.ThrowsAsync<IOException>(() => AdbQrPairingService.ConnectPairedAsync(
            AdbQrPairingRoute.AdbServer, "127.0.0.1", 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MultiplyBaseForTest_ScalarTwo_MatchesRfc8032Vector()
    {
        var encoded = AdbSpake2.MultiplyBaseForTest(new BigInteger(2));

        Assert.Equal("c9a3f86aae465f0e56513864510f3997561fa2c9e85ea21dc2292309f3cd6022", Convert.ToHexString(encoded).ToLowerInvariant());
    }

    [Fact]
    public async Task PairLoopback_SamePassword_DerivesSharedKeyAndReturnsGuid()
    {
        var password = "one-time-qr-password";
        var expectedGuid = "adb-test-LOOP01";
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await RunPairingServerAsync(accepted, password, expectedGuid);
        }, TestContext.Current.CancellationToken);
        var clientTask = AdbPairingConnection.PairAsync("127.0.0.1", port, password, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, clientTask).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expectedGuid, await clientTask);
    }

    [Fact]
    public async Task PairLoopback_WrongPassword_PeerKeysDivergeSoGcmFails()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<CryptographicException>(
                () => RunPairingServerAsync(accepted, "correct-password", "adb-test-LOOP02"));
        }, TestContext.Current.CancellationToken);
        var clientTask = AdbPairingConnection.PairAsync("127.0.0.1", port, "wrong-password",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(() => clientTask);
        await serverTask;
    }

    [Fact]
    public async Task Spake2_SamePassword_DerivesEqualKeys()
    {
        var client = AdbSpake2.CreateClient();
        var server = AdbSpake2.CreateServer();
        var clientMessage = client.GenerateMessage("pairing-code"u8.ToArray());
        var serverMessage = server.GenerateMessage("pairing-code"u8.ToArray());
        var clientKey = client.ProcessMessage(serverMessage);
        var serverKey = server.ProcessMessage(clientMessage);

        Assert.Equal(64, clientKey.Length);
        Assert.Equal(clientKey, serverKey);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Spake2_DifferentPassword_DerivesDivergentKeys()
    {
        var client = AdbSpake2.CreateClient();
        var server = AdbSpake2.CreateServer();
        var clientMessage = client.GenerateMessage("pairing-code"u8.ToArray());
        var serverMessage = server.GenerateMessage("different-code"u8.ToArray());
        var clientKey = client.ProcessMessage(serverMessage);
        var serverKey = server.ProcessMessage(clientMessage);

        Assert.NotEqual(clientKey, serverKey);
        await Task.CompletedTask;
    }

    private static async Task RunPairingServerAsync(TcpClient client, string password, string guid)
    {
        using var authentication = AdbAuthentication.LoadOrCreate();
        using var stream = client.GetStream();
        var crypto = new BcTlsCrypto(new SecureRandom());
        using var certificate = AdbHostCertificate.Create(authentication.RsaKey);
        var certificateDer = certificate.RawDataMemory.ToArray();
        var privateKey = DotNetUtilities.GetKeyPair(authentication.RsaKey).Private;

        var protocol = new TlsServerProtocol(stream);
        var server = new LoopbackPairingTlsServer(crypto, certificateDer, privateKey);
        try
        {
            protocol.Accept(server);
            var exported = server.ExportedKeyMaterial ?? [];
            Assert.Equal(64, exported.Length);

            var passwordBytes = System.Text.Encoding.ASCII.GetBytes(password);
            var spakePassword = new byte[passwordBytes.Length + exported.Length];
            passwordBytes.CopyTo(spakePassword, 0);
            exported.CopyTo(spakePassword, passwordBytes.Length);

            var spake = AdbSpake2.CreateServer();
            var (clientFrameType, clientPayload) = ReadFrame(protocol.Stream);
            Assert.Equal(0, clientFrameType);
            Assert.Equal(32, clientPayload.Length);
            var serverMessage = spake.GenerateMessage(spakePassword);
            WriteFrame(protocol.Stream, 0, serverMessage);
            var keyMaterial = spake.ProcessMessage(clientPayload);

            var aesKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, keyMaterial, 16,
                salt: Array.Empty<byte>(), info: "adb pairing_auth aes-128-gcm key"u8.ToArray());
            using var aes = new AesGcm(aesKey, 16);

            var (infoType, encryptedInfo) = ReadFrame(protocol.Stream);
            Assert.Equal(1, infoType);
            var peerInfo = Decrypt(aes, encryptedInfo, 0);
            Assert.Equal(8192, peerInfo.Length);
            Assert.Equal(0, peerInfo[0]);

            var response = new byte[8192];
            response[0] = 1;
            System.Text.Encoding.ASCII.GetBytes(guid).CopyTo(response, 1);
            WriteFrame(protocol.Stream, 1, Encrypt(aes, response, 0));
        }
        finally
        {
            protocol.Close();
        }
        await Task.CompletedTask;
    }

    private sealed class LoopbackPairingTlsServer(BcTlsCrypto crypto, byte[] certificateDer,
        AsymmetricKeyParameter privateKey) : DefaultTlsServer(crypto)
    {
        internal byte[]? ExportedKeyMaterial { get; private set; }

        public override ProtocolVersion[] GetProtocolVersions() => [ProtocolVersion.TLSv13];

        public override void NotifyHandshakeComplete()
        {
            ExportedKeyMaterial = m_context.ExportKeyingMaterial("adb-label\0", null, 64);
            base.NotifyHandshakeComplete();
        }

        public override TlsCredentials GetCredentials() => new BcDefaultTlsCredentialedSigner(
            new TlsCryptoParameters(m_context), crypto, privateKey,
            new Certificate(Array.Empty<byte>(), [new CertificateEntry(crypto.CreateCertificate(certificateDer), null)]),
            SignatureAndHashAlgorithm.rsa_pss_rsae_sha256);

        public override void NotifyClientCertificate(Certificate clientCertificate)
        {
        }
    }

    private static byte[] Encrypt(AesGcm aes, byte[] plaintext, ulong sequence)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, sequence);
        var ciphertext = new byte[plaintext.Length + 16];
        aes.Encrypt(nonce, plaintext, ciphertext.AsSpan(..^16), ciphertext.AsSpan(^16..));
        return ciphertext;
    }

    private static byte[] Decrypt(AesGcm aes, byte[] ciphertext, ulong sequence)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, sequence);
        var plaintext = new byte[ciphertext.Length - 16];
        aes.Decrypt(nonce, ciphertext.AsSpan(..^16), ciphertext.AsSpan(^16..), plaintext);
        return plaintext;
    }

    private static void WriteFrame(Stream stream, byte type, byte[] payload)
    {
        var header = new byte[6];
        header[0] = 1;
        header[1] = type;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(2), (uint)payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    private static (byte Type, byte[] Payload) ReadFrame(Stream stream)
    {
        var header = new byte[6];
        var offset = 0;
        while (offset < header.Length)
        {
            var read = stream.Read(header, offset, header.Length - offset);
            if (read <= 0)
            {
                throw new EndOfStreamException("The pairing loopback closed early.");
            }
            offset += read;
        }
        Assert.Equal(1, header[0]);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header[2..]);
        var payload = new byte[length];
        offset = 0;
        while (offset < payload.Length)
        {
            var read = stream.Read(payload, offset, payload.Length - offset);
            if (read <= 0)
            {
                throw new EndOfStreamException("The pairing loopback closed early.");
            }
            offset += read;
        }
        return (header[1], payload);
    }
}