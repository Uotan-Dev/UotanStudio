using System.Net;
using UotanStudio.Mirroring.AirPlay;
using UotanStudio.Mirroring.AirPlay.Mdns;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

public sealed class AirPlayMdnsMessageTests
{
    [Fact]
    public void BuildQuery_SinglePointerQuestion_RoundTrips()
    {
        var packet = MdnsMessage.BuildQuery(AirPlayConstants.AirPlayServiceType, MdnsMessage.TypePtr);

        var message = MdnsMessage.Parse(packet);

        var question = Assert.Single(message.Questions);
        Assert.Equal(AirPlayConstants.AirPlayServiceType, question.Name);
        Assert.Equal(MdnsMessage.TypePtr, question.Type);
        Assert.Equal(MdnsMessage.ClassInternet, question.Class);
        Assert.Empty(message.Answers);
        Assert.Equal(0, message.Flags);
    }

    [Fact]
    public void BuildResponse_PointerServiceAndText_ExposesTargetsPortAndValues()
    {
        var target = "UotanStudio._airplay._tcp.local";
        var host = "uotan-aabbccddeeff.local";
        var records = new List<MdnsRecord>
        {
            MdnsRecord.Pointer(AirPlayConstants.AirPlayServiceType, target),
            MdnsRecord.ServiceRecord(target, host, 7100),
            MdnsRecord.TextRecord(target, ["deviceid=aa:bb:cc:dd:ee:ff", "features=0x5A7FFFF7", "vv=2"]),
        };

        var packet = MdnsMessage.BuildResponse(records);
        var message = MdnsMessage.Parse(packet);

        Assert.Equal(0x8400, message.Flags);
        Assert.Equal(3, message.Answers.Count);
        Assert.Equal(target, message.Answers[0].Target);
        Assert.Equal(host, message.Answers[1].Target);
        Assert.Equal(7100, message.Answers[1].Port);
        Assert.Equal("aa:bb:cc:dd:ee:ff", message.Answers[2].GetText("deviceid"));
        Assert.Equal("2", message.Answers[2].GetText("vv"));
        Assert.Null(message.Answers[2].GetText("absent"));
    }

    [Fact]
    public void Parse_CompressedNames_ResolveOwnerAndTarget()
    {
        var packet = BuildPointerAnswerWithCompression();

        var message = MdnsMessage.Parse(packet);

        Assert.Equal(2, message.Answers.Count);
        Assert.Equal("svc.local", message.Answers[0].Name);
        Assert.Equal("target.local", message.Answers[0].Target);
        Assert.Equal("svc.local", message.Answers[1].Name);
        Assert.Equal("target.local", message.Answers[1].Target);
    }

    [Fact]
    public void Parse_TruncatedPacket_Throws()
    {
        var packet = MdnsMessage.BuildResponse([MdnsRecord.Pointer(AirPlayConstants.AirPlayServiceType, "x.local")]);

        Assert.Throws<InvalidDataException>(() => MdnsMessage.Parse(packet.AsSpan(0, packet.Length - 2)));
    }

    [Fact]
    public void Parse_RecordCountBeyondBound_Throws()
    {
        var packet = new byte[12];
        packet[7] = 0xFF;
        packet[9] = 0xFF;

        Assert.Throws<InvalidDataException>(() => MdnsMessage.Parse(packet));
    }

    [Fact]
    public void Parse_A_RecordExposesAddress()
    {
        var records = new[]
        {
            MdnsRecord.AddressRecord("uotan-aabbccddeeff.local", IPAddress.Parse("192.168.1.42")),
        };

        var message = MdnsMessage.Parse(MdnsMessage.BuildResponse(records));

        Assert.Equal(IPAddress.Parse("192.168.1.42"), message.Answers[0].Address);
    }

    [Fact]
    public void FormatDeviceId_SixBytes_ProducesColonSeparatedLowercase()
    {
        var identifier = AirPlayOptions.FormatDeviceId([0xAA, 0xBB, 0xCC, 0x0D, 0x0E, 0x0F]);

        Assert.Equal("aa:bb:cc:0d:0e:0f", identifier);
    }

    [Fact]
    public void FormatDeviceId_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => AirPlayOptions.FormatDeviceId([1, 2, 3]));
    }

    [Fact]
    public void Validate_RtspPortAboveRange_Throws()
    {
        var options = new AirPlayOptions { RtspPort = 70000 };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Validate_ControlCharacterInName_Throws()
    {
        var options = new AirPlayOptions { Name = "Uotan\u0001Studio" };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_MalformedDeviceId_Throws()
    {
        var options = new AirPlayOptions { DeviceId = "aa-bb-cc-dd-ee-ff" };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_DefaultOptions_Accepts()
    {
        var options = new AirPlayOptions();

        options.Validate();
    }

    private static byte[] BuildPointerAnswerWithCompression()
    {
        var owner = new byte[] { 3, (byte)'s', (byte)'v', (byte)'c', 5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0 };
        var target = new byte[]
        {
            6, (byte)'t', (byte)'a', (byte)'r', (byte)'g', (byte)'e', (byte)'t', 5, (byte)'l', (byte)'o', (byte)'c',
            (byte)'a', (byte)'l', 0,
        };
        var packet = new List<byte>(64)
        {
            0, 0, 0x84, 0, 0, 0, 0, 2, 0, 0, 0, 0,
        };
        var ownerOffset = packet.Count;
        packet.AddRange(owner);
        packet.AddRange([0, 12, 0, 1, 0, 0, 0, 120]);
        packet.AddRange([(byte)(target.Length >> 8), (byte)target.Length]);
        var targetOffset = packet.Count;
        packet.AddRange(target);

        packet.AddRange([0xC0, (byte)ownerOffset]);
        packet.AddRange([0, 12, 0, 1, 0, 0, 0, 120]);
        packet.AddRange([0xC0, (byte)targetOffset]);
        return [.. packet];
    }
}