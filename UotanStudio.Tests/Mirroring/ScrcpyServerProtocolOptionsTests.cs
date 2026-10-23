using System.Buffers.Binary;
using UotanStudio.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

public sealed class ScrcpyServerProtocolOptionsTests
{
    [Fact]
    public void BuildArguments_FirstPositionIsTheDeployedJarVersion()
    {
        var options = new ScrcpyServerOptions { ServerVersion = "5.0.1", Control = false };

        var args = options.BuildArguments();

        Assert.Equal("5.0.1", args[0]);
        Assert.DoesNotContain("2.7", args, StringComparer.Ordinal);
    }

    [Fact]
    public void BuildArguments_AnnoptsFiveXStreamMetaAndNeverTheRetiredCodecMeta()
    {
        var options = new ScrcpyServerOptions { ServerVersion = "5.0.1" };

        var args = options.BuildArguments();

        Assert.Contains("send_stream_meta=true", args, StringComparer.Ordinal);
        Assert.DoesNotContain(args, a => a.StartsWith("send_codec_meta", StringComparison.Ordinal));
        Assert.Contains("send_frame_meta=true", args, StringComparer.Ordinal);
        Assert.Contains("tunnel_forward=true", args, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("5.0.1", true)]
    [InlineData("5.0", true)]
    [InlineData("", false)]
    [InlineData("5", false)]
    [InlineData("5;rm -rf /", false)]
    [InlineData("v5.0.1", false)]
    [InlineData("5.0.1;CLASSPATH=/tmp/x", false)]
    public void ServerVersion_AcceptsOnlyDottedDecimal(string version, bool accepted)
    {
        if (accepted)
        {
            var options = new ScrcpyServerOptions { ServerVersion = version };
            Assert.Equal(version, options.BuildArguments()[0]);
            return;
        }

        Assert.Throws<ArgumentException>(() => new ScrcpyServerOptions { ServerVersion = version });
    }

    [Fact]
    public void BuildShellCommand_CarriesEveryArgumentAndNothingElse()
    {
        var options = new ScrcpyServerOptions { ServerVersion = "5.0.1", MaxSize = 1080, VideoBitRate = 4_000_000 };

        var command = options.BuildShellCommand();

        Assert.StartsWith($"CLASSPATH={ScrcpyConstants.ServerDevicePath} app_process / {ScrcpyConstants.ServerMainClass} 5.0.1 ",
            command, StringComparison.Ordinal);
        Assert.Contains("max_size=1080", command, StringComparison.Ordinal);
        Assert.Contains("video_bit_rate=4000000", command, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", command, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPacketFlags_LeaveTheLowSixtyOneBitsForTimestamps()
    {
        Assert.Equal(0x1FFF_FFFF_FFFF_FFFFUL, ScrcpyConstants.PacketTimestampMask);
        var session = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(session, ScrcpyConstants.PacketFlagSession | 1080u);
        BinaryPrimitives.WriteInt32BigEndian(session.AsSpan(8), 2400);

        var flagsAndWidth = BinaryPrimitives.ReadUInt64BigEndian(session);

        Assert.Equal(1080u, flagsAndWidth & 0xFFFF_FFFF);
        Assert.Equal(2400, BinaryPrimitives.ReadInt32BigEndian(session.AsSpan(8)));
        Assert.Equal(ScrcpyConstants.PacketFlagSession, flagsAndWidth & ScrcpyConstants.PacketFlagSession);
    }
}