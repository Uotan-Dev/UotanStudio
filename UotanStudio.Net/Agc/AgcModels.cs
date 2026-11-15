namespace UotanStudio.Net.Agc;

/// <summary>AGC response envelope: success requires <c>ret.code == 0</c> even over HTTP 200.
/// <para>AGC 响应信封：即使 HTTP 200，成功判据也是 <c>ret.code == 0</c>。</para></summary>
/// <param name="Code">Envelope result code. <para>信封结果码。</para></param>
/// <param name="Message">Envelope result message; may be an object or a JSON-encoded string.
/// <para>信封结果消息；可能是对象或 JSON 编码的字符串。</para></param>
public sealed record AgcRet(int Code, string Message)
{
    /// <summary>The success code. <para>成功码。</para></summary>
    public const int Success = 0;
}

/// <summary>One team (developer group) of the signed-in account. <para>登录账号的一个团队（开发者组）。</para></summary>
/// <param name="TeamId">Team identifier. <para>团队标识。</para></param>
/// <param name="Name">Team display name. <para>团队显示名。</para></param>
public sealed record AgcTeam(string TeamId, string Name);

/// <summary>One device registered under the account. <para>账号下注册的一台设备。</para></summary>
/// <param name="DeviceId">Cloud device identifier. <para>云端设备标识。</para></param>
/// <param name="Udid">Device UDID. <para>设备 UDID。</para></param>
/// <param name="DeviceName">Human-readable device name. <para>设备可读名。</para></param>
/// <param name="DeviceType">Device type code. <para>设备类型码。</para></param>
public sealed record AgcDevice(string DeviceId, string Udid, string DeviceName, string DeviceType);

/// <summary>One signing certificate issued by AGC. <para>AGC 签发的一张证书。</para></summary>
/// <param name="CertId">Certificate identifier. <para>证书标识。</para></param>
/// <param name="CertName">Certificate name, e.g. <c>auto_debug_&lt;teamId&gt;.cer</c>.
/// <para>证书名，例如 <c>auto_debug_&lt;teamId&gt;.cer</c>。</para></param>
/// <param name="CertType">Certificate type (debug/release). <para>证书类型（debug/release）。</para></param>
public sealed record AgcCertificate(string CertId, string CertName, string CertType);

/// <summary>One provisioning profile registered under the account. <para>账号下注册的一个 Profile。</para></summary>
/// <param name="ProvisionId">Profile identifier. <para>Profile 标识。</para></param>
/// <param name="ProvisionName">Profile name. <para>Profile 名。</para></param>
/// <param name="BundleName">Bound application package name. <para>绑定的应用包名。</para></param>
/// <param name="Type">Profile type (debug/release). <para>Profile 类型（debug/release）。</para></param>
public sealed record AgcProvision(string ProvisionId, string ProvisionName, string BundleName, string Type);

/// <summary>Token exchange result of the DevEco login flow. <para>DevEco 登录流程的令牌交换结果。</para></summary>
/// <param name="AccessToken">Long-lived access token carried as the <c>oauth2Token</c> header.
/// <para>作为 <c>oauth2Token</c> 头携带的长效访问令牌。</para></param>
/// <param name="UserId">Account user id carried as the <c>uid</c> header.
/// <para>作为 <c>uid</c> 头携带的账号用户 ID。</para></param>
/// <param name="UserName">Account display name. <para>账号显示名。</para></param>
public sealed record AgcSession(string AccessToken, string UserId, string UserName);

/// <summary>Raw response of one AGC call: the parsed envelope plus the untouched body for diagnostics.
/// <para>一次 AGC 调用的原始响应：解析后的信封加上未加工的响应体供诊断。</para></summary>
/// <param name="Ret">Parsed envelope. <para>解析后的信封。</para></param>
/// <param name="RawBody">Response body bytes. <para>响应体字节。</para></param>
public sealed record AgcRawResponse(AgcRet Ret, byte[] RawBody);