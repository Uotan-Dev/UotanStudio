using System.Buffers.Binary;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Security;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Boot ROM SLA response using one dedicated RSA private key held in either borrowed or owned mode.</summary>
/// <remarks>
/// Use only an authorized key dedicated to this protocol, never a general-purpose signing
/// or TLS key. The queried device binding is not cryptographic device authentication.
/// In the two-argument constructor the caller keeps ownership of the RSA instance and must not
/// mutate or dispose it during use. The three-argument constructor with <c>ownsKey</c> transfers
/// that ownership to this responder: <see cref="Dispose"/> releases the key, and a caller that did
/// not transfer ownership may still dispose the responder, which only stops further responses and
/// leaves the borrowed key alive. Public-key identity is captured and checked on each call.
/// Nonexportable keys require a different IMtkBootRomSlaResponder implementation. No credentials,
/// key search or fallback are included. Private operations are synchronous; cancellation is checked
/// before and after them.
/// </remarks>
internal sealed class MtkBootRomRsaSlaResponder : IMtkBootRomSlaResponder
{
    private readonly MtkDeviceInfo _device;
    private readonly byte[] _modulus;
    private readonly byte[] _exponent;
    private readonly bool _ownsKey;
    private readonly Lock _gate = new();
    private RSA? _key;

    /// <summary>Binds one borrowed RSA key and one exact queried identity to future responses.</summary>
    /// <param name="key">Caller-owned key with exportable private CRT parameters.</param>
    /// <param name="expectedDevice">Exact identity authorized by the caller, including security flags.</param>
    public MtkBootRomRsaSlaResponder(RSA key, MtkDeviceInfo expectedDevice)
        : this(key, expectedDevice, ownsKey: false)
    {
    }

    /// <summary>Binds one RSA key and one exact queried identity, optionally taking over its lifetime.</summary>
    /// <param name="key">Key with exportable private CRT parameters; the responder releases it when
    /// <paramref name="ownsKey"/> is set, including when the selected key profile is rejected.</param>
    /// <param name="expectedDevice">Exact identity authorized by the caller, including security flags.</param>
    /// <param name="ownsKey">Whether this responder owns and disposes <paramref name="key"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="expectedDevice"/> is null.</exception>
    /// <exception cref="ArgumentException">The key is not a 1024, 2048, 3072 or 4096-bit RSA key.</exception>
    public MtkBootRomRsaSlaResponder(RSA key, MtkDeviceInfo expectedDevice, bool ownsKey)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expectedDevice);
        RSAParameters publicKey;
        try
        {
            publicKey = key.ExportParameters(false);
        }
        catch
        {
            ReleaseOwnedKey(key, ownsKey);
            throw;
        }
        if (publicKey.Modulus is not { Length: 128 or 256 or 384 or 512 } || publicKey.Exponent is not { Length: > 0 })
        {
            ReleaseOwnedKey(key, ownsKey);
            throw new ArgumentException("The SLA software-key profile supports 1024, 2048, 3072 or 4096-bit RSA.", nameof(key));
        }
        _key = key;
        _ownsKey = ownsKey;
        _device = expectedDevice;
        _modulus = publicKey.Modulus;
        _exponent = publicKey.Exponent;
    }

    private static void ReleaseOwnedKey(RSA key, bool ownsKey)
    {
        if (ownsKey)
        {
            key.Dispose();
        }
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
            var key = _key ?? throw new ObjectDisposedException(nameof(MtkBootRomRsaSlaResponder));
            var parameters = key.ExportParameters(true);
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

    /// <summary>Releases the bound key when this responder owns it and stops further responses.
    /// A borrowed key is left alive and remains the caller's responsibility.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            var key = _key;
            _key = null;
            if (_ownsKey)
            {
                key?.Dispose();
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