using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Flash;

/// <summary>
/// <para>用用户提供的固定 256 字节鉴权数据应答小米 Firehose 挑战，对齐上游 xiaomi 模块的
/// <c>edl_auth</c>：凭据块直接作为签名发送，挑战内容原样忽略，也不尝试任何内置凭据。</para>
/// Answers a Xiaomi Firehose challenge with a user-supplied fixed 256-byte auth-data blob, matching
/// the upstream xiaomi module's <c>edl_auth</c>: the blob is sent as the signature, the challenge is
/// ignored verbatim, and no built-in credentials are attempted.
/// </summary>
/// <param name="authData">
/// <para>恰好 256 字节的鉴权数据；构造时会复制以避免外部修改。</para>
/// Exactly 256 bytes of auth data; copied at construction so the caller cannot mutate it.
/// </param>
public sealed class EdlXiaomiAuthFileProvider(byte[] authData) : IEdlXiaomiAuthenticationProvider
{
    private readonly byte[] _authData = checked(authData.Length == 256
        ? (byte[])authData.Clone()
        : throw new ArgumentOutOfRangeException(nameof(authData),
            "The Xiaomi auth data must contain exactly 256 bytes."));

    /// <summary>
    /// <para>返回固定的鉴权数据块作为签名；挑战与身份仅用于审计，不参与签名计算。</para>
    /// Returns the fixed auth-data blob as the signature; the challenge and identity are audit-only
    /// and take no part in the signature computation.
    /// </summary>
    /// <param name="identity">The optional Sahara identity. <para>可选的 Sahara 身份。</para></param>
    /// <param name="challenge">The challenge bytes reported by the target.
    /// <para>目标报告的挑战字节。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation. <para>协作式取消令牌。</para></param>
    /// <returns>
    /// <para>新分配的 256 字节签名。</para>
    /// A newly allocated 256-byte signature.
    /// </returns>
    public Task<byte[]> CreateSignatureAsync(SaharaDeviceIdentity? identity, ReadOnlyMemory<byte> challenge,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult((byte[])_authData.Clone());
    }
}
