using System.Text;
using UotanStudio.Net.Agc;
using Xunit;

namespace UotanStudio.Tests.Net;

/// <summary>
/// Verifies the tolerant AGC envelope and payload parsing plus the download SSRF guard, all offline.
/// <para>离线验证 AGC 信封与负载的容忍解析，以及下载 SSRF 护栏。</para>
/// </summary>
public sealed class AgcClientTests
{
    [Fact]
    public void ParseEnvelope_AcceptsSuccessAndThrowsOnFailure()
    {
        var success = Encoding.UTF8.GetBytes("{\"ret\":{\"code\":0,\"msg\":\"ok\"},\"value\":{}}");
        var ret = AgcClient.ParseEnvelope(success);
        Assert.Equal(0, ret.Code);

        var failure = Encoding.UTF8.GetBytes("{\"ret\":{\"code\":10019,\"msg\":\"profile verify failed\"}}");
        var exception = Assert.Throws<AgcServiceException>(() => AgcClient.ParseEnvelope(failure));
        Assert.Equal(10019, exception.Code);
        Assert.Contains("profile verify failed", exception.EnvelopeMessage, StringComparison.Ordinal);

        // msg may arrive as a JSON-encoded object rather than a plain string.
        var objectMessage = Encoding.UTF8.GetBytes("{\"ret\":{\"code\":1,\"msg\":{\"detail\":\"bad\"}}}");
        var objectException = Assert.Throws<AgcServiceException>(() => AgcClient.ParseEnvelope(objectMessage));
        Assert.Contains("\"bad\"", objectException.EnvelopeMessage, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => AgcClient.ParseEnvelope("{\"noEnvelope\":true}"u8.ToArray()));
    }

    [Fact]
    public void ParseTeams_FindsNestedEntries()
    {
        var body = Encoding.UTF8.GetBytes("""
            {"ret":{"code":0},"value":{"list":[{"teamId":"2850086000502216751","teamName":"main"},{"nested":{"teamId":"42"}}]}}
            """);
        var teams = AgcClient.ParseTeams(body);
        Assert.Equal(2, teams.Count);
        Assert.Equal("2850086000502216751", teams[0].TeamId);
        Assert.Equal("main", teams[0].Name);
        Assert.Equal("42", teams[1].TeamId);
    }

    [Fact]
    public void ParseDevices_FindsNestedEntries()
    {
        var body = Encoding.UTF8.GetBytes("""
            {"ret":{"code":0},"value":{"devices":[{"deviceId":"d1","udid":"CE4A","deviceName":"phone","deviceType":"4"}]}}
            """);
        var devices = AgcClient.ParseDevices(body);
        var device = Assert.Single(devices);
        Assert.Equal("d1", device.DeviceId);
        Assert.Equal("CE4A", device.Udid);
        Assert.Equal("4", device.DeviceType);
    }

    [Fact]
    public void ParseCertificates_FindsNestedEntries()
    {
        var body = Encoding.UTF8.GetBytes("""
            {"ret":{"code":0},"value":{"certs":[{"certId":"c1","certName":"auto_debug_42.cer","certType":"debug"}]}}
            """);
        var certificates = AgcClient.ParseCertificates(body);
        var certificate = Assert.Single(certificates);
        Assert.Equal("c1", certificate.CertId);
        Assert.Equal("auto_debug_42.cer", certificate.CertName);
    }

    [Fact]
    public void ParseTokenResponse_ExtractsIdentityRecursively()
    {
        var body = Encoding.UTF8.GetBytes("""
            {"ret":{"code":0},"userInfo":{"userId":"u-1","displayName":"Zi","accessToken":"jwt-value"}}
            """);
        var session = AgcClient.ParseTokenResponse(body);
        Assert.NotNull(session);
        Assert.Equal("jwt-value", session.AccessToken);
        Assert.Equal("u-1", session.UserId);
        Assert.Equal("Zi", session.UserName);

        Assert.Null(AgcClient.ParseTokenResponse("{\"ret\":{\"code\":0}}"u8.ToArray()));
    }

    [Fact]
    public void BuildLoginUrl_CarriesPortAppIdAndCode()
    {
        var url = AgcClient.BuildLoginUrl(53412, "abc123");
        Assert.Contains("port=53412", url, StringComparison.Ordinal);
        Assert.Contains("appid=1009", url, StringComparison.Ordinal);
        Assert.Contains("code=abc123", url, StringComparison.Ordinal);
        Assert.StartsWith(AgcClient.AuthOrigin, url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAsync_RejectsInternalAndPlainHttpUrls()
    {
        using var client = new HttpClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgcClient.DownloadAsync(
            client, "http://connect-api.cloud.huawei.com/material", 1024, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgcClient.DownloadAsync(
            client, "https://127.0.0.1/material", 1024, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgcClient.DownloadAsync(
            client, "https://10.1.2.3/material", 1024, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgcClient.DownloadAsync(
            client, "https://192.168.1.5/material", 1024, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgcClient.DownloadAsync(
            client, "https://localhost/material", 1024, TestContext.Current.CancellationToken));
    }
}