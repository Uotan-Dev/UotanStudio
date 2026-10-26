using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Comm.Protocols.Apple.Tunnel;

/// <summary>
/// One RemotePairing pair record: the host Ed25519 private key plus the peer identifier it was
/// issued for. Persisted locally so pair-verify succeeds without a new trust dialog.
/// <para>一份 RemotePairing 配对记录：主机 Ed25519 私钥与签发的对端标识。本地持久化，
/// 让 pair-verify 无需再次弹信任框。</para>
/// </summary>
public sealed class RemotePairingRecord
{
    /// <summary>Gets or sets the identifier the host registered under. <para>获取或设置主机注册的标识。</para></summary>
    public required string HostIdentifier { get; set; }

    /// <summary>Gets or sets the peer device identifier. <para>获取或设置对端设备标识。</para></summary>
    public required string PeerIdentifier { get; set; }

    /// <summary>Gets or sets the Ed25519 private key seed (32 bytes). <para>获取或设置 Ed25519 私钥种子（32 字节）。</para></summary>
    public required byte[] PrivateKeySeed { get; set; }
}

/// <summary>
/// Stores RemotePairing pair records next to the lockdown pair records.
/// <para>把 RemotePairing 配对记录保存在 lockdown 配对记录旁。</para>
/// </summary>
public sealed class RemotePairingRecordStore
{
    private readonly string _directory;

    /// <summary>
    /// Creates a store; the default root matches <see cref="Lockdown.LockdownPairingRecordStore"/>.
    /// <para>创建存储；默认根目录与 <see cref="Lockdown.LockdownPairingRecordStore"/> 一致。</para>
    /// </summary>
    /// <param name="directory">The storage directory, or <see langword="null"/> for the default. <para>存储目录；<see langword="null"/> 用默认。</para></param>
    public RemotePairingRecordStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UotanStudio", "apple-lockdown");
        Directory.CreateDirectory(_directory);
    }

    /// <summary>
    /// Loads the record saved for a peer identifier.
    /// <para>读取为某对端标识保存的记录。</para>
    /// </summary>
    /// <param name="peerIdentifier">The peer device identifier. <para>对端设备标识。</para></param>
    /// <returns>The record, or <see langword="null"/> when absent. <para>记录；缺失时为 <see langword="null"/>。</para></returns>
    public RemotePairingRecord? Load(string peerIdentifier)
    {
        var path = Path.Combine(_directory, $"remote-pairing-{Safe(peerIdentifier)}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (RemotePairingJson.Decode(File.ReadAllBytes(path)) is not IReadOnlyDictionary<string, object?> root)
            {
                return null;
            }

            return new RemotePairingRecord
            {
                HostIdentifier = root.TryGetValue("hostIdentifier", out var host) && host is string hostText ? hostText : string.Empty,
                PeerIdentifier = peerIdentifier,
                PrivateKeySeed = root.TryGetValue("privateKeySeed", out var seed) && seed is byte[] seedBytes
                    ? seedBytes
                    : [],
            };
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves the record for a peer identifier.
    /// <para>保存某对端标识的记录。</para>
    /// </summary>
    /// <param name="record">The record. <para>记录。</para></param>
    public void Save(RemotePairingRecord record)
    {
        var path = Path.Combine(_directory, $"remote-pairing-{Safe(record.PeerIdentifier)}.json");
        var json = Encoding.UTF8.GetString(RemotePairingJson.Encode(new Dictionary<string, object?>
        {
            ["hostIdentifier"] = record.HostIdentifier,
            ["peerIdentifier"] = record.PeerIdentifier,
            ["privateKeySeed"] = record.PrivateKeySeed,
        }));
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    private static string Safe(string identifier)
    {
        var builder = new StringBuilder(identifier.Length);
        foreach (var character in identifier)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

}

/// <summary>
/// The RemotePairing protocol over RPPairing-framed JSON (the RemotePairingTunnelService framing):
/// handshake, pair-verify with the stored record and encrypted requests. Pair-setup (the trust
/// dialog flow) is supported with the pin parameter.
/// <para>经 RPPairing 组帧 JSON 的 RemotePairing 协议（RemotePairingTunnelService 组帧）：握手、
/// 用既有记录做 pair-verify 与加密请求。pair-setup（信任框流程）经 pin 参数支持。</para>
/// </summary>
public sealed class RemotePairingClient : IDisposable
{
    private const int WireProtocolVersion = 19;
    private const string HandshakeMagic = "RPPairing";

    private readonly Stream _stream;
    private readonly RemotePairingRecordStore _store;
    private readonly Lock _sequenceLock = new();
    private ulong _sequenceNumber;
    private ulong _encryptedSequenceNumber;
    private byte[]? _clientKey;
    private byte[]? _serverKey;
    private byte[]? _ed25519Seed;
    private string _identifier;
    private IReadOnlyDictionary<string, object?>? _handshakeInfo;

    /// <summary>
    /// Creates the client over a connected service stream.
    /// <para>在已连接的服务流上创建客户端。</para>
    /// </summary>
    /// <param name="stream">The connected stream. <para>已连接流。</para></param>
    /// <param name="store">The pair record store. <para>配对记录存储。</para></param>
    public RemotePairingClient(Stream stream, RemotePairingRecordStore store)
    {
        _stream = stream;
        _store = store;
        _identifier = Guid.NewGuid().ToString("N").ToUpperInvariant();
        _ed25519Seed = RandomNumberGenerator.GetBytes(32);
    }

    /// <summary>Gets the handshake info reported by the peer. <para>获取对端上报的握手信息。</para></summary>
    public IReadOnlyDictionary<string, object?>? HandshakeInfo => _handshakeInfo;

    /// <summary>
    /// Runs the handshake and pair-verify; when verification fails and <paramref name="autopair"/> is
    /// set, the trust-dialog pairing is attempted and <see cref="RemotePairingCompletedException"/> is
    /// thrown so the caller can reconnect (the device drops the connection after pairing).
    /// <para>执行握手与 pair-verify；校验失败且 <paramref name="autopair"/> 为真时尝试信任框配对，
    /// 并抛出 <see cref="RemotePairingCompletedException"/> 让调用方重连（配对完成后设备会断开连接）。</para>
    /// </summary>
    /// <param name="autopair">Whether to attempt automatic pairing. <para>是否尝试自动配对。</para></param>
    /// <param name="pin">The pairing PIN for tvOS/visionOS style devices. <para>tvOS/visionOS 类设备的配对 PIN。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交换。</para></param>
    /// <exception cref="IOException">The handshake failed. <para>握手失败时抛出。</para></exception>
    public void Connect(bool autopair = true, string? pin = null, CancellationToken cancellationToken = default)
    {
        _handshakeInfo = SendReceivePlain(new Dictionary<string, object?>
        {
            ["request"] = new Dictionary<string, object?>
            {
                ["_0"] = new Dictionary<string, object?>
                {
                    ["handshake"] = new Dictionary<string, object?>
                    {
                        ["_0"] = new Dictionary<string, object?>
                        {
                            ["hostOptions"] = new Dictionary<string, object?> { ["attemptPairVerify"] = true },
                            ["wireProtocolVersion"] = XpcCodec.UInt64(WireProtocolVersion),
                        },
                    },
                },
            },
        }, cancellationToken);
        var handshakeResponse = _handshakeInfo;
        if (handshakeResponse is not null &&
            handshakeResponse.TryGetValue("response", out var response) &&
            response is IReadOnlyDictionary<string, object?> responseDict &&
            responseDict.TryGetValue("_1", out var inner) &&
            inner is IReadOnlyDictionary<string, object?> innerDict &&
            innerDict.TryGetValue("handshake", out var handshake) &&
            handshake is IReadOnlyDictionary<string, object?> handshakeDict &&
            handshakeDict.TryGetValue("_0", out var info) &&
            info is IReadOnlyDictionary<string, object?> infoDict)
        {
            _handshakeInfo = infoDict;
        }

        if (TryPairVerify(cancellationToken))
        {
            DeriveMainKeys();
            return;
        }

        if (!autopair)
        {
            throw new IOException("The RemotePairing pair-verify failed and autopairing is disabled.");
        }

        PairSetup(pin, cancellationToken);
        throw new RemotePairingCompletedException();
    }

    /// <summary>
    /// Sends one encrypted request and returns the decrypted response payload.
    /// <para>发送一条加密请求并返回解密后的响应负载。</para>
    /// </summary>
    /// <param name="request">The request dictionary. <para>请求字典。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交换。</para></param>
    /// <returns>The decrypted response. <para>解密后的响应。</para></returns>
    public IReadOnlyDictionary<string, object?> SendEncryptedRequest(IReadOnlyDictionary<string, object?> request,
        CancellationToken cancellationToken = default)
    {
        if (_clientKey is null || _serverKey is null)
        {
            throw new InvalidOperationException("The RemotePairing session keys are not derived yet.");
        }

        ulong encryptedSequence;
        ulong sequence;
        lock (_sequenceLock)
        {
            encryptedSequence = _encryptedSequenceNumber++;
            sequence = _sequenceNumber;
        }

        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, encryptedSequence);
        var encrypted = AeadCipher.Encrypt(_clientKey, nonce, RemotePairingJson.Encode(request));

        var response = SendReceivePlain(new Dictionary<string, object?>
        {
            ["message"] = new Dictionary<string, object?>
            {
                ["streamEncrypted"] = new Dictionary<string, object?> { ["_0"] = encrypted },
            },
            ["originatedBy"] = "host",
            ["sequenceNumber"] = XpcCodec.UInt64(sequence),
        }, cancellationToken);
        lock (_sequenceLock)
        {
            _sequenceNumber++;
        }

        if (response.TryGetValue("message", out var message) &&
            message is IReadOnlyDictionary<string, object?> messageDict &&
            messageDict.TryGetValue("streamEncrypted", out var streamEncrypted) &&
            streamEncrypted is IReadOnlyDictionary<string, object?> streamEncryptedDict &&
            streamEncryptedDict.TryGetValue("_0", out var payload) &&
            payload is byte[] encryptedResponse)
        {
            var plaintext = AeadCipher.Decrypt(_serverKey, nonce, encryptedResponse);
            if (RemotePairingJson.Decode(plaintext) is IReadOnlyDictionary<string, object?> decoded &&
                decoded.TryGetValue("response", out var responseValue) &&
                responseValue is IReadOnlyDictionary<string, object?> responseDict &&
                responseDict.TryGetValue("_1", out var inner) &&
                inner is IReadOnlyDictionary<string, object?> innerDict)
            {
                return innerDict;
            }

            throw new IOException("The RemotePairing encrypted response has an unexpected shape.");
        }

        throw new IOException("The RemotePairing encrypted response has an unexpected shape.");
    }

    /// <inheritdoc/>
    public void Dispose() => _stream.Dispose();

    private bool TryPairVerify(CancellationToken cancellationToken)
    {
        var record = ReadPeerRecord(cancellationToken);
        var x25519 = new X25519PrivateKey(null);
        return PairVerify(x25519, record, cancellationToken);
    }

    private bool PairVerify(X25519PrivateKey x25519, RemotePairingRecord? record, CancellationToken cancellationToken)
    {
        var publicKey = x25519.ExportPublicKey();
        var tlv = Tlv8.Encode((Tlv8.TypeState, (byte[])[0x01]), (Tlv8.TypePublicKey, publicKey));
        var response = SendReceivePairingData("verifyManualPairing", tlv, startNewSession: true, cancellationToken);
        var items = Tlv8.Decode(response);
        if (items.ContainsKey(Tlv8.TypeError))
        {
            return false;
        }

        var peerPublic = items[Tlv8.TypePublicKey];
        var shared = x25519.DeriveSharedSecret(peerPublic);
        var verifyKey = HkdfSha512(shared, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");

        byte[] signature;
        if (record is not null)
        {
            _identifier = record.HostIdentifier;
            var signer = new Ed25519Signer(record.PrivateKeySeed);
            signature = signer.Sign(BuildVerifySignature(publicKey, peerPublic));
        }
        else
        {
            var signer = new Ed25519Signer(new byte[32]);
            signature = signer.Sign(BuildVerifySignature(publicKey, peerPublic));
        }

        var encrypted = AeadCipher.Encrypt(verifyKey, BuildAadNonce("PV-Msg03"),
            Tlv8.Encode((Tlv8.TypeIdentifier, Encoding.UTF8.GetBytes(_identifier)), (Tlv8.TypeSignature, signature)));
        var verifyTlv = Tlv8.Encode((Tlv8.TypeState, (byte[])[0x03]), (Tlv8.TypeEncryptedData, encrypted));
        var verifyResponse = SendReceivePairingData("verifyManualPairing", verifyTlv, startNewSession: false,
            cancellationToken);
        var verifyItems = Tlv8.Decode(verifyResponse);
        return !verifyItems.ContainsKey(Tlv8.TypeError);
    }

    private byte[] BuildVerifySignature(byte[] hostPublic, byte[] peerPublic) =>
        hostPublic.Concat(Encoding.UTF8.GetBytes(_identifier)).Concat(peerPublic).ToArray();

    private void DeriveMainKeys()
    {
        if (_ed25519Seed is null)
        {
            throw new InvalidOperationException("The RemotePairing session was not initialized.");
        }

        if (_sharedSecret is null)
        {
            throw new InvalidOperationException("The RemotePairing shared secret is missing.");
        }

        _clientKey = HkdfSha512(_sharedSecret, null, "ClientEncrypt-main");
        _serverKey = HkdfSha512(_sharedSecret, null, "ServerEncrypt-main");
    }

    private byte[]? _sharedSecret;

    private void PairSetup(string? pin, CancellationToken cancellationToken)
    {
        var setupTlv = Tlv8.Encode((Tlv8.TypeMethod, (byte[])[0x00]), (Tlv8.TypeState, (byte[])[0x01]));
        SendPairingData("setupManualPairing", setupTlv, startNewSession: true, cancellationToken);
        var consentResponse = ReceivePlain(cancellationToken);
        var consent = ExtractEventPayload(consentResponse);
        if (consent.TryGetValue("pairingRejectedWithError", out var rejection))
        {
            throw new IOException($"The device rejected the pairing: {Describe(rejection)}.");
        }

        byte[] pairingData;
        if (consent.TryGetValue("awaitingUserConsent", out _))
        {
            pairingData = ReceivePairingData(cancellationToken);
        }
        else if (consent.TryGetValue("pairingData", out var immediate))
        {
            pairingData = ExtractPairingDataBytes(immediate);
        }
        else
        {
            throw new IOException("The device answered the pairing request with an unexpected message.");
        }

        var consentItems = Tlv8.Decode(pairingData);
        if (consentItems.ContainsKey(Tlv8.TypeError))
        {
            throw new IOException("The device answered the pairing request with an error.");
        }

        var srp = new Srp3072Client();
        var serverPublic = consentItems[Tlv8.TypePublicKey];
        var salt = consentItems[Tlv8.TypeSalt];
        srp.ProcessChallenge("Pair-Setup", pin ?? "000000", salt, serverPublic);

        var clientPublic = srp.ComputeClientProof();
        var proof = srp.ComputeClientProof();
        var clientProof = proof;
        _ = clientPublic;
        var stateTlv = Tlv8.Encode((Tlv8.TypeState, (byte[])[0x03]), (Tlv8.TypePublicKey, clientPublic), (Tlv8.TypeProof, clientProof));
        var stateResponse = SendReceivePairingData("setupManualPairing", stateTlv, startNewSession: false,
            cancellationToken);
        var stateItems = Tlv8.Decode(stateResponse);
        if (!stateItems.TryGetValue(Tlv8.TypeProof, out var serverProof))
        {
            throw new IOException("The device did not return an SRP proof.");
        }

        var shared = srp.VerifyServerProof(serverProof);
        _sharedSecret = shared;

        var setupEncryptionKey = HkdfSha512(shared, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info");
        var ed25519 = new Ed25519Signer(_ed25519Seed!);
        var hostPublicEd25519 = ed25519.ExportPublicKey();
        var signBuffer = HkdfSha512(shared, "Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info")
            .Concat(Encoding.UTF8.GetBytes(_identifier))
            .Concat(hostPublicEd25519)
            .ToArray();
        var signature = ed25519.Sign(signBuffer);

        var deviceInfo = Opack.Encode(new Dictionary<string, object?>
        {
            ["altIRK"] = new byte[] { 0xE9, 0xE8, 0x2D, 0xC0, 0x6A, 0x49, 0x79, 0x6B, 0x56, 0x6F, 0x54, 0x00, 0x19, 0xB1, 0xC7, 0x7B },
            ["btAddr"] = "11:22:33:44:55:66",
            ["mac"] = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 },
            ["remotepairing_serial_number"] = "AAAAAAAAAAAA",
            ["accountID"] = _identifier,
            ["model"] = "computer-model",
            ["name"] = Environment.MachineName,
        });

        var identityTlv = Tlv8.Encode(
            (Tlv8.TypeIdentifier, Encoding.UTF8.GetBytes(_identifier)),
            (Tlv8.TypePublicKey, hostPublicEd25519),
            (Tlv8.TypeSignature, signature),
            (Tlv8.TypeInfo, deviceInfo));
        var encryptedData = AeadCipher.Encrypt(setupEncryptionKey, BuildPsNonce(5), identityTlv);
        var encryptedTlv = Tlv8.Encode(
            (Tlv8.TypeEncryptedData, encryptedData),
            (Tlv8.TypeState, (byte[])[0x05]));
        var encryptedResponse = SendReceivePairingData("setupManualPairing", encryptedTlv, startNewSession: false,
            cancellationToken);
        var encryptedItems = Tlv8.Decode(encryptedResponse);
        if (!encryptedItems.TryGetValue(Tlv8.TypeEncryptedData, out var deviceEncrypted))
        {
            throw new IOException("The device did not return the pairing final message.");
        }

        var deviceTlv = AeadCipher.Decrypt(setupEncryptionKey, BuildPsNonce(6), deviceEncrypted);
        _ = Tlv8.Decode(deviceTlv);

        var peerIdentifier = ReadPeerIdentifierFromHandshake()
            ?? throw new IOException("The RemotePairing handshake did not report a peer identifier.");
        _store.Save(new RemotePairingRecord
        {
            HostIdentifier = _identifier,
            PeerIdentifier = peerIdentifier,
            PrivateKeySeed = _ed25519Seed!,
        });
    }

    private static byte[] BuildPsNonce(int counter)
    {
        var nonce = new byte[12];
        nonce[4] = (byte)'P';
        nonce[5] = (byte)'S';
        nonce[6] = (byte)'-';
        nonce[7] = (byte)'M';
        nonce[8] = (byte)'s';
        nonce[9] = (byte)'g';
        nonce[10] = (byte)('0' + counter / 10);
        nonce[11] = (byte)('0' + counter % 10);
        return nonce;
    }

    private static byte[] BuildAadNonce(string tag)
    {
        var nonce = new byte[12];
        var tagBytes = System.Text.Encoding.ASCII.GetBytes(tag);
        tagBytes.CopyTo(nonce, 12 - tagBytes.Length);
        return nonce;
    }

    private byte[] SendReceivePairingData(string kind, byte[] tlv, bool startNewSession, CancellationToken cancellationToken)
    {
        SendPairingData(kind, tlv, startNewSession, cancellationToken);
        return ReceivePairingData(cancellationToken);
    }

    private void SendPairingData(string kind, byte[] tlv, bool startNewSession, CancellationToken cancellationToken)
    {
        SendPlain(new Dictionary<string, object?>
        {
            ["event"] = new Dictionary<string, object?>
            {
                ["_0"] = new Dictionary<string, object?>
                {
                    ["pairingData"] = new Dictionary<string, object?>
                    {
                        ["_0"] = new Dictionary<string, object?>
                        {
                            ["data"] = tlv,
                            ["kind"] = kind,
                            ["sendingHost"] = Environment.MachineName,
                            ["startNewSession"] = startNewSession,
                        },
                    },
                },
            },
        }, cancellationToken);
    }

    private byte[] ReceivePairingData(CancellationToken cancellationToken)
    {
        var response = ReceivePlain(cancellationToken);
        var payload = ExtractEventPayload(response);
        if (payload.TryGetValue("pairingData", out var pairingData))
        {
            return ExtractPairingDataBytes(pairingData);
        }

        if (payload.TryGetValue("pairingRejectedWithError", out var rejection))
        {
            throw new IOException($"The device rejected the pairing: {Describe(rejection)}.");
        }

        throw new IOException("The device sent an unexpected pairing message.");
    }

    private IReadOnlyDictionary<string, object?> SendReceivePlain(IReadOnlyDictionary<string, object?> request,
        CancellationToken cancellationToken)
    {
        SendPlain(request, cancellationToken);
        return ReceivePlain(cancellationToken);
    }

    private void SendPlain(IReadOnlyDictionary<string, object?> request, CancellationToken cancellationToken)
    {
        ulong sequence;
        lock (_sequenceLock)
        {
            sequence = _sequenceNumber++;
        }

        var body = RemotePairingJson.Encode(new Dictionary<string, object?>(request)
        {
            ["originatedBy"] = "host",
            ["sequenceNumber"] = sequence,
        });
        WritePacket(body, cancellationToken);
    }

    private IReadOnlyDictionary<string, object?> ReceivePlain(CancellationToken cancellationToken)
    {
        var body = ReadPacket(cancellationToken);
        return RemotePairingJson.Decode(body) as IReadOnlyDictionary<string, object?>
            ?? throw new IOException("The RemotePairing response is not a JSON object.");
    }

    private static IReadOnlyDictionary<string, object?> ExtractEventPayload(IReadOnlyDictionary<string, object?> response)
    {
        if (response.TryGetValue("event", out var @event) &&
            @event is IReadOnlyDictionary<string, object?> eventDict &&
            eventDict.TryGetValue("_0", out var payload) &&
            payload is IReadOnlyDictionary<string, object?> payloadDict)
        {
            return payloadDict;
        }

        throw new IOException("The RemotePairing response has an unexpected shape.");
    }

    private static byte[] ExtractPairingDataBytes(object? pairingData)
    {
        if (pairingData is IReadOnlyDictionary<string, object?> dict &&
            dict.TryGetValue("_0", out var inner) &&
            inner is IReadOnlyDictionary<string, object?> innerDict &&
            innerDict.TryGetValue("data", out var data) &&
            data is byte[] bytes)
        {
            return bytes;
        }

        throw new IOException("The RemotePairing pairingData has an unexpected shape.");
    }

    private static string Describe(object? value) => value is IReadOnlyDictionary<string, object?> dictionary
        && dictionary.TryGetValue("NSLocalizedDescription", out var description) && description is string text
            ? text
            : value?.ToString() ?? string.Empty;

    private RemotePairingRecord? ReadPeerRecord(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var peerIdentifier = ReadPeerIdentifierFromHandshake();
        return peerIdentifier is null ? null : _store.Load(peerIdentifier);
    }

    private string? ReadPeerIdentifierFromHandshake()
    {
        if (_handshakeInfo is not null &&
            _handshakeInfo.TryGetValue("peerDeviceInfo", out var deviceInfo) &&
            deviceInfo is IReadOnlyDictionary<string, object?> deviceDict &&
            deviceDict.TryGetValue("identifier", out var identifier) &&
            identifier is string identifierText)
        {
            return identifierText;
        }

        return null;
    }

    private void WritePacket(byte[] body, CancellationToken cancellationToken)
    {
        var packet = new byte[10 + body.Length];
        Encoding.ASCII.GetBytes(HandshakeMagic).CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8), (ushort)body.Length);
        body.CopyTo(packet, 10);
        _stream.WriteAsync(packet, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    private byte[] ReadPacket(CancellationToken cancellationToken)
    {
        var header = new byte[10];
        ReadAll(header, cancellationToken);
        if (Encoding.ASCII.GetString(header, 0, 8) != HandshakeMagic)
        {
            throw new IOException("The RemotePairing packet carries an invalid magic.");
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8));
        var body = new byte[length];
        ReadAll(body, cancellationToken);
        return body;
    }

    private void ReadAll(byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = _stream.ReadAsync(buffer.AsMemory(read), cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            if (count <= 0)
            {
                throw new IOException("The RemotePairing stream closed during a read.");
            }

            read += count;
        }
    }

    private static byte[] HkdfSha512(byte[] secret, string? salt, string info)
    {
        var saltBytes = salt is null ? [] : Encoding.UTF8.GetBytes(salt);
        return HKDF.DeriveKey(HashAlgorithmName.SHA512, secret, 32, saltBytes, Encoding.UTF8.GetBytes(info));
    }
}

/// <summary>
/// Combined ChaCha20-Poly1305 AEAD (ciphertext immediately followed by the 16-byte tag) over the
/// .NET 10 split-ciphertext/tag API.
/// <para>组合格式的 ChaCha20-Poly1305 AEAD（密文后紧跟 16 字节标签），构建于 .NET 10 的密文/标签
/// 分离 API 之上。</para>
/// </summary>
public static class AeadCipher
{
    /// <summary>Gets the Poly1305 tag length in bytes. <para>获取 Poly1305 标签长度（字节）。</para></summary>
    public const int TagLength = 16;

    /// <summary>
    /// Encrypts and appends the authentication tag.
    /// <para>加密并在密文后附加认证标签。</para>
    /// </summary>
    /// <param name="key">The 32-byte key. <para>32 字节密钥。</para></param>
    /// <param name="nonce">The 12-byte nonce. <para>12 字节 nonce。</para></param>
    /// <param name="plaintext">The plaintext. <para>明文。</para></param>
    /// <param name="associatedData">The optional associated data. <para>可选关联数据。</para></param>
    /// <returns>The ciphertext with the trailing tag. <para>带尾部标签的密文。</para></returns>
    public static byte[] Encrypt(byte[] key, byte[] nonce, byte[] plaintext, byte[]? associatedData = null)
    {
        using var cipher = new System.Security.Cryptography.ChaCha20Poly1305(key);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        cipher.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        var combined = new byte[plaintext.Length + TagLength];
        ciphertext.CopyTo(combined, 0);
        tag.CopyTo(combined, plaintext.Length);
        return combined;
    }

    /// <summary>
    /// Decrypts a combined ciphertext with trailing tag.
    /// <para>解密带尾部标签的组合密文。</para>
    /// </summary>
    /// <param name="key">The 32-byte key. <para>32 字节密钥。</para></param>
    /// <param name="nonce">The 12-byte nonce. <para>12 字节 nonce。</para></param>
    /// <param name="combined">The ciphertext with trailing tag. <para>带尾部标签的密文。</para></param>
    /// <param name="associatedData">The optional associated data. <para>可选关联数据。</para></param>
    /// <returns>The plaintext. <para>明文。</para></returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Authentication failed. <para>认证失败时抛出。</para></exception>
    public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] combined, byte[]? associatedData = null)
    {
        if (combined.Length < TagLength)
        {
            throw new System.Security.Cryptography.CryptographicException("The AEAD payload is shorter than the tag.");
        }

        using var cipher = new System.Security.Cryptography.ChaCha20Poly1305(key);
        var ciphertext = combined[..^TagLength];
        var tag = combined[^TagLength..];
        var plaintext = new byte[ciphertext.Length];
        cipher.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }
}

/// <summary>
/// X25519 key agreement wrapper over BouncyCastle.
/// <para>基于 BouncyCastle 的 X25519 密钥协商包装。</para>
/// </summary>
internal sealed class X25519PrivateKey
{
    private readonly Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters _privateKey;

    public X25519PrivateKey(byte[]? self)
    {
        _privateKey = self is null
            ? new Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters(
                new Org.BouncyCastle.Security.SecureRandom())
            : new Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters(self);
    }

    /// <summary>
    /// Exports the raw public key.
    /// <para>导出原始公钥。</para>
    /// </summary>
    /// <returns>The 32-byte public key. <para>32 字节公钥。</para></returns>
    public byte[] ExportPublicKey() => _privateKey.GeneratePublicKey().GetEncoded();

    /// <summary>
    /// Derives the 32-byte shared secret with the peer public key.
    /// <para>用对端公钥派生 32 字节共享密钥。</para>
    /// </summary>
    /// <param name="peerPublic">The peer public key. <para>对端公钥。</para></param>
    /// <returns>The shared secret. <para>共享密钥。</para></returns>
    public byte[] DeriveSharedSecret(byte[] peerPublic)
    {
        var agreement = new Org.BouncyCastle.Crypto.Agreement.X25519Agreement();
        agreement.Init(_privateKey);
        var output = new byte[32];
        agreement.CalculateAgreement(new Org.BouncyCastle.Crypto.Parameters.X25519PublicKeyParameters(peerPublic), output);
        return output;
    }
}

/// <summary>
/// Ed25519 signer wrapper over a 32-byte seed, using BouncyCastle.
/// <para>基于 32 字节种子、使用 BouncyCastle 的 Ed25519 签名包装。</para>
/// </summary>
internal sealed class Ed25519Signer
{
    private readonly Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters _privateKey;

    public Ed25519Signer(byte[] seed) =>
        _privateKey = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(seed);

    /// <summary>
    /// Exports the raw public key.
    /// <para>导出原始公钥。</para>
    /// </summary>
    /// <returns>The 32-byte public key. <para>32 字节公钥。</para></returns>
    public byte[] ExportPublicKey() => _privateKey.GeneratePublicKey().GetEncoded();

    /// <summary>
    /// Signs a message.
    /// <para>对消息签名。</para>
    /// </summary>
    /// <param name="message">The message bytes. <para>消息字节。</para></param>
    /// <returns>The 64-byte signature. <para>64 字节签名。</para></returns>
    public byte[] Sign(byte[] message)
    {
        var signer = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
        signer.Init(true, _privateKey);
        signer.BlockUpdate(message);
        return signer.GenerateSignature();
    }
}

/// <summary>
/// Raised when the RemotePairing pairing finished and the connection must be re-established.
/// <para>RemotePairing 配对完成后抛出；调用方必须重建连接。</para>
/// </summary>
public sealed class RemotePairingCompletedException : IOException
{
    /// <summary>
    /// Creates the exception.
    /// <para>创建异常。</para>
    /// </summary>
    public RemotePairingCompletedException() : base("The RemotePairing pairing completed; reconnect required.")
    {
    }
}