using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Apple;
using UotanStudio.Comm.Protocols.Apple.Lockdown;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Lockdown client exchanges over a scripted in-memory stream plus pairing-record store behavior.
/// <para>基于脚本化内存流的 lockdown 客户端交换，以及配对记录存储行为。</para>
/// </summary>
public sealed class LockdownClientTests
{
    [Fact(Timeout = 15000)]
    public void QueryType_ExchangesFramedPlistAndReturnsType()
    {
        var exchange = new ScriptedLockdownStream();
        exchange.EnqueueResponse(new Dictionary<string, object>
        {
            ["Type"] = "com.apple.mobile.lockdown",
            ["Status"] = 0L,
        });
        using var client = new LockdownClient(exchange, (_, _) => throw new IOException("no services in this test"));

        var type = client.QueryType(TestContext.Current.CancellationToken);

        Assert.Equal("com.apple.mobile.lockdown", type);
        Assert.Contains("QueryType", exchange.SentText, StringComparison.Ordinal);
        Assert.True(exchange.SentBytes.Length >= 4);
        var length = BinaryPrimitives.ReadUInt32BigEndian(exchange.SentBytes);
        Assert.Equal((uint)(exchange.SentBytes.Length - 4), length);
    }

    [Fact(Timeout = 15000)]
    public void GetAllValues_ReturnsValueDictionary()
    {
        var exchange = new ScriptedLockdownStream();
        exchange.EnqueueResponse(new Dictionary<string, object>
        {
            ["Value"] = new Dictionary<string, object>
            {
                ["DeviceName"] = "iPhone",
                ["ProductVersion"] = "26.1",
            },
            ["Status"] = 0L,
        });
        using var client = new LockdownClient(exchange, (_, _) => throw new IOException("no services in this test"));

        var values = client.GetAllValues(TestContext.Current.CancellationToken);

        Assert.Equal("iPhone", values["DeviceName"]);
        Assert.Equal("26.1", values["ProductVersion"]);
    }

    [Fact(Timeout = 15000)]
    public void StartService_WithoutSsl_ReturnsConnectorStreamForReportedPort()
    {
        var exchange = new ScriptedLockdownStream();
        exchange.EnqueueResponse(new Dictionary<string, object>
        {
            ["Status"] = 0L,
            ["Port"] = 4321L,
            ["EnableServiceSSL"] = false,
        });
        var serviceStream = new MemoryStream();
        using var client = new LockdownClient(exchange, (port, _) =>
        {
            Assert.Equal(4321, port);
            return serviceStream;
        });

        var opened = client.StartService(AppleConstants.ServiceAfc, TestContext.Current.CancellationToken);

        Assert.Same(serviceStream, opened);
        Assert.Contains(AppleConstants.ServiceAfc, exchange.SentText, StringComparison.Ordinal);
    }

    [Fact(Timeout = 15000)]
    public void Request_ErrorResponse_RaisesLockdownExceptionWithCode()
    {
        var exchange = new ScriptedLockdownStream();
        exchange.EnqueueResponse(new Dictionary<string, object>
        {
            ["Error"] = new Dictionary<string, object>
            {
                ["Code"] = "PairingDialogResponsePending",
                ["Description"] = "user has not yet responded to the pairing dialog.",
            },
        });
        using var client = new LockdownClient(exchange, (_, _) => throw new IOException("no services in this test"));

        var exception = Assert.Throws<LockdownException>(() =>
            client.QueryType(TestContext.Current.CancellationToken));

        Assert.Equal("PairingDialogResponsePending", exception.ErrorCode);
    }

    [Fact(Timeout = 15000)]
    public void PairingRecordStore_SaveLoadDelete_RoundTripsAndPersistsBuid()
    {
        _ = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var store = new LockdownPairingRecordStore(temp.Path);
        using var deviceKey = System.Security.Cryptography.RSA.Create(2048);
        var record = LockdownPairingRecord.CreatePending(
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
            Encoding.ASCII.GetBytes(deviceKey.ExportSubjectPublicKeyInfoPem()));

        store.Save("UDID123", record);

        var loaded = store.Load("UDID123");
        Assert.NotNull(loaded);
        Assert.Equal(record.HostId, loaded.HostId);
        Assert.Equal(record.HostCertificatePem, loaded.HostCertificatePem);
        Assert.True(loaded.RootPrivateKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal));
        store.Delete("UDID123");
        Assert.Null(store.Load("UDID123"));

        var buid = store.GetSystemBuid();
        Assert.Equal(32, buid.Length);
        Assert.Equal(buid, store.GetSystemBuid());
    }

    [Fact(Timeout = 15000)]
    public void PairingRecord_FromPlist_AcceptsDerDataFields()
    {
        _ = TestContext.Current.CancellationToken;
        using var rsa = System.Security.Cryptography.RSA.Create(1024);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=test", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        var der = certificate.RawData;
        var keyDer = rsa.ExportPkcs8PrivateKey();

        var record = LockdownPairingRecord.FromPlist(new Dictionary<string, object>
        {
            ["HostID"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            ["SystemBUID"] = "B",
            ["DeviceCertificate"] = der,
            ["HostCertificate"] = der,
            ["HostPrivateKey"] = keyDer,
            ["RootCertificate"] = der,
            ["RootPrivateKey"] = keyDer,
            ["EscrowBag"] = new byte[] { 1, 2, 3 },
        });

        Assert.Contains("BEGIN CERTIFICATE", record.HostCertificatePem, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 1, 2, 3 }, record.EscrowBag);
        Assert.NotNull(record.BuildTlsClientCertificate());
    }

    /// <summary>
    /// Duplex stream whose writes are recorded and whose reads replay pre-enqueued framed responses.
    /// <para>双工流：写入被记录，读取回放预置的组帧响应。</para>
    /// </summary>
    private sealed class ScriptedLockdownStream : Stream
    {
        private readonly MemoryStream _sent = new();
        private readonly Queue<byte[]> _responses = new();
        private byte[]? _current;
        private int _currentOffset;

        public byte[] SentBytes
        {
            get
            {
                Flush();
                return _sent.ToArray();
            }
        }

        public string SentText => Encoding.UTF8.GetString(SentBytes);

        public void EnqueueResponse(Dictionary<string, object> body)
        {
            var payload = ApplePlist.ToXml(body);
            var framed = new byte[4 + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)payload.Length);
            payload.CopyTo(framed, 4);
            _responses.Enqueue(framed);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _sent.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_current is null || _currentOffset >= _current.Length)
            {
                if (_responses.Count == 0)
                {
                    throw new IOException("The scripted lockdown stream has no response queued.");
                }

                _current = _responses.Dequeue();
                _currentOffset = 0;
            }

            var copied = Math.Min(count, _current.Length - _currentOffset);
            Array.Copy(_current, _currentOffset, buffer, offset, copied);
            _currentOffset += copied;
            return copied;
        }

        public override void Write(byte[] buffer, int offset, int count) => _sent.Write(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}