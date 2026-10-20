using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>Checks device property parsing with scripted responses, without devices or UI state.</summary>
public sealed class DevicePropertyReaderTests
{
    [Fact]
    public void AdbProperties_BatchGetprop_ReadsEveryPropertyFromOneCommand()
    {
        var commands = new List<string>();
        string Run(string command)
        {
            commands.Add(command);
            return command switch
            {
                "getprop" => "[ro.product.model]: [Example phone]\r\n"
                    + "[ro.secureboot.lockstate]: [locked]\r\n"
                    + "[ro.boot.vbmeta.device_state]: [locked]\r\n"
                    + "[ro.boot.verifiedbootstate]: [green]\r\n"
                    + "[ro.fastboot.unlock]: [false]\r\n",
                "wm size" => "Physical size: 1080x2400\nOverride size: 720x1600",
                "wm density" => "Physical density: 420",
                "cat /proc/uptime" => "90061.0 123.0",
                "dumpsys battery" => "level: 75\nvoltage: 4000\ntemperature: 250",
                _ => "",
            };
        }
        string Localize(string key) => key switch
        {
            "Device_Unlocked" => "unlocked",
            "Device_Locked" => "locked",
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
        Assert.Equal(1, commands.Count(command => command == "getprop"));
        Assert.DoesNotContain(commands, command => command.StartsWith("getprop ", StringComparison.Ordinal));
        string[] expectedCommands =
        [
            "getprop",
            "uname -r",
            "wm size",
            "wm density",
            "cat /sys/devices/soc0/serial_number",
            "cat /proc/uptime",
            "dumpsys battery",
            "cat /proc/meminfo | grep -E 'MemTotal|MemAvailable'",
            "echo; df /storage/emulated 2>/dev/null || df /data 2>/dev/null || df /sdcard 2>/dev/null",
        ];
        Assert.Equal(expectedCommands, commands);
    }

    [Theory]
    [InlineData("unlocked", "", "", "", "Device_Unlocked")]
    [InlineData("", "unlocked", "", "", "Device_Unlocked")]
    [InlineData("", "", "orange", "", "Device_Unlocked")]
    [InlineData("", "", "", "true", "Device_Unlocked")]
    [InlineData("", "locked", "orange", "true", "Device_Unlocked")]
    [InlineData("locked", "", "", "", "Device_Locked")]
    [InlineData("", "", "green", "", "Device_Locked")]
    [InlineData("", "", "yellow", "", "Device_Locked")]
    [InlineData("", "", "", "false", "Device_Locked")]
    [InlineData("", "", "", "unknown", "Device_Locked")]
    [InlineData("", "", "", "", "--")]
    [InlineData("", "locked", "red", "", "Device_Locked")]
    [InlineData("", "", "red", "", "--")]
    public void AdbLockSignals_UnlockedEvidenceWinsOverLockedEvidence(
        string lockstate,
        string deviceState,
        string verifiedbootstate,
        string oemUnlock,
        string expected)
    {
        string[] tableLines =
        [
            $"[ro.secureboot.lockstate]: [{lockstate}]",
            $"[ro.boot.vbmeta.device_state]: [{deviceState}]",
            $"[ro.boot.verifiedbootstate]: [{verifiedbootstate}]",
            $"[ro.fastboot.unlock]: [{oemUnlock}]",
        ];
        var properties = DevicePropertyReader.ReadAdbProperties(command => command switch
        {
            "getprop" => string.Join('\n', tableLines),
            _ => "",
        }, key => key);

        Assert.Equal(expected, properties["bl"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("error: device offline")]
    public void AdbProperties_UnusableBatchGetprop_FallsBackToPerKeyCommands(string batchOutput)
    {
        var commands = new List<string>();
        string Run(string command)
        {
            commands.Add(command);
            return command switch
            {
                "getprop" => batchOutput,
                "getprop ro.product.model" => "Fallback phone",
                "getprop ro.boot.vbmeta.device_state" => "unlocked",
                _ => "",
            };
        }

        var properties = DevicePropertyReader.ReadAdbProperties(Run, key => key);

        Assert.Equal("Fallback phone", properties["model"]);
        Assert.Equal("Device_Unlocked", properties["bl"]);
        Assert.Equal(1, commands.Count(command => command == "getprop"));
        Assert.Contains("getprop ro.product.brand", commands);
        Assert.Contains("getprop ro.fastboot.unlock", commands);
    }

    [Fact]
    public void AdbProperties_PermissionDeniedBoardIdFallsBackToSentinel()
    {
        var properties = DevicePropertyReader.ReadAdbProperties(command => command switch
        {
            "cat /sys/devices/soc0/serial_number" => "cat: /sys/devices/soc0/serial_number: Permission denied",
            _ => "",
        }, key => key);

        Assert.Equal("--", properties["boardid"]);
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
        Assert.Equal(0, DevicePropertyReader.ParseMem("MemTotal: 1000000 kB").Level);
    }
}