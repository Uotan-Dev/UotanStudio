using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace UotanStudio.Tests.TestInfrastructure;

/// <summary>
/// Reuses generated key bytes for signing fixtures, while each test owns its RSA instance.
/// Key-generation tests must continue to exercise the production generator independently.
/// </summary>
internal static class SigningTestKeys
{
    private static readonly ConcurrentDictionary<(int Bits, bool Alternate), Lazy<byte[]>> Keys = new();

    /// <summary>Creates an isolated signing key; alternate keys are reserved for mismatch tests.</summary>
    internal static RSA Create(int bits = 2048, bool alternate = false)
    {
        var encoded = Keys.GetOrAdd((bits, alternate), static identity => new(() =>
        {
            using var generated = RSA.Create(identity.Bits);
            return generated.ExportPkcs8PrivateKey();
        })).Value;
        var key = RSA.Create();
        try
        {
            key.ImportPkcs8PrivateKey(encoded, out _);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}