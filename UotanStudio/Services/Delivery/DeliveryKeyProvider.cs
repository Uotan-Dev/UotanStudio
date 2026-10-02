using System.Security.Cryptography;
using UotanStudio.Services.Keys;

namespace UotanStudio.Services.Delivery;

/// <summary>
/// <para>把应用密钥库接入交付库的签名抽象，供桌面宿主注入门面与 MCP 宿主。</para>
/// Connects the application key library to the delivery library's signing abstraction, injected into the
/// delivery facade and the MCP host by the desktop shell.</summary>
public sealed class DeliveryKeyProvider : IDeliverySigningKeyProvider
{
    public RSA LoadPrivateKey(string path) => KeyLibraryService.LoadPrivateKey(path);
}