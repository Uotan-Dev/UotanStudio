using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Security;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Boot ROM SLA response using one caller-owned, exportable, dedicated RSA private key.</summary>
/// <remarks>
/// Use only an authorized key dedicated to this protocol, never a general-purpose signing
/// or TLS key. The queried device binding is not cryptographic device authentication.
/// The caller owns the RSA instance and must not mutate/dispose it during use. Public-key
/// identity is captured and checked on each call. Nonexportable keys require a different
/// IMtkBootRomSlaResponder implementation. No credentials, key search or fallback are included.
/// Private operations are synchronous; cancellation is checked before and after them.
/// </remarks>
public sealed class MtkBootRomRsaSlaResponder : IMtkBootRomSlaResponder
{
    private readonly RSA _key;
    private readonly MtkDeviceInfo _device;
    private readonly byte[] _modulus;
    private readonly byte[] _exponent;
    private readonly Lock _gate = new();

    /// <summary>Binds one borrowed RSA key and one exact queried identity to future responses.</summary>
    /// <param name="key">Caller-owned key with exportable private CRT parameters.</param>
    /// <param name="expectedDevice">Exact identity authorized by the caller, including security flags.</param>
    public MtkBootRomRsaSlaResponder(RSA key, MtkDeviceInfo expectedDevice)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expectedDevice);
        var publicKey = key.ExportParameters(false);
        if (publicKey.Modulus is not { Length: 128 or 256 or 384 or 512 } || publicKey.Exponent is not { Length: > 0 })
        {
            throw new ArgumentException("The SLA software-key profile supports 1024, 2048, 3072 or 4096-bit RSA.", nameof(key));
        }
        _key = key;
        _device = expectedDevice;
        _modulus = publicKey.Modulus;
        _exponent = publicKey.Exponent;
    }

    /// <inheritdoc />
    public Task<byte[]> CreateResponseAsync(MtkDeviceInfo device, ReadOnlyMemory<byte> wireChallenge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();
        if (device != _device)
        {
            throw new InvalidOperationException("The queried target does not match the identity bound to this SLA key.");
        }
        if (wireChallenge.Length == 0 || (wireChallenge.Length & 1) != 0 || wireChallenge.Length > _modulus.Length - 11)
        {
            throw new ArgumentException("The SLA challenge must be even-length and fit the selected RSA type-1 block.", nameof(wireChallenge));
        }
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameters = _key.ExportParameters(true);
            byte[]? message = null;
            byte[]? response = null;
            try
            {
                if (parameters.Modulus is null || parameters.Exponent is null
                    || !parameters.Modulus.AsSpan().SequenceEqual(_modulus)
                    || !parameters.Exponent.AsSpan().SequenceEqual(_exponent))
                {
                    throw new InvalidOperationException("The caller replaced the selected SLA key.");
                }
                message = wireChallenge.ToArray();
                SwapWordBytes(message);
                var encoding = new Pkcs1Encoding(new RsaBlindedEngine());
                encoding.Init(true, DotNetUtilities.GetRsaKeyPair(parameters).Private);
                response = encoding.ProcessBlock(message, 0, message.Length);
                if (response.Length != _modulus.Length)
                {
                    throw new CryptographicException("The RSA response width does not match the selected key.");
                }
                SwapWordBytes(response);
                cancellationToken.ThrowIfCancellationRequested();
                var result = response;
                response = null;
                return Task.FromResult(result);
            }
            finally
            {
                Clear(message);
                Clear(response);
                Clear(parameters.D);
                Clear(parameters.P);
                Clear(parameters.Q);
                Clear(parameters.DP);
                Clear(parameters.DQ);
                Clear(parameters.InverseQ);
            }
        }
    }

    private static void SwapWordBytes(Span<byte> bytes)
    {
        for (var offset = 0; offset < bytes.Length; offset += sizeof(ushort))
        {
            var word = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
            BinaryPrimitives.WriteUInt16BigEndian(bytes[offset..], word);
        }
    }

    private static void Clear(byte[]? bytes)
    {
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}