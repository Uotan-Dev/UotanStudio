using System.Text;
using System.Text.Json;
using UotanStudio.Net.Agc;
using Xunit;

namespace UotanStudio.Tests.Net;

/// <summary>
/// Covers direct AGC parsing from nonzero memory slices and the retained span entry points.
/// <para>覆盖从非零内存切片直接解析 AGC，以及保留的 span 入口。</para>
/// </summary>
public sealed class Net11AgcClientTests
{
    [Fact]
    public void MemoryOverloads_NonzeroOffset_IgnoreSurroundingBytes()
    {
        var teamBody = Slice("""
            {"ret":{"code":0},"value":{"list":[{"teamId":"42","teamName":"main"},{"nested":{"teamId":43,"name":"numeric"}}]}}
            """);
        var teams = AgcClient.ParseTeams(teamBody);
        Assert.Collection(teams,
            team =>
            {
                Assert.Equal("42", team.TeamId);
                Assert.Equal("main", team.Name);
            },
            team =>
            {
                Assert.Equal("43", team.TeamId);
                Assert.Equal("numeric", team.Name);
            });

        var deviceBody = Slice("""
            {"value":{"groups":[{"devices":[{"deviceId":"d1","udid":"CE4A","deviceName":"phone","deviceType":"4"}]}]}}
            """);
        var device = Assert.Single(AgcClient.ParseDevices(deviceBody));
        Assert.Equal(new AgcDevice("d1", "CE4A", "phone", "4"), device);

        var certificateBody = Slice("""
            {"value":{"nested":[{"certId":"c1","certName":"auto_debug_42.cer","certType":"debug"}]}}
            """);
        var certificate = Assert.Single(AgcClient.ParseCertificates(certificateBody));
        Assert.Equal(new AgcCertificate("c1", "auto_debug_42.cer", "debug"), certificate);

        var tokenBody = Slice("""
            {"userInfo":{"identity":{"accessToken":"jwt-value","uid":"u-1","displayName":"Zi"}}}
            """);
        var session = AgcClient.ParseTokenResponse(tokenBody);
        Assert.Equal(new AgcSession("jwt-value", "u-1", "Zi"), session);
        Assert.Null(AgcClient.ParseTokenResponse(Slice("""{"ret":{"code":0}}""")));

        Assert.Equal("https://material.example/profile", AgcClient.FindStringValue(
            Slice("""{"signedUrl":"https://material.example/profile"}"""), "signedUrl"));
        Assert.Null(AgcClient.FindStringValue(Slice("""{"url":7}"""), "url"));
    }

    [Fact]
    public void ParseEnvelope_MemorySlice_PreservesEnvelopeSemantics()
    {
        var success = AgcClient.ParseEnvelope(Slice("""{"ret":{"code":0,"msg":"ok"}}"""));
        Assert.Equal(new AgcRet(0, "ok"), success);

        var failure = Assert.Throws<AgcServiceException>(() => AgcClient.ParseEnvelope(
            Slice("""{"ret":{"code":10019,"msg":{"detail":"profile verify failed"}}}""")));
        Assert.Equal(10019, failure.Code);
        Assert.Contains("profile verify failed", failure.EnvelopeMessage, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => AgcClient.ParseEnvelope(Slice("""{"noEnvelope":true}""")));
    }

    [Fact]
    public void MemoryOverloads_MalformedJsonOrUtf8_ThrowJsonException()
    {
        Action[] parsers =
        [
            () => AgcClient.ParseEnvelope(Slice("""{"ret":""")),
            () => AgcClient.ParseTeams(Slice("""{"value":[,]}""")),
            () => AgcClient.ParseDevices(Slice("""{"value":""")),
            () => AgcClient.ParseCertificates(Slice("""{"value":""")),
            () => AgcClient.ParseTokenResponse(Slice("""{"value":""")),
            () => AgcClient.FindStringValue(Slice("""{"value":"""), "url"),
        ];

        foreach (var parse in parsers)
        {
            Assert.ThrowsAny<JsonException>(parse);
        }

        ReadOnlyMemory<byte> invalidUtf8 = Slice([0xff]);
        Action[] utf8Parsers =
        [
            () => AgcClient.ParseEnvelope(invalidUtf8),
            () => AgcClient.ParseTeams(invalidUtf8),
            () => AgcClient.ParseDevices(invalidUtf8),
            () => AgcClient.ParseCertificates(invalidUtf8),
            () => AgcClient.ParseTokenResponse(invalidUtf8),
            () => AgcClient.FindStringValue(invalidUtf8, "url"),
        ];

        foreach (var parse in utf8Parsers)
        {
            Assert.ThrowsAny<JsonException>(parse);
        }
    }

    [Fact]
    public void SpanEntryPoints_MemorySlices_RemainCompatible()
    {
        byte[] body =
        [
            0xff, 0xff, 0xff, 0xff, 0xff,
            .. Encoding.UTF8.GetBytes("""{"ret":{"code":0},"teamId":"42","udid":"CE4A","certId":"c1","accessToken":"jwt"}"""),
            0xff, 0xff, 0xff, 0xff, 0xff,
        ];
        ReadOnlySpan<byte> span = body.AsSpan(5, body.Length - 10);

        Assert.Equal(AgcClient.ParseEnvelope(body.AsMemory(5, body.Length - 10)), AgcClient.ParseEnvelope(span));
        Assert.Equal(AgcClient.ParseTeams(body.AsMemory(5, body.Length - 10)), AgcClient.ParseTeams(span));
        Assert.Equal(AgcClient.ParseDevices(body.AsMemory(5, body.Length - 10)), AgcClient.ParseDevices(span));
        Assert.Equal(AgcClient.ParseCertificates(body.AsMemory(5, body.Length - 10)), AgcClient.ParseCertificates(span));
        Assert.Equal(AgcClient.ParseTokenResponse(body.AsMemory(5, body.Length - 10)), AgcClient.ParseTokenResponse(span));
        Assert.Equal(AgcClient.FindStringValue(body.AsMemory(5, body.Length - 10), "teamId"),
            AgcClient.FindStringValue(span, "teamId"));

        Assert.ThrowsAny<JsonException>(() => AgcClient.ParseEnvelope("{"u8));
        Assert.ThrowsAny<JsonException>(() => AgcClient.FindStringValue("{"u8, "url"));
    }

    private static ReadOnlyMemory<byte> Slice(string json)
        => Slice(Encoding.UTF8.GetBytes(json));

    private static ReadOnlyMemory<byte> Slice(byte[] bytes)
    {
        var storage = new byte[bytes.Length + 13];
        storage.AsSpan().Fill(0xff);
        bytes.CopyTo(storage, 7);
        return storage.AsMemory(7, bytes.Length);
    }
}