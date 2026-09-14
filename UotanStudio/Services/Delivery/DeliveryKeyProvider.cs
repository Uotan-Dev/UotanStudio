using System.Security.Cryptography;
using UotanStudio.Services.Keys;

namespace UotanStudio.Services.Delivery;

/// <summary>Connects the application key library to the delivery library's signing abstraction.</summary>
internal sealed class DeliveryKeyProvider : IDeliverySigningKeyProvider
{
    public RSA LoadPrivateKey(string path) => KeyLibraryService.LoadPrivateKey(path);
}