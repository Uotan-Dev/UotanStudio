using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using UotanStudio.Comm.Transports.Tcp;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Adb;

public sealed class AdbTlsTransportTests
{
    private const uint StlsCommand = 0x534C5453;
    private const uint StlsVersion = 1;
    private const int HeaderSize = 24;

    [Fact]
    public async Task Negotiate_PeerAnsweringWithPlaintextHeader_AnnouncesUpgradeBeforeAnyAdbMessage()
    {
        var certificate = TestCertificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var announced = new byte[HeaderSize];
        var ct = TestContext.Current.CancellationToken;
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            await stream.ReadExactlyAsync(announced, ct);
            await stream.WriteAsync(PlaintextCnxnHeader(), ct);
            await Task.Delay(300, CancellationToken.None);
        }, ct);

        using var transport = new AdbTlsTransport("127.0.0.1", port, certificate, 5000);
        await peer;

        Assert.Equal(StlsCommand, BinaryPrimitives.ReadUInt32LittleEndian(announced));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(announced.AsSpan(4)));
        var buffer = new byte[HeaderSize];
        Assert.Equal(HeaderSize, transport.Read(buffer, 0, HeaderSize, 5000));
        Assert.Equal(PlaintextCnxnHeader(), buffer);
    }

    [Fact]
    public async Task Negotiate_PeerNeverAnswers_TimesOutNamingTheEndpoint()
    {
        var certificate = TestCertificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ct = TestContext.Current.CancellationToken;
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await client.GetStream().ReadExactlyAsync(new byte[HeaderSize], ct);
            await Task.Delay(1500, CancellationToken.None);
        }, ct);

        var failure = Assert.Throws<TimeoutException>(() =>
            new AdbTlsTransport("127.0.0.1", port, certificate, 400));
        Assert.Contains($"127.0.0.1:{port}", failure.Message, StringComparison.Ordinal);
        await peer;
    }

    [Fact]
    public async Task Negotiate_PeerClosingTheConnection_FailsWithoutWaitingForTheAdbHandshake()
    {
        var certificate = TestCertificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ct = TestContext.Current.CancellationToken;
        var peer = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync(ct);
            client.Dispose();
        }, ct);

        Assert.ThrowsAny<IOException>(() => new AdbTlsTransport("127.0.0.1", port, certificate, 5000));
        await peer;
    }

    [Fact]
    public async Task Negotiate_BouncyCastleTlsServer_CompletesHandshakePresentsClientCertificateAndEchoesData()
    {
        var certificate = TestCertificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ct = TestContext.Current.CancellationToken;
        var payload = "uotan-stls-echo"u8.ToArray();
        using var serverKey = RSA.Create(2048);
        var serverCertificate = SelfSigned(serverKey, "CN=uotan-stls-loopback-server");
        var peer = Task.Run(
            () => RunBouncyCastleEchoServerAsync(listener, serverCertificate.RawDataMemory.ToArray(),
                DotNetUtilities.GetKeyPair(serverKey).Private, payload.Length, ct),
            ct);

        using var transport = new AdbTlsTransport("127.0.0.1", port, certificate, 5000);
        var written = await transport.WriteAsync(payload, 0, payload.Length, 5000, ct);
        var echoed = new byte[payload.Length];
        var read = await transport.ReadAsync(echoed, 0, echoed.Length, 5000, ct);
        var presented = await peer;

        Assert.Equal(payload.Length, written);
        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, echoed);
        Assert.Equal(certificate.RawDataMemory.ToArray(), presented);
    }

    [Fact]
    public async Task Negotiate_PeerClosingDuringTheTlsHandshake_FailsNamingTheEndpoint()
    {
        var certificate = TestCertificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ct = TestContext.Current.CancellationToken;
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            await stream.ReadExactlyAsync(new byte[HeaderSize], ct);
            await stream.WriteAsync(StlsHeader(), ct);
            await stream.FlushAsync(ct);
        }, ct);

        var failure = Assert.ThrowsAny<IOException>(() =>
            new AdbTlsTransport("127.0.0.1", port, certificate, 5000));
        Assert.Contains($"127.0.0.1:{port}", failure.Message, StringComparison.Ordinal);
        await peer;
    }

    [Fact]
    public void RequiresManagedTls_EphemeralPrivateKeyCertificate_IsTrueOnlyOnWindows()
    {
        using var certificate = TestCertificate();

        Assert.Equal(OperatingSystem.IsWindows(), AdbTlsTransport.RequiresManagedTls(certificate));
    }

    [Fact]
    public void RequiresManagedTls_NonEphemeralPrivateKeyCertificate_IsFalseOnEveryPlatform()
    {
        using var certificate = PersistedCertificate();
        try
        {
            Assert.False(AdbTlsTransport.RequiresManagedTls(certificate));
        }
        finally
        {
            DeletePersistedKey(certificate);
        }
    }

    private static async Task<byte[]?> RunBouncyCastleEchoServerAsync(TcpListener listener, byte[] serverCertificateDer,
        AsymmetricKeyParameter serverPrivateKey, int payloadLength, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();
        var announced = new byte[HeaderSize];
        await stream.ReadExactlyAsync(announced, cancellationToken);
        Assert.Equal(StlsCommand, BinaryPrimitives.ReadUInt32LittleEndian(announced));
        await stream.WriteAsync(StlsHeader(), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var protocol = new TlsServerProtocol(stream);
        var server = new LoopbackTlsServer(new BcTlsCrypto(new SecureRandom()), serverCertificateDer, serverPrivateKey);
        protocol.Accept(server);
        var echo = new byte[payloadLength];
        ReadExactly(protocol.Stream, echo);
        protocol.Stream.Write(echo, 0, echo.Length);
        protocol.Stream.Flush();
        protocol.Close();
        return server.ClientCertificateDer;
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("The TLS peer closed before sending the expected payload.");
            }

            offset += read;
        }
    }

    private static byte[] StlsHeader()
    {
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, StlsCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), StlsVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), StlsCommand ^ 0xFFFFFFFF);
        return header;
    }

    private static byte[] PlaintextCnxnHeader()
    {
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x01000000);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0x01000001);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0x00100000);
        return header;
    }

    private static X509Certificate2 TestCertificate()
    {
        using var rsa = RSA.Create(2048);
        return SelfSigned(rsa, "CN=uotan-tls-transport-test");
    }

    private static X509Certificate2 PersistedCertificate()
    {
        using var rsa = RSA.Create(2048);
        using var ephemeral = SelfSigned(rsa, "CN=uotan-tls-transport-persisted");
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
    }

    private static X509Certificate2 SelfSigned(RSA key, string subject)
    {
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(subject, key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static void DeletePersistedKey(X509Certificate2 certificate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = certificate.GetRSAPrivateKey();
        if (key is RSACng cng)
        {
            cng.Key.Delete();
        }
    }

    private sealed class LoopbackTlsServer(BcTlsCrypto crypto, byte[] certificateDer,
        AsymmetricKeyParameter privateKey) : DefaultTlsServer(crypto)
    {
        internal byte[]? ClientCertificateDer { get; private set; }

        public override ProtocolVersion[] GetProtocolVersions() => [ProtocolVersion.TLSv13];

        public override TlsCredentials GetCredentials() => new BcDefaultTlsCredentialedSigner(
            new TlsCryptoParameters(m_context),
            crypto,
            privateKey,
            new Certificate(Array.Empty<byte>(), [new CertificateEntry(crypto.CreateCertificate(certificateDer), null)]),
            SignatureAndHashAlgorithm.rsa_pss_rsae_sha256);

        public override Org.BouncyCastle.Tls.CertificateRequest GetCertificateRequest() => new(
            Array.Empty<byte>(),
            [SignatureAndHashAlgorithm.rsa_pss_rsae_sha256],
            null,
            null);

        public override void NotifyClientCertificate(Certificate clientCertificate)
        {
            ClientCertificateDer = clientCertificate is { Length: > 0 }
                ? clientCertificate.GetCertificateAt(0).GetEncoded()
                : null;
        }
    }
}