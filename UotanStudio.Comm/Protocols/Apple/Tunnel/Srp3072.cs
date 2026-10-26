using System.Text;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;

namespace UotanStudio.Comm.Protocols.Apple.Tunnel;

/// <summary>
/// SRP-6a client over the RFC 5054 3072-bit group with SHA-512, the RemotePairing pairing-setup
/// parameters (<c>Pair-Setup</c> identity, PIN as password).
/// <para>RemotePairing 配对设置使用的 SRP-6a 客户端：RFC 5054 3072 位群组 + SHA-512
/// （<c>Pair-Setup</c> 身份、PIN 作为密码）。</para>
/// </summary>
public sealed class Srp3072Client
{
    private const string PrimeHex =
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB9ED529077096966D670C35E4ABC9804F1746C08CA18217C32905E462E36CE3BE39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF";

    private static readonly BigInteger Prime = new(PrimeHex, 16);
    private static readonly BigInteger Generator = BigInteger.ValueOf(5);

    private readonly Srp6Client _client = new();

    /// <summary>
    /// Initializes the client session and consumes the server's public value and salt.
    /// <para>初始化客户端会话并处理服务端的公开值与盐。</para>
    /// </summary>
    /// <param name="identity">The SRP identity, <c>Pair-Setup</c>. <para>SRP 身份，即 <c>Pair-Setup</c>。</para></param>
    /// <param name="password">The pairing PIN. <para>配对 PIN。</para></param>
    /// <param name="salt">The server salt. <para>服务端盐。</para></param>
    /// <param name="serverPublicValue">The server public value B. <para>服务端公开值 B。</para></param>
    /// <returns>The client public value A. <para>客户端公开值 A。</para></returns>
    /// <exception cref="InvalidDataException">The server public value is invalid for the group. <para>服务端公开值对该群组非法时抛出。</para></exception>
    public byte[] ProcessChallenge(string identity, string password, byte[] salt, byte[] serverPublicValue)
    {
        _client.Init(Prime, Generator, new Sha512Digest(),
            new Org.BouncyCastle.Security.SecureRandom());
        var clientPublic = _client.GenerateClientCredentials(
            salt, Encoding.UTF8.GetBytes(identity), Encoding.UTF8.GetBytes(password));
        var shared = _client.CalculateSecret(new BigInteger(1, serverPublicValue));
        _ = shared;
        return clientPublic.ToByteArrayUnsigned();
    }

    /// <summary>
    /// Verifies the server proof M2 and returns the shared secret K.
    /// <para>校验服务端证明 M2 并返回共享密钥 K。</para>
    /// </summary>
    /// <param name="serverProof">The server proof M2. <para>服务端证明 M2。</para></param>
    /// <returns>The 64-byte shared secret. <para>64 字节共享密钥。</para></returns>
    /// <exception cref="InvalidDataException">The server proof does not verify. <para>服务端证明未通过校验时抛出。</para></exception>
    public byte[] VerifyServerProof(byte[] serverProof)
    {
        var secret = _client.CalculateSessionKey();
        if (!_client.VerifyServerEvidenceMessage(new BigInteger(1, serverProof)))
        {
            throw new InvalidDataException("The RemotePairing server proof does not verify.");
        }

        return secret.ToByteArrayUnsigned();
    }

    /// <summary>
    /// Computes the client proof M1.
    /// <para>计算客户端证明 M1。</para>
    /// </summary>
    /// <returns>The client evidence message M1. <para>客户端证明 M1。</para></returns>
    public byte[] ComputeClientProof() => _client.CalculateClientEvidenceMessage().ToByteArrayUnsigned();
}