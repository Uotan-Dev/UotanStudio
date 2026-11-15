using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UotanStudio.Net.Agc;

namespace UotanStudio.Services.Harmony;

/// <summary>Persisted login session for the AGC services. <para>AGC 服务的持久化登录会话。</para></summary>
/// <param name="AccessToken">Long-lived access token. <para>长效访问令牌。</para></param>
/// <param name="UserId">Account user id. <para>账号用户 ID。</para></param>
/// <param name="UserName">Account display name. <para>账号显示名。</para></param>
/// <param name="CreatedAtUtc">Creation timestamp. <para>创建时间。</para></param>
public sealed record AgcSessionState(string AccessToken, string UserId, string UserName, DateTimeOffset CreatedAtUtc);

/// <summary>
/// Manages the AGC login session: opens the DevEco-style browser login with a one-shot loopback callback, exchanges
/// the returned <c>tempToken</c> for a long-lived JWT, and persists it under the application data root.
/// <para>管理 AGC 登录会话：以 DevEco 风格打开浏览器登录并用一次性回环回调接住结果，换取长效 JWT，
/// 持久化到应用数据根之下。</para>
/// </summary>
/// <remarks>
/// The listener accepts a single callback whose query must carry <c>tempToken</c> and <c>siteId=1</c> (mainland
/// site), mirroring the official flow. The session file holds only the JWT and identity — no account password is
/// ever stored, and the token can be revoked from the Huawei account page.
/// <para>监听器只接受一次回调，查询串必须携带 <c>tempToken</c> 与 <c>siteId=1</c>（大陆站点），与官方流程一致。
/// 会话文件只存 JWT 与身份——从不保存账号密码，令牌可在华为账号页注销。</para>
/// </remarks>
internal sealed class AgcSessionService
{
    private static readonly string SessionPath =
        Path.Combine(AppPaths.Root, "harmony", "session.json");

    private static readonly Lock LoadLock = new();

    /// <summary>
    /// Returns a valid session, re-validating a persisted token online when present; otherwise starts the browser
    /// login. <para>返回有效会话；已有持久化令牌时先在线复核，否则启动浏览器登录。</para></summary>
    /// <param name="client">HTTP client. <para>HTTP 客户端。</para></param>
    /// <param name="progress">Progress reporter. <para>进度上报。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A valid session. <para>有效会话。</para></returns>
    public async Task<AgcSessionState> GetValidSessionAsync(HttpClient client, IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        var stored = LoadSession();
        if (stored is not null)
        {
            progress?.Report("Re-validating the stored AGC session  复核已存的 AGC 会话");
            try
            {
                var checkedSession = await AgcClient.CheckTokenAsync(client, stored.AccessToken, cancellationToken)
                    .ConfigureAwait(false);
                return stored with { UserName = checkedSession.UserName, UserId = checkedSession.UserId };
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException)
            {
                progress?.Report("The stored AGC session expired; signing in again  已存 AGC 会话失效，重新登录");
            }
        }

        return await LoginAsync(client, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the browser login and returns the fresh session.
    /// <para>运行浏览器登录并返回新会话。</para></summary>
    /// <param name="client">HTTP client. <para>HTTP 客户端。</para></param>
    /// <param name="progress">Progress reporter. <para>进度上报。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A valid session. <para>有效会话。</para></returns>
    /// <exception cref="InvalidOperationException">The callback never arrives or carries an unsupported site.
    /// <para>回调未到达或站点不受支持。</para></exception>
    public async Task<AgcSessionState> LoginAsync(HttpClient client, IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var code = Guid.NewGuid().ToString("N");
            var loginUrl = AgcClient.BuildLoginUrl(port, code);
            progress?.Report("Waiting for the Huawei account login in the browser  等待浏览器完成华为账号登录");
            ExternalLinkService.TryOpen(loginUrl);

            var callback = await AcceptCallbackAsync(listener, code, cancellationToken).ConfigureAwait(false);
            progress?.Report("Exchanging the login token  交换登录令牌");
            var session = await AgcClient.ExchangeTempTokenAsync(client, callback, cancellationToken)
                .ConfigureAwait(false);
            var state = new AgcSessionState(session.AccessToken, session.UserId, session.UserName, DateTimeOffset.UtcNow);
            SaveSession(state);
            return state;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Clears the persisted session. <para>清除持久化会话。</para></summary>
    public static void ClearSession()
    {
        try
        {
            File.Delete(SessionPath);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<string> AcceptCallbackAsync(TcpListener listener, string expectedCode,
        CancellationToken cancellationToken)
    {
        var deadline = TimeSpan.FromMinutes(5);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        while (!timeout.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The AGC login callback did not arrive in time. AGC 登录回调未在时限内到达。");
            }

            using (client)
            {
                var result = await HandleCallbackAsync(client, expectedCode, cancellationToken).ConfigureAwait(false);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException("The AGC login callback did not arrive in time. AGC 登录回调未在时限内到达。");
    }

    private static async Task<string?> HandleCallbackAsync(TcpClient client, string expectedCode,
        CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        if (remote is null || !IPAddress.IsLoopback(remote.Address))
        {
            return null;
        }

        var stream = client.GetStream();
        var buffer = new byte[16 << 10];
        var length = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        var request = Encoding.ASCII.GetString(buffer, 0, length);
        var lineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
        if (lineEnd < 0)
        {
            return null;
        }

        var requestLine = request[..lineEnd];
        var queryStart = requestLine.IndexOf('?');
        if (queryStart < 0)
        {
            await WriteResponseAsync(stream, "missing query", cancellationToken).ConfigureAwait(false);
            return null;
        }

        var query = requestLine[queryStart..];
        var values = System.Web.HttpUtility.ParseQueryString(query);
        var siteId = values.Get("siteId");
        if (siteId is not null && siteId != "1")
        {
            await WriteResponseAsync(stream, "unsupported site; mainland China site required", cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException("The AGC login returned an unsupported site; only the mainland China site is wired. " +
                "AGC 登录返回了不受支持的站点；当前仅支持大陆站点。");
        }

        var tempToken = values.Get("tempToken");
        if (string.IsNullOrWhiteSpace(tempToken))
        {
            await WriteResponseAsync(stream, "missing tempToken", cancellationToken).ConfigureAwait(false);
            return null;
        }

        await WriteResponseAsync(stream, "login complete; you can close this page", cancellationToken)
            .ConfigureAwait(false);
        return tempToken;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string message,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><meta charset=\"utf-8\"><body style=\"font-family:sans-serif\">{message}</body>");
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\n" +
            $"Content-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AgcSessionState? LoadSession()
    {
        lock (LoadLock)
        {
            try
            {
                if (!File.Exists(SessionPath))
                {
                    return null;
                }

                using var document = JsonDocument.Parse(File.ReadAllBytes(SessionPath));
                var root = document.RootElement;
                var token = root.TryGetProperty("accessToken", out var tokenElement) ? tokenElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(token))
                {
                    return null;
                }

                return new AgcSessionState(
                    token,
                    root.TryGetProperty("userId", out var userElement) ? userElement.GetString() ?? string.Empty : string.Empty,
                    root.TryGetProperty("userName", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty,
                    root.TryGetProperty("createdAtUtc", out var createdElement) && createdElement.TryGetDateTimeOffset(out var created)
                        ? created
                        : DateTimeOffset.MinValue);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static void SaveSession(AgcSessionState state)
    {
        lock (LoadLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
            var staged = SessionPath + ".tmp";
            using (var stream = File.Create(staged))
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                writer.WriteString("accessToken", state.AccessToken);
                writer.WriteString("userId", state.UserId);
                writer.WriteString("userName", state.UserName);
                writer.WriteString("createdAtUtc", state.CreatedAtUtc);
                writer.WriteEndObject();
            }

            File.Move(staged, SessionPath, overwrite: true);
        }
    }
}