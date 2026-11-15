using System.Text.Json.Serialization;

namespace UotanStudio.Net.Agc;

/// <summary>Request payload for device registration. <para>设备注册的请求负载。</para></summary>
/// <param name="DeviceName">Human-readable device name. <para>设备可读名。</para></param>
/// <param name="Udid">Device UDID. <para>设备 UDID。</para></param>
/// <param name="DeviceType">Device type code. <para>设备类型码。</para></param>
public sealed record AgcDevicePayload(
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("udid")] string Udid,
    [property: JsonPropertyName("deviceType")] string DeviceType);

/// <summary>Request payload for certificate application. <para>证书申请的请求负载。</para></summary>
/// <param name="Csr">CSR content in PEM form. <para>PEM 形式的 CSR 内容。</para></param>
/// <param name="CertName">Deterministic certificate name. <para>确定性证书名。</para></param>
/// <param name="CertType">Certificate type. <para>证书类型。</para></param>
public sealed record AgcCertificatePayload(
    [property: JsonPropertyName("csr")] string Csr,
    [property: JsonPropertyName("certName")] string CertName,
    [property: JsonPropertyName("certType")] string CertType);

/// <summary>Request payload for debug profile application. <para>调试 Profile 申请的请求负载。</para></summary>
/// <param name="CertList">Certificate ids to bind. <para>需绑定的证书 ID。</para></param>
/// <param name="PackageName">Application package name. <para>应用包名。</para></param>
/// <param name="ProvisionName">Deterministic profile name. <para>确定性 Profile 名。</para></param>
/// <param name="DeviceList">Authorized cloud device ids. <para>授权的云端设备 ID。</para></param>
/// <param name="AclPermissionList">ACL permissions; empty in the automatic flow.
/// <para>ACL 权限；自动流程为空。</para></param>
public sealed record AgcProvisionPayload(
    [property: JsonPropertyName("certList")] IReadOnlyList<string> CertList,
    [property: JsonPropertyName("packageName")] string PackageName,
    [property: JsonPropertyName("provisionName")] string ProvisionName,
    [property: JsonPropertyName("deviceList")] IReadOnlyList<string> DeviceList,
    [property: JsonPropertyName("aclPermissionList")] IReadOnlyList<string> AclPermissionList);

/// <summary>Request payload for the signed download URL exchange. <para>签名下载地址交换的请求负载。</para></summary>
/// <param name="Url">Object URL returned by the apply calls. <para>申请类调用返回的对象地址。</para></param>
public sealed record AgcReapplyPayload([property: JsonPropertyName("url")] string Url);

/// <summary>Source-generated JSON context for the AGC request payloads.
/// <para>AGC 请求负载的源生成 JSON 上下文。</para></summary>
[JsonSerializable(typeof(AgcDevicePayload))]
[JsonSerializable(typeof(AgcCertificatePayload))]
[JsonSerializable(typeof(AgcProvisionPayload))]
[JsonSerializable(typeof(AgcReapplyPayload))]
internal sealed partial class AgcJsonContext : JsonSerializerContext;