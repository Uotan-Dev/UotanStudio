using System.Security.Cryptography;

namespace UotanStudio.Services.Delivery;

/// <summary>
/// <para>向交付构建器提供私钥，使交付库不依赖具体的密钥存储实现。宿主或无头调用方实现本接口并经
/// <see cref="DeliveryService"/> 注入；缺少供应器时签名输出按 <see cref="DeliverySigning.Load"/> 如实失败。</para>
/// Supplies private keys to delivery builders without coupling the delivery library to key storage. Hosts and
/// headless callers implement this interface and inject it through <see cref="DeliveryService"/>; signed output
/// fails honestly when no provider is available.
/// </summary>
public interface IDeliverySigningKeyProvider
{
    /// <summary><para>按路径加载并返回 RSA 私钥。</para>Loads and returns the RSA private key for a path.</summary>
    /// <param name="path">
    /// <para>计划中记录的密钥路径，由实现决定如何解析为实际密钥材料。</para>
    /// Key path recorded in the delivery plan; the implementation decides how to resolve it into key material.
    /// </param>
    /// <returns>
    /// <para>调用方负责释放的 RSA 私钥实例。</para>An RSA private key instance the caller disposes.
    /// </returns>
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
    /// <exception cref="System.InvalidOperationException"><paramref name="provider"/> is <see langword="null"/>,
    /// so signed output cannot be produced.
    /// <para><paramref name="provider"/> 为 <see langword="null"/>，无法产出签名输出。</para></exception>
    internal static RSA Load(IDeliverySigningKeyProvider? provider, string path)
        => provider?.LoadPrivateKey(path)
            ?? throw new InvalidOperationException("A delivery signing-key provider is required for signed output.");
}