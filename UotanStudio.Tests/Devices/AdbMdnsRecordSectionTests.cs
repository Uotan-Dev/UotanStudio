using System.Collections.Concurrent;
using Makaretu.Dns;
using UotanStudio.Comm.Transports.Udp;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class AdbMdnsRecordSectionTests
{
    private static readonly DomainName Instance = new("phone-1._adb-tls-connect._tcp.local");
    private static readonly DomainName ServiceType = new("_adb-tls-connect._tcp.local");
    private static readonly DomainName Host = new("oneplus.local");

    [Fact]
    public void Accumulate_QueryResponseStyleRecords_ResolvesAddressAndSrvPort()
    {
        var message = new Message();
        message.Answers.Add(new PTRRecord { Name = ServiceType, DomainName = Instance });
        message.AdditionalRecords.Add(new SRVRecord { Name = Instance, Target = Host, Port = 37177 });
        message.AdditionalRecords.Add(new ARecord { Name = Host, Address = System.Net.IPAddress.Parse("192.0.2.31") });

        var devices = Browse(message);

        var device = Assert.Single(devices);
        Assert.Equal(Instance.ToString(), device.Instance);
        Assert.Equal("192.0.2.31", Assert.Single(device.Addresses));
        Assert.Equal(37177, device.Port);
    }

    [Fact]
    public void Accumulate_AnnounceStyleRecords_StillResolvesAddressAndSrvPort()
    {
        var message = new Message();
        message.Answers.Add(new PTRRecord { Name = ServiceType, DomainName = Instance });
        message.Answers.Add(new SRVRecord { Name = Instance, Target = Host, Port = 37177 });
        message.Answers.Add(new ARecord { Name = Host, Address = System.Net.IPAddress.Parse("192.0.2.31") });

        var device = Assert.Single(Browse(message));

        Assert.Equal("192.0.2.31", Assert.Single(device.Addresses));
        Assert.Equal(37177, device.Port);
    }

    [Fact]
    public void Accumulate_AddressRecordInAuthoritySection_ResolvesAddress()
    {
        var message = new Message();
        message.Answers.Add(new PTRRecord { Name = ServiceType, DomainName = Instance });
        message.AdditionalRecords.Add(new SRVRecord { Name = Instance, Target = Host, Port = 37177 });
        message.AuthorityRecords.Add(new ARecord { Name = Host, Address = System.Net.IPAddress.Parse("192.0.2.32") });

        var device = Assert.Single(Browse(message));

        Assert.Equal("192.0.2.32", Assert.Single(device.Addresses));
    }

    [Fact]
    public void BuildDevices_InstanceWithoutSrvRecord_OmitsDeviceInsteadOfGuessingDefaultPort()
    {
        var message = new Message();
        message.Answers.Add(new PTRRecord { Name = ServiceType, DomainName = Instance });
        message.AdditionalRecords.Add(new ARecord { Name = Instance, Address = System.Net.IPAddress.Parse("192.0.2.31") });

        Assert.Empty(Browse(message));
    }

    [Fact]
    public void BuildDevices_ResolvedInstanceWithoutAddress_OmitsDeviceInsteadOfReportingEmptyAddressList()
    {
        var message = new Message();
        message.AdditionalRecords.Add(new SRVRecord { Name = Instance, Target = Host, Port = 37177 });

        Assert.Empty(Browse(message));
    }

    [Fact]
    public void Accumulate_TxtRecordInAdditionalSection_ReportsProperties()
    {
        var message = new Message();
        message.Answers.Add(new PTRRecord { Name = ServiceType, DomainName = Instance });
        message.AdditionalRecords.Add(new SRVRecord { Name = Instance, Target = Host, Port = 37177 });
        message.AdditionalRecords.Add(new ARecord { Name = Host, Address = System.Net.IPAddress.Parse("192.0.2.31") });
        message.AdditionalRecords.Add(new TXTRecord { Name = Instance, Strings = ["model=LE2110", "device=OnePlus9"] });

        var device = Assert.Single(Browse(message));

        Assert.Equal("LE2110", device.Properties["model"]);
        Assert.Equal("OnePlus9", device.Properties["device"]);
    }

    private static IReadOnlyList<MdnsDevice> Browse(Message message)
    {
        var byInstance = new ConcurrentDictionary<string, AdbMdnsDiscovery.DeviceAccumulator>(StringComparer.OrdinalIgnoreCase);
        var addresses = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AdbMdnsDiscovery.Accumulate(message, byInstance, addresses);
        return AdbMdnsDiscovery.BuildDevices(byInstance, addresses, static _ => true);
    }
}