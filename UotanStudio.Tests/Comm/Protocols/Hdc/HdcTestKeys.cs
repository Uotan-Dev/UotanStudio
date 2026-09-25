using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Hdc;

namespace UotanStudio.Tests.Comm.Protocols.Hdc;

/// <summary>
/// Shares one 3072-bit RSA key pair across HDC protocol tests. Key generation dominates their
/// runtime, so the parameters are produced once and imported into a fresh key per session; each
/// session still owns an independent, disposable RSA instance.
/// <para>在 HDC 协议测试之间共享一对 3072 位 RSA 密钥。密钥生成占其运行时间的主要部分，
/// 因此参数只生成一次并导入到每个会话的新密钥中；每个会话仍持有独立、可释放的 RSA 实例。</para>
/// </summary>
internal static class HdcTestKeys
{
    private static readonly Lazy<RSAParameters> CachedParameters = new(() =>
    {
        using var rsa = RSA.Create(3072);
        return rsa.ExportParameters(includePrivateParameters: true);
    });

    /// <summary>Creates a fresh 3072-bit RSA key from the cached parameters.</summary>
    internal static RSA CreateRsa()
    {
        var rsa = RSA.Create();
        rsa.ImportParameters(CachedParameters.Value);
        return rsa;
    }

    /// <summary>Creates HDC authentication backed by a fresh key from the cached parameters.</summary>
    internal static HdcAuthentication CreateAuthentication() => new(CreateRsa());
}