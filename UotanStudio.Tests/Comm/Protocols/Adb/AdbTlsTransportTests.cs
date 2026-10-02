using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UotanStudio.Comm.Transports.Tcp;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Adb;

public sealed class AdbTlsTransportTests
{
    private const uint StlsCommand = 0x534C5453;
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
        var request = new CertificateRequest("CN=uotan-tls-transport-test", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
