using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>Checks device property parsing with scripted responses, without devices or UI state.</summary>
public sealed class DevicePropertyReaderTests
{
    [Fact]
    public void AdbProperties_UsesFallbackAndCallerLocalization()
    {
        var commands = new List<string>();
        string Run(string command)
        {
            commands.Add(command);
            return command switch
            {
                "getprop ro.product.model" => "\r\nExample phone\r\n",
                "getprop ro.secureboot.lockstate" => "not found",
                "getprop ro.boot.vbmeta.device_state" => "locked",
                "wm size" => "Physical size: 1080x2400\nOverride size: 720x1600",
                "wm density" => "Physical density: 420",
                "cat /proc/uptime" => "90061.0 123.0",
                "dumpsys battery" => "level: 75\nvoltage: 4000\ntemperature: 250",
                _ => "",
            };
        }
        string Localize(string key) => key switch
        {
            "Device_UptimeDay" => "d",
            "Device_UptimeHour" => "h",
            "Device_UptimeMinute" => "m",
            "Device_UptimeSecond" => "s",
            _ => throw new InvalidOperationException(key),
        };

        var properties = DevicePropertyReader.ReadAdbProperties(Run, Localize);

        Assert.Equal("Example phone", properties["model"]);
        Assert.Equal("locked", properties["bl"]);
        Assert.Equal("720x1600", properties["resolution"]);
        Assert.Equal("420", properties["density"]);
        Assert.Equal("1d1h1m1s", properties["uptime"]);
        Assert.Equal("75", properties["batteryLevel"]);
        Assert.Equal("--", properties["brand"]);
        var first = commands.IndexOf("getprop ro.secureboot.lockstate");
        Assert.Equal("getprop ro.boot.vbmeta.device_state", commands[first + 1]);
    }

    [Fact]
    public void AdbProperties_DoesNotRunLockFallbackAfterSuccessfulPrimaryQuery()
    {
        var commands = new List<string>();
        DevicePropertyReader.ReadAdbProperties(command =>
        {
            commands.Add(command);
            return command == "getprop ro.secureboot.lockstate" ? "locked" : "";
        }, key => key);

        Assert.DoesNotContain("getprop ro.boot.vbmeta.device_state", commands);
    }

    [Theory]
    [InlineData("_a", "Slot A")]
    [InlineData(" B ", "Slot B")]
    [InlineData("current-slot: a", "Slot A")]
    [InlineData("unknown", "--")]
    public void Slot_RecognizesExistingProtocolRepresentations(string input, string expected) =>
        Assert.Equal(expected, DevicePropertyReader.ParseSlot(input));

    [Fact]
    public void Resources_IgnoreChargerVoltageAndPreserveUnitConversions()
    {
        var battery = DevicePropertyReader.ParseBattery(
            "Max charging voltage: 9000000\nCharger voltage: 9000\nlevel: 75\nvoltage: 4000\ntemperature: 250");
        Assert.Equal(75, battery.Level);
        Assert.Equal($"{4:0.00}V {25:0.0}\u2103", battery.Text);

        var memory = DevicePropertyReader.ParseMem("MemTotal: 1000000 kB\nMemAvailable: 500000 kB");
        Assert.Equal(50, memory.Level);
        Assert.Equal($"{0.5:0.0}GB/{1:0.0}GB", memory.Text);

        var disk = DevicePropertyReader.ParseDisk("Filesystem Size Used Avail Use% Mounted\n/data 1048576 524288 524288 50% /data");
        Assert.Equal(50, disk.Level);
        Assert.Equal($"{0.5:0.00}GB/{1:0.00}GB", disk.Text);
    }

    [Fact]
    public void MissingResources_KeepExistingUnavailableSentinels()
    {
        Assert.Equal((-1, "--"), DevicePropertyReader.ParseBattery("permission denied"));
        Assert.Equal((-1, "--"), DevicePropertyReader.ParseMem("permission denied"));
        Assert.Equal((-1, "--"), DevicePropertyReader.ParseDisk("permission denied"));
        Assert.Equal("--", DevicePropertyReader.FormatUptime("--", _ => throw new InvalidOperationException()));
        // The existing panel treats unavailable MemAvailable as zero usage; keep that policy here.
        Assert.Equal(0, DevicePropertyReader.ParseMem("MemTotal: 1000000 kB").Level);
    }
}
