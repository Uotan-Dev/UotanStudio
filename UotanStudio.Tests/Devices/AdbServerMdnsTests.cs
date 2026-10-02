using UotanStudio.Comm.Protocols.Adb;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class AdbServerMdnsTests
{
    private const string LiveCaptureRow = "probe-pair\t_adb-tls-pairing._tcp\t192.168.1.190:45678\n";

    [Fact]
    public void Parse_LiveServerPayload_ReturnsInstanceTypeAndDialEndpoint()
    {
        var entries = AdbServerMdns.Parse(LiveCaptureRow);

        var entry = Assert.Single(entries);
        Assert.Equal("probe-pair", entry.Instance);
        Assert.Equal("_adb-tls-pairing._tcp", entry.ServiceType);
        Assert.Equal("192.168.1.190", entry.Host);
        Assert.Equal(45678, entry.Port);
    }

    [Fact]
    public void Parse_DuplicateRowsPerInterface_CollapseToOneEntry()
    {
        var entries = AdbServerMdns.Parse(string.Concat(Enumerable.Repeat(LiveCaptureRow, 4)));

        Assert.Single(entries);
    }

    [Fact]
    public void IsType_NormalizedServerRow_MatchesLocalBrowseConstantOnlyForItsOwnService()
    {
        var connect = new AdbServerMdnsEntry("phone-1", "_adb-tls-connect._tcp", "192.0.2.31", 37177);
        var pairing = new AdbServerMdnsEntry("phone-1", "_adb-tls-pairing._tcp", "192.0.2.31", 45678);

        Assert.True(AdbServerMdns.IsType(connect, "_adb-tls-connect._tcp.local"));
        Assert.False(AdbServerMdns.IsType(connect, "_adb-tls-pairing._tcp.local"));
        Assert.True(AdbServerMdns.IsType(pairing, "_adb-tls-pairing._tcp.local"));
    }

    [Fact]
    public void Parse_ServiceTypeWithTrailingDot_NormalizesToComparableName()
    {
        var entries = AdbServerMdns.Parse("phone-1\t_adb-tls-connect._tcp.\t192.0.2.31:37177\n");

        var entry = Assert.Single(entries);
        Assert.Equal("_adb-tls-connect._tcp", entry.ServiceType);
        Assert.True(AdbServerMdns.IsType(entry, "_adb-tls-connect._tcp.local"));
    }

    [Fact]
    public void Parse_BracketedIpv6Row_KeepsAddressBracketFreeAndPort()
    {
        var entries = AdbServerMdns.Parse("phone-1\t_adb-tls-connect._tcp\t[fe80::1]:40001\n");

        var entry = Assert.Single(entries);
        Assert.Equal("fe80::1", entry.Host);
        Assert.Equal(40001, entry.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("List of discovered mdns services\n")]
    [InlineData("phone-1\t_adb-tls-connect._tcp\n")]
    [InlineData("phone-1\t_adb-tls-connect._tcp\t192.0.2.31\n")]
    [InlineData("phone-1\t_adb-tls-connect._tcp\t192.0.2.31:http\n")]
    [InlineData("phone-1\t_adb-tls-connect._tcp\t192.0.2.31:0\n")]
    [InlineData("phone-1\t_adb-tls-connect._tcp\t192.0.2.31:65536\n")]
    [InlineData("\t_adb-tls-connect._tcp\t192.0.2.31:5555\n")]
    public void Parse_MissingOrUnusableFields_ReturnsNoEntries(string payload)
    {
        Assert.Empty(AdbServerMdns.Parse(payload));
    }

    [Fact]
    public void Parse_RowsBeyondCap_StopAtTheBound()
    {
        var payload = string.Concat(Enumerable.Range(1, 400)
            .Select(index => $"phone-{index}\t_adb-tls-connect._tcp\t192.0.2.{index % 251}:{50000 + index}\n"));

        var entries = AdbServerMdns.Parse(payload);

        Assert.Equal(256, entries.Count);
    }

    [Fact]
    public void Parse_MixedValidAndMalformedLines_KeepsOnlyDialableRows()
    {
        var payload = "garbage line\n" +
                      "phone-1\t_adb-tls-connect._tcp\t192.0.2.31:37177\n" +
                      "phone-2\t_adb-tls-pairing._tcp\t192.0.2.31:45678\n";

        var entries = AdbServerMdns.Parse(payload);

        Assert.Equal(2, entries.Count);
        Assert.Equal("phone-1", entries[0].Instance);
        Assert.Equal("phone-2", entries[1].Instance);
    }

    [Fact]
    public void ToDevice_ServerRow_ProducesAddressAndPortWithoutSrvLookup()
    {
        var entry = new AdbServerMdnsEntry("probe-pair", "_adb-tls-pairing._tcp", "192.168.1.190", 45678);

        var device = AdbServerMdns.ToDevice(entry);

        Assert.Equal("probe-pair", device.Instance);
        Assert.Equal(45678, device.Port);
        Assert.Equal(["192.168.1.190"], device.Addresses);
        Assert.Empty(device.Properties);
    }
}