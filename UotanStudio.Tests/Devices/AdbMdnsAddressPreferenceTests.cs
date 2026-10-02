using System.Collections.Concurrent;
using System.Net;
using Makaretu.Dns;
using UotanStudio.Comm.Transports.Udp;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class AdbMdnsAddressPreferenceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Accumulate_DualStackAnswers_Ipv4WinsRegardlessOfRecordOrder(bool aaaaFirst)
    {
        var message = new Message();
        var aaaa = new AAAARecord
        {
            Name = new DomainName("phone.local"),
            Address = IPAddress.Parse("fd56:1de:1157:0:a034:6903:6060:7323")
        };
        var a = new ARecord { Name = new DomainName("phone.local"), Address = IPAddress.Parse("192.0.2.7") };
        if (aaaaFirst)
        {
            message.Answers.Add(aaaa);
            message.Answers.Add(a);
        }
        else
        {
            message.Answers.Add(a);
            message.Answers.Add(aaaa);
        }

        var addresses = Accumulate(message);

        Assert.Equal("192.0.2.7", addresses["phone.local"]);
    }

    [Fact]
    public void Accumulate_Ipv6OnlyAnswer_KeepsAdvertisedIpv6Address()
    {
        var message = new Message();
        message.Answers.Add(new AAAARecord
        {
            Name = new DomainName("phone.local"),
            Address = IPAddress.Parse("fd56:1de:1157:0:a034:6903:6060:7323")
        });

        var addresses = Accumulate(message);

        Assert.Equal("fd56:1de:1157:0:a034:6903:6060:7323", addresses["phone.local"]);
    }

    [Fact]
    public void Accumulate_LaterIpv4Answer_ReplacesEarlierIpv6Address()
    {
        var first = new Message();
        first.Answers.Add(new AAAARecord
        {
            Name = new DomainName("phone.local"),
            Address = IPAddress.Parse("fd56::1")
        });
        var addresses = Accumulate(first);
        var second = new Message();
        second.Answers.Add(new ARecord { Name = new DomainName("phone.local"), Address = IPAddress.Parse("192.0.2.9") });

        Accumulate(second, addresses);

        Assert.Equal("192.0.2.9", addresses["phone.local"]);
    }

    private static ConcurrentDictionary<string, string> Accumulate(
        Message message, ConcurrentDictionary<string, string>? addresses = null)
    {
        var target = addresses ?? new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AdbMdnsDiscovery.Accumulate(
            message,
            new ConcurrentDictionary<string, AdbMdnsDiscovery.DeviceAccumulator>(StringComparer.OrdinalIgnoreCase),
            target);
        return target;
    }
}