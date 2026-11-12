using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Drives a full EDL initialization through a program that refuses configure until the signature exchange
/// completes, covering both the signature-present and signature-absent outcomes.
/// <para>通过一个在签名交换完成前拒绝 configure 的程序驱动完整 EDL 初始化，覆盖有签名与无签名两种结果。</para>
/// </summary>
public sealed class EdlSessionVendorAuthTests
{
    private const string AuthenticationRefusal =
        "ERROR: Only nop and sig tag can be recevied before authentication.";

    private static byte[] Document(string body) => Encoding.UTF8.GetBytes(
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<data>\n{body}\n</data>\n");

    private static byte[] ConfigureAck() => Document(
        "<response value=\"ACK\" MemoryName=\"UFS\" MaxPayloadSizeToTargetInBytes=\"1048576\" " +
        "MaxPayloadSizeToTargetInBytesSupported=\"1048576\" MaxPayloadSizeFromTargetInBytes=\"4096\" />");

    private static FirehoseConfiguration Configuration() => new()
    {
        AutoDetectSectorSize = false,
        SectorSize = 512,
        ResponseTimeoutSeconds = 1,
        ConfigureRetryIntervalMs = 1,
        ConfigureResponseTimeoutMs = 500,
        ConfigureFirstAttemptTimeoutMs = 500
    };

    private static byte[] Signature()
    {
        var signature = new byte[FirehoseProtocol.VendorSignatureLength];
        for (var index = 0; index < signature.Length; index++)
        {
            signature[index] = (byte)index;
        }

        return signature;
    }

    private sealed class AuthenticationState
    {
        public int Configures { get; set; }

        public int Signatures { get; set; }

        public bool Authenticated { get; set; }
    }

    private static ScriptedCommTransport CreateTransport(AuthenticationState state)
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(Document("<response value=\"NAK\" />"));
        transport.OnWrite = bytes =>
        {
            if (bytes.Length == FirehoseProtocol.VendorSignatureLength && bytes[0] != (byte)'<')
            {
                state.Signatures++;
                state.Authenticated = true;
                return
                [
                    Document("<log value=\"INFO: Target authenticated\" />"),
                    Document("<response value=\"ACK\" />")
                ];
            }

            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("<configure", StringComparison.Ordinal))
            {
                state.Configures++;
                return state.Authenticated
                    ? [ConfigureAck()]
                    : [Document($"<log value=\"{AuthenticationRefusal}\" />"), Document("<response value=\"NAK\" />")];
            }

            if (text.Contains("<sig", StringComparison.Ordinal))
            {
                return [Document("<response value=\"ACK\" rawmode=\"true\" />")];
            }

            return [Document("<response value=\"NAK\" />")];
        };
        return transport;
    }

    [Fact]
    public async Task Initialize_AcceptedSignature_ConfiguresAfterAuthentication()
    {
        var state = new AuthenticationState();
        var transport = CreateTransport(state);
        using var session = new EdlSession(transport, Configuration());
        var options = new EdlInitializationOptions { VendorAuthSignature = Signature() };

        await session.InitializeAsync(null, options, TestContext.Current.CancellationToken);

        Assert.True(session.IsConfigured);
        Assert.False(session.VendorAuthenticationRequired);
        Assert.Equal(2, state.Configures);
        Assert.Equal(1, state.Signatures);
    }

    [Fact]
    public async Task Initialize_NoSignature_ReportsTypedRefusalAfterOneConfigure()
    {
        var state = new AuthenticationState();
        var transport = CreateTransport(state);
        using var session = new EdlSession(transport, Configuration());

        var exception = await Assert.ThrowsAsync<EdlException>(
            () => session.InitializeAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.VendorAuthenticationRequired, exception.ErrorCode);
        Assert.False(session.IsConfigured);
        Assert.True(session.VendorAuthenticationRequired);
        Assert.Equal(1, state.Configures);
        Assert.Equal(0, state.Signatures);
    }
}