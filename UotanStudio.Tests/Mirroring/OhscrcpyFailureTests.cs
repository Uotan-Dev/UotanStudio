using System.Net.Sockets;
using UotanStudio.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

public sealed class OhscrcpyFailureTests
{
    [Fact]
    public void IsConnectionFailure_SocketHttpAndForwardErrors_ReturnsTrue()
    {
        Assert.True(OhscrcpyFailure.IsConnectionFailure(
            new SocketException((int)SocketError.ConnectionRefused)));
        Assert.True(OhscrcpyFailure.IsConnectionFailure(
            new HttpRequestException("An error occurred while sending the request.")));
        Assert.True(OhscrcpyFailure.IsConnectionFailure(
            new IOException("outer", new SocketException((int)SocketError.ConnectionReset))));
        Assert.True(OhscrcpyFailure.IsConnectionFailure(
            new IOException("The HDC forward on 127.0.0.1:5000 did not accept the connection within 20000 ms.")));
    }

    [Fact]
    public void IsConnectionFailure_CapabilityRejections_ReturnsFalse()
    {
        Assert.False(OhscrcpyFailure.IsConnectionFailure(
            new NotSupportedException("the running capture library does not implement /ScrcpyService/onStart")));
        Assert.False(OhscrcpyFailure.IsConnectionFailure(
            new IOException("The device gRPC call /ScrcpyService/onStart failed with HTTP 501.")));
        Assert.False(OhscrcpyFailure.IsConnectionFailure(
            new InvalidDataException("The device gRPC video stream produced no H.264 access unit.")));
    }

    [Fact]
    public void IsCapabilityFailure_UnimplementedStatus_ReturnsTrue_AndTransientErrorReturnsFalse()
    {
        Assert.True(OhscrcpyFailure.IsCapabilityFailure(
            new IOException("The device gRPC call /ScrcpyService/onStart failed with HTTP 404.")));
        Assert.True(OhscrcpyFailure.IsCapabilityFailure(new NotSupportedException("unsupported")));
        Assert.False(OhscrcpyFailure.IsCapabilityFailure(
            new HttpRequestException("An error occurred while sending the request.")));
    }

    [Theory]
    [InlineData("dlopen failed: cannot locate symbol \"grpc_...\"", true)]
    [InlineData("libgrpc.z.so: undefined symbol: _ZN4grpc...", true)]
    [InlineData("cannot find library libprotobuf.z.so", true)]
    [InlineData("capture extension started and is serving", false)]
    [InlineData("", false)]
    public void LooksLikeAbiIncompatibility_LogText_MatchesLoaderMarkers(string log, bool expected)
    {
        Assert.Equal(expected, OhscrcpyFailure.LooksLikeAbiIncompatibility(log));
    }

    [Fact]
    public void WrapVideoFailure_LoaderLog_ReportsUnsupportedWithoutRetrying()
    {
        var wrapped = OhscrcpyFailure.WrapVideoFailure(
            new IOException("The connection was closed."),
            "dlopen failed: cannot locate symbol \"_ZN4grpc6StatusD2Ev\"");

        var error = Assert.IsType<NotSupportedException>(wrapped);
        Assert.Contains(OhscrcpyFailure.CapabilityPrefix, error.Message, StringComparison.Ordinal);
        Assert.Contains("dlopen", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrapVideoFailure_ConnectionError_ReportsRetryableVideoFailure()
    {
        var cause = new HttpRequestException("An error occurred while sending the request.");
        var wrapped = OhscrcpyFailure.WrapVideoFailure(cause, "capture extension started");

        var error = Assert.IsType<IOException>(wrapped);
        Assert.Contains(OhscrcpyFailure.VideoPrefix, error.Message, StringComparison.Ordinal);
        Assert.Same(cause, error.InnerException);
    }

    [Theory]
    [InlineData(404, true)]
    [InlineData(405, true)]
    [InlineData(501, true)]
    [InlineData(500, false)]
    [InlineData(200, false)]
    public void IsUnimplementedStatus_HttpStatus_MatchesUnimplementedCodes(int status, bool expected)
    {
        Assert.Equal(expected, OhscrcpyFailure.IsUnimplementedStatus(status));
    }

    [Fact]
    public void Snippet_EmptyLog_RendersPlaceholderAndBoundsLongText()
    {
        Assert.Equal("(empty)", OhscrcpyFailure.Snippet("   "));
        var longText = new string('x', 2_000);
        var snippet = OhscrcpyFailure.Snippet(longText);
        Assert.True(snippet.Length < longText.Length);
        Assert.EndsWith("…", snippet, StringComparison.Ordinal);
    }
}