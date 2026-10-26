using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Tls;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Transports.Tcp;
using Xunit;
using CertificateRequest = System.Security.Cryptography.X509Certificates.CertificateRequest;

namespace UotanStudio.Tests.Devices;

public sealed class AdbTlsTransportTests
{
    [Fact]
    public async Task UpgradeToTls_DeviceRebuildsItsCertificateEachHandshake_CompletesBothHandshakes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var hostKey = RSA.Create(2048);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var first = await ExchangeAsync(listener, port, hostKey, cancellationToken);
        var second = await ExchangeAsync(listener, port, hostKey, cancellationToken);

        Assert.NotEqual(first.ServerCertificate, second.ServerCertificate);
        Assert.True(first.ClientCertificatePresent);
        Assert.True(second.ClientCertificatePresent);
    }

    [Theory]
    [InlineData(AlertDescription.bad_certificate)]
    [InlineData(AlertDescription.certificate_unknown)]
    [InlineData(AlertDescription.unknown_ca)]
    [InlineData(AlertDescription.access_denied)]
    public void IsClientKeyRejected_PeerRejectsClientCertificate_ReportsRejection(short alertDescription)
    {
        var failure = new IOException("handshake failed",
            new TlsFatalAlertReceived(alertDescription));

        Assert.True(AdbTlsTransport.IsClientKeyRejected(failure));
    }

    [Fact]
    public void IsClientKeyRejected_UnrelatedHandshakeFailure_ReportsNoRejection()
    {
        var failure = new IOException("handshake failed",
            new TlsFatalAlertReceived(AlertDescription.handshake_failure));

        Assert.False(AdbTlsTransport.IsClientKeyRejected(failure));
    }

    private static async Task<HandshakeObservation> ExchangeAsync(TcpListener listener, int port, RSA hostKey,
        CancellationToken cancellationToken)
    {
        using var clientCertificate = AdbHostCertificate.Create(hostKey);
        using var transport = new AdbTlsTransport(IPAddress.Loopback.ToString(), port, clientCertificate, 15000);
        var accepted = AcceptServerAsync(listener, cancellationToken);
        await Task.WhenAll(Task.Run(transport.UpgradeToTls, cancellationToken), accepted);

        Assert.True(transport.IsEncrypted);
        Assert.True(transport.IsConnected);

        using var session = await accepted;
        Assert.True(session.Authenticated);
        return session.Observation;
    }

    private static async Task<ServerSession> AcceptServerAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var serverKey = RSA.Create(2048);
        using var serverCertificate = CreateServerCertificate(serverKey);
        var clientCertificatePresent = false;
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
            (_, certificate, _, _) =>
            {
                clientCertificatePresent = certificate is not null;
                return true;
            });
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = serverCertificate,
            ClientCertificateRequired = true,
        }, cancellationToken).ConfigureAwait(false);
        return new ServerSession(client, ssl,
            new HandshakeObservation(Convert.ToHexString(SHA256.HashData(serverCertificate.RawData)),
                clientCertificatePresent));
    }

    private static X509Certificate2 CreateServerCertificate(RSA key)
    {
        var request = new CertificateRequest("CN=AdbDevice", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var notBefore = DateTimeOffset.UtcNow;
        using var ephemeral = request.CreateSelfSigned(notBefore, notBefore.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable);
    }

    private sealed record HandshakeObservation(string ServerCertificate, bool ClientCertificatePresent);

    private sealed class ServerSession(TcpClient client, SslStream ssl, HandshakeObservation observation) : IDisposable
    {
        public bool Authenticated => ssl.IsAuthenticated;

        public HandshakeObservation Observation => observation;

        public void Dispose()
        {
            ssl.Dispose();
            client.Dispose();
        }
    }
}