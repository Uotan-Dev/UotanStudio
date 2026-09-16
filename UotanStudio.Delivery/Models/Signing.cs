using System.Security.Cryptography;

namespace UotanStudio.Services.Delivery;

/// <summary><para>向交付构建器提供私钥，使交付库不依赖具体的密钥存储实现。</para>
/// Supplies private keys to delivery builders without coupling the delivery library to key storage.</summary>
internal interface IDeliverySigningKeyProvider
{
    /// <summary><para>按路径加载并返回 RSA 私钥。</para>Loads and returns the RSA private key for a path.</summary>
    RSA LoadPrivateKey(string path);
}

/// <summary><para>通过注入的密钥供应器加载签名私钥，供应器不可用时抛出明确错误。</para>
/// Loads a signing private key through the injected provider, throwing a clear error when the provider is unavailable.</summary>
internal static class DeliverySigning
{
    /// <summary>
    /// <para>从路径加载 RSA 私钥；未配置 provider 时抛出 InvalidOperationException，表明签名输出需要密钥供应器。</para>
    /// Loads the RSA private key for a path; throws InvalidOperationException when no provider is configured, indicating signed output needs a key provider.
    /// </summary>
    internal static RSA Load(IDeliverySigningKeyProvider? provider, string path)
        => provider?.LoadPrivateKey(path)
            ?? throw new InvalidOperationException("A delivery signing-key provider is required for signed output.");
}