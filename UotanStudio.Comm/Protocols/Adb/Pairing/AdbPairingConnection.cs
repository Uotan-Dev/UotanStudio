using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace UotanStudio.Comm.Protocols.Adb.Pairing;

/// <summary>
/// Client for the ADB wireless pairing protocol: mutual self-signed TLS 1.3, then SPAKE2
/// and an AES-128-GCM protected PeerInfo exchange inside the TLS channel.
/// <para>ADB 无线配对协议客户端：先完成双向自签证书的 TLS 1.3，随后在 TLS 通道内执行
/// SPAKE2 与 AES-128-GCM 保护的 PeerInfo 交换。</para>
/// </summary>
internal static class AdbPairingConnection
{
    private const byte FrameTypeSpake2Message = 0;
    private const byte FrameTypePeerInfo = 1;
    private const byte PeerInfoTypeAdbRsaPublicKey = 0;
    private const byte PeerInfoTypeAdbDeviceGuid = 1;
    private const int PeerInfoSize = 8192;
    private const int MaxFramePayloadSize = PeerInfoSize * 2;
    private const int ExportedKeySize = 64;
    private const int HeaderVersion = 1;
    private const int ConnectTimeoutMs = 5000;
    private const int SocketTimeoutMs = 8000;

    /// <summary>Pairs with the device's pairing service. The device must currently be showing the pairing dialog (pairing code or QR code) so the one-time password is valid.
    /// <para>与设备配对服务完成配对。设备当前必须停在配对对话框（配对码或二维码）， 一次性密码才有效。</para></summary>
    /// <param name="host">The device IP address.<para>设备 IP 地址。</para></param>
    /// <param name="port">The pairing service TCP port.<para>配对服务 TCP 端口。</para></param>
    /// <param name="password">The one-time pairing password.<para>一次性配对密码。</para></param>
    /// <param name="cancellationToken">Cancels the connection and protocol exchange.
    /// <para>取消连接与协议交互。</para></param>
    /// <returns>The device GUID reported in the pairing exchange.
    /// <para>配对交互中设备报告的 GUID。</para></returns>
    /// <exception cref="ArgumentException">Arguments are blank or out of range.
    /// <para>参数为空或超出范围。</para></exception>
    /// <exception cref="IOException">The TLS handshake, SPAKE2 exchange or PeerInfo decryption failed (a wrong or expired pairing password fails here).
    /// <para>TLS 握手、SPAKE2 交互或 PeerInfo 解密失败（密码错误或过期在此失败）。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    public static async Task<string> PairAsync(string host, int port, string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        return await Task.Run(() => PairCore(host, port, password, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string PairCore(string host, int port, string password, CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };
        using var cancellation = cancellationToken.Register(client.Dispose);
        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeoutMs);
            client.ConnectAsync(host, port, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException($"Unable to connect to the pairing service at {host}:{port}.", ex);
        }
        client.ReceiveTimeout = SocketTimeoutMs;
        client.SendTimeout = SocketTimeoutMs;

        try
        {
            using var stream = client.GetStream();
            return RunPairingProtocol(stream, password);
        }
        catch (Exception ex) when (ex is IOException or SocketException && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Pairing was canceled.", ex, cancellationToken);
        }
        catch (CryptographicException ex)
        {
            throw new IOException("Pairing failed: the pairing password is wrong or has expired.", ex);
        }
    }

    private static string RunPairingProtocol(Stream stream, string password)
    {
        using var authentication = AdbAuthentication.LoadOrCreate();
        var crypto = new BcTlsCrypto(new SecureRandom());
        using var certificate = AdbHostCertificate.Create(authentication.RsaKey);
        var certificateDer = certificate.RawDataMemory.ToArray();
        var privateKey = DotNetUtilities.GetKeyPair(authentication.RsaKey).Private;

        var protocol = new TlsClientProtocol(stream);
        var tlsClient = new AdbPairingTlsClient(crypto, certificateDer, privateKey);
        try
        {
            protocol.Connect(tlsClient);

            var exported = tlsClient.ExportedKeyMaterial;
            if (exported is not { Length: ExportedKeySize })
            {
                throw new IOException("The TLS stack did not export the expected pairing key material.");
            }

            var passwordBytes = System.Text.Encoding.ASCII.GetBytes(password);
            var spakePassword = new byte[passwordBytes.Length + exported.Length];
            passwordBytes.CopyTo(spakePassword, 0);
            exported.CopyTo(spakePassword, passwordBytes.Length);

            var spake = AdbSpake2.CreateClient();
            var spakeMessage = spake.GenerateMessage(spakePassword);
            WriteFrame(protocol.Stream, FrameTypeSpake2Message, spakeMessage);

            var (peerType, peerPayload) = ReadFrame(protocol.Stream);
            if (peerType != FrameTypeSpake2Message || peerPayload.Length != 32)
            {
                throw new IOException("The device sent an unexpected pairing handshake message.");
            }
            var keyMaterial = spake.ProcessMessage(peerPayload);

            var aesKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, keyMaterial, 16,
                salt: [], info: "adb pairing_auth aes-128-gcm key"u8.ToArray());
            using var aes = new AesGcm(aesKey, 16);
            ulong sendSequence = 0;
            ulong receiveSequence = 0;

            var publicKeyLine = AdbAuthentication.BuildPublicKeyPayload(authentication.RsaKey);
            if (publicKeyLine.Length > PeerInfoSize - 2)
            {
                throw new IOException("The ADB public key line exceeds the pairing PeerInfo capacity.");
            }
            var peerInfo = new byte[PeerInfoSize];
            peerInfo[0] = PeerInfoTypeAdbRsaPublicKey;
            publicKeyLine.CopyTo(peerInfo, 1);
            WriteFrame(protocol.Stream, FrameTypePeerInfo, Encrypt(aes, peerInfo, sendSequence++));

            var (responseType, responsePayload) = ReadFrame(protocol.Stream);
            if (responseType != FrameTypePeerInfo)
            {
                throw new IOException("The device sent an unexpected pairing confirmation message.");
            }
            var decrypted = Decrypt(aes, responsePayload, receiveSequence++);
            if (decrypted.Length != PeerInfoSize || decrypted[0] != PeerInfoTypeAdbDeviceGuid)
            {
                throw new IOException("The device returned an unexpected pairing response.");
            }

            return ExtractGuid(decrypted);
        }
        finally
        {
            protocol.Close();
        }
    }

    private static byte[] Encrypt(AesGcm aes, byte[] plaintext, ulong sequence)
    {
        var ciphertext = new byte[plaintext.Length + 16];
        aes.Encrypt(CreateNonce(sequence), plaintext, ciphertext.AsSpan(..^16), ciphertext.AsSpan(^16..));
        return ciphertext;
    }

    private static byte[] Decrypt(AesGcm aes, byte[] ciphertext, ulong sequence)
    {
        if (ciphertext.Length is < 16 or > MaxFramePayloadSize)
        {
            throw new IOException("The pairing PeerInfo payload has an invalid length.");
        }
        var plaintext = new byte[ciphertext.Length - 16];
        aes.Decrypt(CreateNonce(sequence), ciphertext.AsSpan(..^16), ciphertext.AsSpan(^16..), plaintext);
        return plaintext;
    }

    private static byte[] CreateNonce(ulong sequence)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, sequence);
        return nonce;
    }

    private static void WriteFrame(Stream stream, byte type, byte[] payload)
    {
        if (payload is not { Length: > 0 and <= MaxFramePayloadSize })
        {
            throw new IOException("The pairing frame payload has an invalid length.");
        }
        var header = new byte[6];
        header[0] = HeaderVersion;
        header[1] = type;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(2), (uint)payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    private static (byte Type, byte[] Payload) ReadFrame(Stream stream)
    {
        var header = new byte[6];
        stream.ReadExactly(header);
        if (header[0] != HeaderVersion)
        {
            throw new IOException("The device sent an unsupported pairing header version.");
        }
        var length = BinaryPrimitives.ReadUInt32BigEndian(header[2..]);
        if (length is 0 or > MaxFramePayloadSize)
        {
            throw new IOException("The pairing frame payload has an invalid length.");
        }
        var payload = new byte[length];
        stream.ReadExactly(payload);
        return (header[1], payload);
    }

    private static string ExtractGuid(byte[] peerInfo)
    {
        var end = 1;
        while (end < peerInfo.Length && peerInfo[end] != 0)
        {
            end++;
        }
        return System.Text.Encoding.ASCII.GetString(peerInfo, 1, end - 1);
    }
}

/// <summary>
/// TLS 1.3 client for the pairing channel: presents the ADB certificate and accepts the
/// device's self-signed certificate, since the SPAKE2 password is the trust anchor.
/// <para>配对通道的 TLS 1.3 客户端：出示 ADB 证书并接受设备自签证书，因为 SPAKE2
/// 密码即信任锚。</para>
/// </summary>
/// <param name="crypto">The TLS crypto provider. <para>TLS 密码学提供方。</para></param>
/// <param name="certificateDer">The DER-encoded client certificate. <para>DER 编码的客户端证书。</para></param>
/// <param name="privateKey">The private key paired with that certificate.
/// <para>与该证书配对的私钥。</para></param>
internal sealed class AdbPairingTlsClient(BcTlsCrypto crypto, byte[] certificateDer, AsymmetricKeyParameter privateKey)
    : DefaultTlsClient(crypto)
{
    private const int ExportedKeySize = 64;

    /// <summary>
    /// Gets the channel-bound keying material snapshotted during handshake completion,
    /// because a TLS 1.3 export is only permitted from inside that callback.
    /// <para>获取在握手完成回调期间快照保存的通道绑定密钥材料，因为只有该回调内才允许
    /// TLS 1.3 导出。</para>
    /// </summary>
    /// <value>The 64-byte exporter output, or <c>null</c> while the handshake is unfinished.
    /// <para>64 字节的 exporter 输出，握手未完成时为 <c>null</c>。</para></value>
    internal byte[]? ExportedKeyMaterial { get; private set; }

    /// <summary>
    /// Exports and stores the channel-bound keying material, then finishes the base
    /// handshake-completion work.
    /// <para>导出并保存通道绑定密钥材料，随后完成基类的握手收尾处理。</para>
    /// </summary>
    public override void NotifyHandshakeComplete()
    {
        ExportedKeyMaterial = m_context.ExportKeyingMaterial("adb-label\0", null, ExportedKeySize);
        base.NotifyHandshakeComplete();
    }

    /// <summary>
    /// Supplies the pairing channel's certificate authentication.
    /// <para>提供配对通道的证书认证。</para>
    /// </summary>
    /// <returns>The authentication used for this handshake. <para>本次握手使用的认证对象。</para></returns>
    public override TlsAuthentication GetAuthentication() =>
        new AdbPairingTlsAuthentication(new TlsCryptoParameters(m_context), crypto, certificateDer, privateKey);

    /// <summary>
    /// Restricts protocol negotiation to TLS 1.3.
    /// <para>把协议协商限制为 TLS 1.3。</para>
    /// </summary>
    /// <returns>The array holding the one allowed protocol version. <para>仅含唯一允许协议版本的数组。</para></returns>
    public override ProtocolVersion[] GetProtocolVersions() => [ProtocolVersion.TLSv13];

    private sealed class AdbPairingTlsAuthentication(TlsCryptoParameters cryptoParameters, BcTlsCrypto crypto,
        byte[] certificateDer, AsymmetricKeyParameter privateKey) : TlsAuthentication
    {
        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
        }

        public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
        {
            var chain = new Certificate([],
                [new CertificateEntry(crypto.CreateCertificate(certificateDer), null)]);
            return new BcDefaultTlsCredentialedSigner(cryptoParameters, crypto, privateKey, chain,
                SignatureAndHashAlgorithm.rsa_pss_rsae_sha256);
        }
    }
}