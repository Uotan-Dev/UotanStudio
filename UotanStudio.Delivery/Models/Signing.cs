using System.Security.Cryptography;

namespace UotanStudio.Services.Delivery;

/// <summary>Supplies private keys to delivery builders without coupling the delivery library to key storage.</summary>
internal interface IDeliverySigningKeyProvider
{
    RSA LoadPrivateKey(string path);
}

internal static class DeliverySigning
{
    internal static RSA Load(IDeliverySigningKeyProvider? provider, string path)
        => provider?.LoadPrivateKey(path)
            ?? throw new InvalidOperationException("A delivery signing-key provider is required for signed output.");
}