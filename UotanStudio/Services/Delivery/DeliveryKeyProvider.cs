using System.Security.Cryptography;
using UotanStudio.Delivery;
using UotanStudio.Services.Keys;

namespace UotanStudio.Services.Delivery;

/// <summary>
/// Adapts the application key library to the delivery signing-key contract.
/// <para>把应用密钥库适配到交付签名密钥契约。</para>
/// </summary>
public sealed class DeliveryKeyProvider : IDeliverySigningKeyProvider
{
    /// <summary>
    /// Loads the signing private key stored at a path.
    /// <para>加载指定路径存储的签名私钥。</para>
    /// </summary>
    /// <param name="path">Path of the stored key. <para>存储密钥的路径。</para></param>
    /// <returns>The loaded RSA private key. <para>加载得到的 RSA 私钥。</para></returns>
    public RSA LoadPrivateKey(string path) => KeyLibraryService.LoadPrivateKey(path);
}