using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace UotanStudio.Net.Agc;

/// <summary>Authorization context carried on every AGC call.
/// <para>每次 AGC 调用携带的授权上下文。</para></summary>
/// <param name="AccessToken">Access token (<c>oauth2Token</c> header). <para>访问令牌（<c>oauth2Token</c> 头）。</para></param>
/// <param name="UserId">Account user id (<c>uid</c> header). <para>账号用户 ID（<c>uid</c> 头）。</para></param>
/// <param name="TeamId">Team id (<c>teamId</c> header); empty for account-level calls.
/// <para>团队 ID（<c>teamId</c> 头）；账号级调用为空。</para></param>
public sealed record AgcCallContext(string AccessToken, string UserId, string TeamId = "")
{
    /// <summary>An empty context for unauthenticated calls. <para>未鉴权调用使用的空上下文。</para></summary>
    public static readonly AgcCallContext Anonymous = new(string.Empty, string.Empty, string.Empty);
}

/// <summary>
/// REST client for the AGC (AppGallery Connect) signing-material services DevEco Studio drives during automatic
/// signing: team resolution, device registration, debug certificate and provisioning profile management, and the
/// signed-URL exchange for material downloads.
/// <para>AGC（AppGallery Connect）签名材料服务的 REST 客户端，覆盖 DevEco Studio 自动签名驱动的团队解析、
/// 设备注册、调试证书与 Profile 管理，以及材料下载的签名 URL 交换。</para>
/// </summary>
/// <remarks>
/// Every JSON response is capped and parsed tolerantly: AGC nests payloads under varying keys across versions, so
/// the parser locates values by key search instead of a fixed shape, and the raw body always accompanies the parsed
/// result for diagnostics. Success requires the envelope <c>ret.code == 0</c> even over HTTP 200. Download URLs are
/// validated against a SSRF guard (HTTPS, non-loopback, non-private host) before any byte is fetched.
/// <para>每个 JSON 响应都有上限并以容忍方式解析：AGC 各版本把负载嵌在不同键下，解析器按键搜索取值而非固定
/// 结构，原始响应体始终随解析结果一并返回以便诊断。即使 HTTP 200，成功判据仍是信封 <c>ret.code == 0</c>。
/// 下载地址在取回任何字节前先经 SSRF 护栏（HTTPS、非回环、非私网主机）校验。</para>
/// </remarks>
public static class AgcClient
{
    /// <summary>API origin for the connect services. <para>connect 服务的 API 源站。</para></summary>
    public const string ApiOrigin = "https://connect-api.cloud.huawei.com";

    /// <summary>Origin of the DevEco login and token exchange services.
    /// <para>DevEco 登录与令牌交换服务的源站。</para></summary>
    public const string AuthOrigin = "https://cn.devecostudio.huawei.com";

    /// <summary>Application id DevEco presents during login; <c>1009</c> is the DevEco Studio id.
    /// <para>登录时 DevEco 呈报的应用 ID；<c>1009</c> 为 DevEco Studio。</para></summary>
    public const string LoginAppId = "1009";

    /// <summary>Maximum accepted response body size in bytes. <para>可接受的响应体最大字节数。</para></summary>
    public const int MaximumResponseBytes = 2 << 20;

    /// <summary>Builds the loopback login page URL the user opens in a browser.
    /// <para>构造用户在浏览器中打开的回环登录页地址。</para></summary>
    /// <param name="port">Local loopback port awaiting the callback. <para>等待回调的本地回环端口。</para></param>
    /// <param name="callbackCode">Random one-time code guarding the callback. <para>守护回调的一次性随机码。</para></param>
    /// <returns>The login URL. <para>登录地址。</para></returns>
    public static string BuildLoginUrl(int port, string callbackCode)
        => $"{AuthOrigin}/console/DevEcoIDE/apply?port={port}&appid={LoginAppId}&code={Uri.EscapeDataString(callbackCode)}";

    /// <summary>
    /// Exchanges the login <c>tempToken</c> for the long-lived JWT used as the <c>oauth2Token</c> header.
    /// <para>用登录 <c>tempToken</c> 换取作为 <c>oauth2Token</c> 头的长效 JWT。</para>
    /// </summary>
    /// <param name="client">HTTP client. <para>HTTP 客户端。</para></param>
    /// <param name="tempToken">Callback temp token. <para>回调返回的 tempToken。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The session identity. <para>会话身份。</para></returns>
    /// <exception cref="InvalidCredentialException">The exchange did not yield an access token.
    /// <para>交换未得到访问令牌。</para></exception>
    public static async Task<AgcSession> ExchangeTempTokenAsync(HttpClient client, string tempToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempToken);
        var url = $"{AuthOrigin}/authrouter/auth/api/temptoken/check?tempToken={Uri.EscapeDataString(tempToken)}" +
            "&site=CN&version=1.0.0&appid=" + LoginAppId;
        var body = await ReadAsync(client, new Uri(url), cancellationToken).ConfigureAwait(false);
        return ParseTokenResponse(body) ?? throw new InvalidCredentialException(
            "The token exchange response carries no access token. 令牌交换响应不含访问令牌。");
    }

    /// <summary>
    /// Re-validates a stored JWT and returns the refreshed session identity.
    /// <para>复核已存 JWT 并返回刷新后的会话身份。</para>
    /// </summary>
    /// <param name="client">HTTP client. <para>HTTP 客户端。</para></param>
    /// <param name="accessToken">Stored JWT. <para>已存 JWT。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The session identity. <para>会话身份。</para></returns>
    /// <exception cref="InvalidCredentialException">The check did not yield an access token.
    /// <para>复核未得到访问令牌。</para></exception>
    public static async Task<AgcSession> CheckTokenAsync(HttpClient client, string accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        var body = await ReadAsync(client,
            new Uri($"{AuthOrigin}/authrouter/auth/api/jwToken/check"),
            cancellationToken,
            accessToken).ConfigureAwait(false);
        return ParseTokenResponse(body) ?? throw new InvalidCredentialException(
            "The token check response carries no access token. 令牌复核响应不含访问令牌。");
    }

    /// <summary>
    /// Lists the teams of the signed-in account. <para>列出登录账号的团队。</para></summary>
    /// <param name="client">HTTP client. <para>HTTP 客户端。</para></param>
    /// <param name="context">Authorization context. <para>授权上下文。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Teams in service order. <para>按服务顺序的团队列表。</para></returns>
    public static async Task<IReadOnlyList<AgcTeam>> GetTeamsAsync(HttpClient client, AgcCallContext context,
        CancellationToken cancellationToken)
    {
        var body = await SendAsync(client, HttpMethod.Get,
            new Uri($"{ApiOrigin}/api/ups/user-permission-service/v1/user-team-list"),
            context, null, cancellationToken).ConfigureAwait(false);
        return ParseTeams(body.RawBody);
    }

    /// <summary>
    /// Lists the registered devices of a team, one page at a time.
    /// <para>分页列出团队已注册的设备。</para></summary>
    /// <param name="client">HTTP client. <para>HTTP 客户端。</para></param>
    /// <param name="context">Authorization context including the team id.
    /// <para>含团队 ID 的授权上下文。</para></param>
    /// <param name="start">Zero-based page start. <para>从零起的页起点。</para></param>
    /// <param name="pageSize">Page size, capped at 100. <para>页大小，上限 100。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Devices of the page. <para>该页的设备。</para></returns>
    public static async Task<IReadOnlyList<AgcDevice>> ListDevicesAsync(HttpClient client, AgcCallContext context,
        int start, int pageSize, CancellationToken cancellationToken)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        var body = await SendAsync(client, HttpMethod.Get,
            new Uri($"{ApiOrigin}/api/cps/device-manage/v1/device/list?encodeFlag=0&start={start}&pageSize={pageSize}"),
            context, null, cancellationToken).ConfigureAwait(false);
        return ParseDevices(body.RawBody);
    }

    /// <summary>
    /// Registers one device under the team. <para>在团队下注册一台设备。</para></summary>
    /// <param name="client">HTTP client. <para>HTTP 客户端。</para></param>
    /// <param name="context">Authorization context including the team id.
    /// <para>含团队 ID 的授权上下文。</para></param>
    /// <param name="deviceName">Human-readable device name. <para>设备可读名。</para></param>
    /// <param name="udid">Device UDID. <para>设备 UDID。</para></param>
    /// <param name="deviceType">Device type code. <para>设备类型码。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The raw response for diagnostics. <para>供诊断的原始响应。</para></returns>
    public static Task<AgcRawResponse> AddDeviceAsync(HttpClient client, AgcCallContext context,
        string deviceName, string udid, string deviceType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(udid);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceType);
        var payload = JsonSerializer.Serialize(new AgcDevicePayload(deviceName, udid, deviceType), AgcJsonContext.Default.AgcDevicePayload);
        return SendAsync(client, HttpMethod.Post,
            new Uri($"{ApiOrigin}/api/cps/device-manage/v1/device/add"),
            context, payload, cancellationToken);
    }

    /// <summary>
    /// Lists the signing certificates of a team. <para>列出团队的签名证书。</para></summary>
    /// <param name="client">HTTP client. <para>HTTP 客户端。</para></param>
    /// <param name="context">Authorization context including the team id.
    /// <para>含团队 ID 的授权上下文。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Certificates in service order. <para>按服务顺序的证书。</para></returns>
    public static async Task<IReadOnlyList<AgcCertificate>> ListCertificatesAsync(HttpClient client,
        AgcCallContext context, CancellationToken cancellationToken)
    {
        var body = await SendAsync(client, HttpMethod.Get,
            new Uri($"{ApiOrigin}/api/cps/harmony-cert-manage/v1/cert/list"),
            context, null, cancellationToken).ConfigureAwait(false);
        return ParseCertificates(body.RawBody);
    }

    /// <summary>
    /// Applies for a signing certificate from the supplied CSR. <para>用给定 CSR 申请签名证书。</para></summary>
    /// <param name="client">HTTP client. <para>HTTP 客户端。</para></param>
    /// <param name="context">Authorization context including the team id.
    /// <para>含团队 ID 的授权上下文。</para></param>
    /// <param name="csrPem">CSR content in PEM form. <para>PEM 形式的 CSR 内容。</para></param>
    /// <param name="certName">Deterministic certificate name, e.g. <c>auto_debug_&lt;teamId&gt;.cer</c>.
    /// <para>确定性证书名，例如 <c>auto_debug_&lt;teamId&gt;.cer</c>。</para></param>
    /// <param name="certType">Certificate type, <c>debug</c> for debugging.
    /// <para>证书类型，调试用 <c>debug</c>。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The raw response carrying any download URL. <para>含下载地址的原始响应。</para></returns>
    public static Task<AgcRawResponse> AddCertificateAsync(HttpClient client, AgcCallContext context,
        string csrPem, string certName, string certType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csrPem);
        ArgumentException.ThrowIfNullOrWhiteSpace(certName);
        var payload = JsonSerializer.Serialize(new AgcCertificatePayload(csrPem, certName, certType), AgcJsonContext.Default.AgcCertificatePayload);
        return SendAsync(client, HttpMethod.Post,
            new Uri($"{ApiOrigin}/api/cps/harmony-cert-manage/v1/cert/add"),
            context, payload, cancellationToken);
    }

    /// <summary>
    /// Applies for a debug provisioning profile binding the certificate and device list.
    /// <para>申请绑定证书与设备列表的调试 Profile。</para></summary>
    /// <param name="client">HTTP client. <para>HTTP 客户端。</para></param>
    /// <param name="context">Authorization context including the team id.
    /// <para>含团队 ID 的授权上下文。</para></param>
    /// <param name="certificateIds">Certificate ids to bind. <para>需绑定的证书 ID。</para></param>
    /// <param name="packageName">Application package name. <para>应用包名。</para></param>
    /// <param name="provisionName">Deterministic profile name. <para>确定性 Profile 名。</para></param>
    /// <param name="deviceIds">All cloud device ids the debug profile authorizes.
    /// <para>调试 Profile 授权的全部云端设备 ID。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The raw response carrying the profile file URL. <para>含 Profile 文件地址的原始响应。</para></returns>
    public static Task<AgcRawResponse> AddDebugProvisionAsync(HttpClient client, AgcCallContext context,
        IReadOnlyList<string> certificateIds, string packageName, string provisionName,
        IReadOnlyList<string> deviceIds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionName);
        if (certificateIds.Count == 0)
        {
            throw new ArgumentException("At least one certificate id is required. 至少需要一个证书 ID。", nameof(certificateIds));
        }

        var payload = JsonSerializer.Serialize(new AgcProvisionPayload(certificateIds, packageName, provisionName, deviceIds, []), AgcJsonContext.Default.AgcProvisionPayload);
        return SendAsync(client, HttpMethod.Post,
            new Uri($"{ApiOrigin}/api/cps/provision-manage/v1/ide/test/provision/add"),
            context, payload, cancellationToken);
    }

    /// <summary>
    /// Exchanges a material object URL for a fresh signed download URL.
    /// <para>把材料对象地址换取为新的签名下载地址。</para></summary>
    /// <param name="client">HTTP client. <para>HTTP 客户端。</para></param>
    /// <param name="context">Authorization context. <para>授权上下文。</para></param>
    /// <param name="objectUrl">Object URL returned by the apply calls. <para>申请类调用返回的对象地址。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The fresh signed URL, or <see langword="null"/> when the response carries none.
    /// <para>新的签名地址；响应不含时为 <see langword="null"/>。</para></returns>
    public static async Task<string?> ReapplyUrlAsync(HttpClient client, AgcCallContext context, string objectUrl,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectUrl);
        var payload = JsonSerializer.Serialize(new AgcReapplyPayload(objectUrl), AgcJsonContext.Default.AgcReapplyPayload);
        var body = await SendAsync(client, HttpMethod.Post,
            new Uri($"{ApiOrigin}/api/amis/app-manage/v1/objects/url/reapply"),
            context, payload, cancellationToken).ConfigureAwait(false);
        return FindStringValue(body.RawBody, "reapplyUrl")
            ?? FindStringValue(body.RawBody, "signedUrl")
            ?? FindStringValue(body.RawBody, "url");
    }

    /// <summary>
    /// Downloads a material file from a signed URL after the SSRF guard.
    /// <para>经 SSRF 护栏后从签名地址下载材料文件。</para></summary>
    /// <param name="client">HTTP client. <para>HTTP 客户端。</para></param>
    /// <param name="signedUrl">Fresh signed URL. <para>新的签名地址。</para></param>
    /// <param name="maximumBytes">Download size cap. <para>下载大小上限。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The file bytes. <para>文件字节。</para></returns>
    /// <exception cref="InvalidOperationException">The URL fails the SSRF guard or the download exceeds the cap.
    /// <para>地址未通过 SSRF 护栏或下载超出上限。</para></exception>
    public static async Task<byte[]> DownloadAsync(HttpClient client, string signedUrl, long maximumBytes,
        CancellationToken cancellationToken)
    {
        var url = new Uri(signedUrl);
        EnsureExternalHttps(url);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadBoundedAsync(stream, Math.Min(maximumBytes, MaximumResponseBytes), cancellationToken)
            .ConfigureAwait(false);
    }

    // ── envelope and payload parsing (pure, offline-testable) ──────────────

    /// <summary>
    /// Parses the AGC envelope; throws when <c>ret.code</c> is absent or non-zero.
    /// <para>解析 AGC 信封；<c>ret.code</c> 缺失或非零时抛出。</para>
    /// </summary>
    /// <param name="body">Response body bytes. <para>响应体字节。</para></param>
    /// <returns>The parsed envelope. <para>解析后的信封。</para></returns>
    /// <exception cref="InvalidDataException">The body is not JSON or carries no envelope.
    /// <para>响应体不是 JSON 或不含信封。</para></exception>
    /// <exception cref="AgcServiceException">The envelope reports a non-zero code.
    /// <para>信封报告非零码。</para></exception>
    public static AgcRet ParseEnvelope(ReadOnlySpan<byte> body)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ret", out var ret))
        {
            throw new InvalidDataException("The AGC response carries no ret envelope. AGC 响应不含 ret 信封。");
        }

        var code = ret.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsed)
            ? parsed
            : -1;
        var message = ret.TryGetProperty("msg", out var messageElement)
            ? messageElement.ValueKind switch
            {
                JsonValueKind.String => messageElement.GetString() ?? string.Empty,
                JsonValueKind.Object or JsonValueKind.Array => messageElement.GetRawText(),
                _ => string.Empty,
            }
            : string.Empty;
        if (code != AgcRet.Success)
        {
            throw new AgcServiceException(code, message);
        }

        return new AgcRet(code, message);
    }

    /// <summary>Parses the team list from a response body. <para>从响应体解析团队列表。</para></summary>
    /// <param name="body">Response body bytes. <para>响应体字节。</para></param>
    /// <returns>Teams found by key search. <para>按键搜索找到的团队。</para></returns>
    public static IReadOnlyList<AgcTeam> ParseTeams(ReadOnlySpan<byte> body)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        var teams = new List<AgcTeam>();
        CollectObjects(document.RootElement, static element => element.TryGetProperty("teamId", out _), element =>
        {
            var teamId = element.TryGetProperty("teamId", out var id) && id.ValueKind is JsonValueKind.String
                ? id.GetString() ?? string.Empty
                : id.ValueKind is JsonValueKind.Number ? id.GetRawText() : string.Empty;
            if (teamId.Length == 0)
            {
                return;
            }

            var name = GetString(element, "teamName");
            if (name.Length == 0)
            {
                name = GetString(element, "name");
            }

            teams.Add(new AgcTeam(teamId, name));
        });
        return teams;
    }

    /// <summary>Parses the device list from a response body. <para>从响应体解析设备列表。</para></summary>
    /// <param name="body">Response body bytes. <para>响应体字节。</para></param>
    /// <returns>Devices found by key search. <para>按键搜索找到的设备。</para></returns>
    public static IReadOnlyList<AgcDevice> ParseDevices(ReadOnlySpan<byte> body)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        var devices = new List<AgcDevice>();
        CollectObjects(document.RootElement, static element => element.TryGetProperty("udid", out _), element =>
        {
            var udid = GetString(element, "udid");
            if (udid.Length == 0)
            {
                return;
            }

            devices.Add(new AgcDevice(
                GetString(element, "deviceId"),
                udid,
                GetString(element, "deviceName"),
                GetString(element, "deviceType")));
        });
        return devices;
    }

    /// <summary>Parses the certificate list from a response body. <para>从响应体解析证书列表。</para></summary>
    /// <param name="body">Response body bytes. <para>响应体字节。</para></param>
    /// <returns>Certificates found by key search. <para>按键搜索找到的证书。</para></returns>
    public static IReadOnlyList<AgcCertificate> ParseCertificates(ReadOnlySpan<byte> body)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        var certificates = new List<AgcCertificate>();
        CollectObjects(document.RootElement, static element => element.TryGetProperty("certId", out _), element =>
        {
            var certId = GetString(element, "certId");
            if (certId.Length == 0)
            {
                return;
            }

            certificates.Add(new AgcCertificate(
                certId,
                GetString(element, "certName"),
                GetString(element, "certType")));
        });
        return certificates;
    }

    /// <summary>
    /// Parses the token-exchange or token-check response; returns <see langword="null"/> when no access token is
    /// present. <para>解析令牌交换或复核响应；无访问令牌时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="body">Response body bytes. <para>响应体字节。</para></param>
    /// <returns>The session identity or <see langword="null"/>. <para>会话身份或 <see langword="null"/>。</para></returns>
    public static AgcSession? ParseTokenResponse(ReadOnlySpan<byte> body)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        string? accessToken = null;
        string? userId = null;
        string? userName = null;
        CollectObjects(document.RootElement, static _ => true, element =>
        {
            if (accessToken is null && TryFindString(element, "accessToken", out var token))
            {
                accessToken = token;
            }

            if (userId is null && (TryFindString(element, "userId", out var id) || TryFindString(element, "uid", out id)))
            {
                userId = id;
            }

            if (userName is null && (TryFindString(element, "userName", out var name) || TryFindString(element, "displayName", out name)))
            {
                userName = name;
            }
        });
        return string.IsNullOrEmpty(accessToken) ? null : new AgcSession(accessToken!, userId ?? string.Empty, userName ?? string.Empty);
    }

    /// <summary>Searches the body for the first string value under the given key.
    /// <para>在响应体中按键搜索首个字符串值。</para></summary>
    /// <param name="body">Response body bytes. <para>响应体字节。</para></param>
    /// <param name="key">Key to search. <para>搜索的键。</para></param>
    /// <returns>The string value or <see langword="null"/>. <para>字符串值或 <see langword="null"/>。</para></returns>
    public static string? FindStringValue(ReadOnlySpan<byte> body, string key)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        return TryFindString(document.RootElement, key, out var value) ? value : null;
    }

    private static bool TryFindString(JsonElement element, string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        if (element.ValueKind is JsonValueKind.Object
            && element.TryGetProperty(key, out var property)
            && property.ValueKind is JsonValueKind.String)
        {
            value = property.GetString();
            return !string.IsNullOrEmpty(value);
        }

        value = null;
        return false;
    }

    private static void CollectObjects(JsonElement element, Func<JsonElement, bool> predicate,
        Action<JsonElement> visit)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (predicate(element))
                {
                    visit(element);
                }

                foreach (var property in element.EnumerateObject())
                {
                    CollectObjects(property.Value, predicate, visit);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectObjects(item, predicate, visit);
                }

                break;
        }
    }

    private static string GetString(JsonElement element, string key)
        => element.TryGetProperty(key, out var property) && property.ValueKind is JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : property.ValueKind is JsonValueKind.Number ? property.GetRawText() : string.Empty;

    private static void EnsureExternalHttps(Uri url)
    {
        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Material downloads require HTTPS. 材料下载必须使用 HTTPS。");
        }

        if (IsInternalHost(url.Host))
        {
            throw new InvalidOperationException($"The material URL host {url.Host} is not an external host. 材料地址主机 {url.Host} 不是外部主机。");
        }
    }

    private static bool IsInternalHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Only IP literals are inspected; host names resolve server side and pass the guard.
        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] is 127 or 10
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168;
        }

        return address.IsIPv6LinkLocal || address.Equals(IPAddress.IPv6Loopback);
    }

    private static async Task<byte[]> ReadAsync(HttpClient client, Uri url, CancellationToken cancellationToken,
        string? accessToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(accessToken))
        {
            request.Headers.TryAddWithoutValidation("oauth2Token", accessToken);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadBoundedAsync(stream, MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AgcRawResponse> SendAsync(HttpClient client, HttpMethod method, Uri url,
        AgcCallContext context, string? jsonPayload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(context.AccessToken))
        {
            request.Headers.TryAddWithoutValidation("oauth2Token", context.AccessToken);
        }

        if (!string.IsNullOrEmpty(context.UserId))
        {
            request.Headers.TryAddWithoutValidation("uid", context.UserId);
        }

        if (!string.IsNullOrEmpty(context.TeamId))
        {
            request.Headers.TryAddWithoutValidation("teamId", context.TeamId);
        }

        if (jsonPayload is not null)
        {
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var body = await ReadBoundedAsync(stream, MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        var ret = ParseEnvelope(body);
        return new AgcRawResponse(ret, body);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[64 << 10];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidOperationException($"The response exceeds {maximumBytes} bytes. 响应超过 {maximumBytes} 字节。");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>Thrown when an AGC envelope reports a non-zero result code.
/// <para>AGC 信封报告非零结果码时抛出。</para></summary>
/// <param name="code">Envelope result code. <para>信封结果码。</para></param>
/// <param name="message">Envelope result message. <para>信封结果消息。</para></param>
public sealed class AgcServiceException(int code, string message)
    : InvalidOperationException($"AGC call failed (ret.code={code}): {message}  AGC 调用失败（ret.code={code}）：{message}")
{
    /// <summary>Gets the envelope result code. <para>信封结果码。</para></summary>
    public int Code { get; } = code;

    /// <summary>Gets the envelope result message. <para>信封结果消息。</para></summary>
    public string EnvelopeMessage { get; } = message;
}