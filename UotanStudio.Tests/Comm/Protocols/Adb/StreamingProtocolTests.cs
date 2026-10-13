using UotanStudio.Comm.Protocols.Adb;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Adb;

/// <summary>Regression tests for incremental communication-protocol framing.<para>增量通信协议分帧的回归测试。</para></summary>
public class StreamingProtocolTests
{
    [Fact]
    public void AdbShellParser_PreservesSplitHeaderAndPayload()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var frame = new byte[] { (byte)ShellFrameKind.Stdout }
            .Concat(BitConverter.GetBytes(payload.Length)).Concat(payload).ToArray();
        var parser = new AdbShellClient.FrameParser();

        Assert.Empty(parser.Feed(frame[..2]));
        Assert.Empty(parser.Feed(frame[2..7]));
        var parsed = Assert.Single(parser.Feed(frame[7..]));

        Assert.Equal(ShellFrameKind.Stdout, parsed.Kind);
        Assert.Equal(payload, parsed.Payload);
        parser.Complete();
    }

    [Fact]
    public void AdbShellParser_RejectsTruncatedStream()
    {
        var parser = new AdbShellClient.FrameParser();
        parser.Feed([(byte)ShellFrameKind.Stdout, 4, 0, 0, 0, 1]);

        Assert.Throws<InvalidDataException>(() => parser.Complete());
    }

    [Fact]
    public void AdbShellParser_HandlesOneByteFeedsAndReusesStorage()
    {
        var payload = Enumerable.Range(0, 4096).Select(value => (byte)value).ToArray();
        var frame = new byte[] { (byte)ShellFrameKind.Stdout }
            .Concat(BitConverter.GetBytes(payload.Length)).Concat(payload).ToArray();
        var parser = new AdbShellClient.FrameParser();
        var parsed = new List<ShellFrame>();

        foreach (var value in frame)
        {
            parsed.AddRange(parser.Feed([value]));
        }

        Assert.Equal(payload, Assert.Single(parsed).Payload);
        parser.Complete();
    }
}