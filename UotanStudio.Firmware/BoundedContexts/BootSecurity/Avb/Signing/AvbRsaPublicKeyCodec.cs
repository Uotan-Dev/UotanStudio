using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Signing;

/// <summary>Encodes and validates the RSA public-key blob produced by <c>avbtool extract_public_key</c>.</summary>
public static class AvbRsaPublicKeyCodec
{
    /// <summary>Exports an RSA key in libavb's big-endian public-key format.</summary>
    public static byte[] Export(RSA key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var parameters = key.ExportParameters(false);
        if (parameters.Modulus is null || parameters.Exponent is not [1, 0, 1])
        {
            throw new CryptographicException("AVB RSA keys must use public exponent 65537.");
        }
        return Export(parameters.Modulus, key.KeySize);
    }

    /// <summary>Imports and validates a libavb RSA public-key blob.</summary>
    public static RSA Import(ReadOnlySpan<byte> blob)
        => TryImport(blob) ?? throw new InvalidDataException("The AVB RSA public-key blob is invalid.");

    /// <summary>Attempts to import a libavb RSA public-key blob, including Montgomery-field validation.</summary>
    public static RSA? TryImport(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 8)
        {
            return null;
        }
        var keyBits = BinaryPrimitives.ReadUInt32BigEndian(blob);
        if (keyBits is not (2048 or 4096 or 8192))
        {
            return null;
        }
        var keyBytes = checked((int)keyBits / 8);
        if (blob.Length != 8 + 2 * keyBytes)
        {
            return null;
        }
        var modulus = blob.Slice(8, keyBytes);
        if ((modulus[0] & 0x80) == 0 || (modulus[^1] & 1) == 0
            || BinaryPrimitives.ReadUInt32BigEndian(blob.Slice(4, 4)) != ComputeN0Inv(modulus))
        {
            return null;
        }
        var expectedRr = ComputeRr(modulus, checked((int)keyBits));
        if (!CryptographicOperations.FixedTimeEquals(expectedRr, blob.Slice(8 + keyBytes, keyBytes)))
        {
            return null;
        }

        try
        {
            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = modulus.ToArray(),
                Exponent = [1, 0, 1]
            });
            return rsa;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Atomically writes an AVB public-key blob without replacing the destination on failure.</summary>
    public static async Task ExportAsync(
        RSA key,
        string outputPath,
        bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var blob = Export(key);
        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, blob, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite);
        }
        finally
        {
            File.Delete(temporary);
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    internal static byte[] Export(RsaKeyParameters key, int keyBits)
    {
        ArgumentNullException.ThrowIfNull(key);
        var modulus = key.Modulus.ToByteArrayUnsigned();
        return Export(modulus, keyBits);
    }

    private static byte[] Export(ReadOnlySpan<byte> modulus, int keyBits)
    {
        if (keyBits is not (2048 or 4096 or 8192))
        {
            throw new CryptographicException("AVB RSA keys must be 2048, 4096, or 8192 bits.");
        }
        var keyBytes = keyBits / 8;
        if (modulus.Length > keyBytes)
        {
            modulus = modulus[^keyBytes..];
        }
        var paddedModulus = new byte[keyBytes];
        modulus.CopyTo(paddedModulus.AsSpan(keyBytes - modulus.Length));
        if ((paddedModulus[0] & 0x80) == 0 || (paddedModulus[^1] & 1) == 0)
        {
            throw new CryptographicException("AVB RSA modulus does not match its declared key size.");
        }

        var blob = new byte[checked(8 + 2 * keyBytes)];
        BinaryPrimitives.WriteUInt32BigEndian(blob, checked((uint)keyBits));
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(4), ComputeN0Inv(paddedModulus));
        paddedModulus.CopyTo(blob, 8);
        ComputeRr(paddedModulus, keyBits).CopyTo(blob, 8 + keyBytes);
        CryptographicOperations.ZeroMemory(paddedModulus);
        return blob;
    }

    private static uint ComputeN0Inv(ReadOnlySpan<byte> modulus)
    {
        var low = BinaryPrimitives.ReadUInt32BigEndian(modulus[^4..]);
        var inverse = 1U;
        for (var index = 0; index < 5; index++)
        {
            inverse = unchecked(inverse * (2U - low * inverse));
        }
        return unchecked(0U - inverse);
    }

    private static byte[] ComputeRr(ReadOnlySpan<byte> modulus, int keyBits)
    {
        var n = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        var value = (BigInteger.One << checked(keyBits * 2)) % n;
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[keyBits / 8];
        bytes.CopyTo(padded, padded.Length - bytes.Length);
        return padded;
    }
}
