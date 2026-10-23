using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>Checks device property parsing with scripted responses, without devices or UI state.</summary>
public sealed class DevicePropertyReaderTests
{
    private static string LocalizeCompact(string key) => key switch
    {
        "Device_Unlocked" => "unlocked",
        "Device_Locked" => "locked",
        "Device_UptimeDay" => "d",
        "Device_UptimeHour" => "h",
        "Device_UptimeMinute" => "m",
        "Device_UptimeSecond" => "s",
        _ => key,
    };

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
            "cat /proc/meminfo",
            "df -k /storage/emulated 2>/dev/null || df -k /data 2>/dev/null || df -k /sdcard 2>/dev/null",
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

    [Fact]
    public void AdbProperties_Android9Sdk28_ReadsCapturedPropertiesAndResources()
    {
        var physicalBattery = """
            Current Battery Service state:
              AC powered: false
              USB powered: true
              Wireless powered: false
              Max charging current: 500000
              Max charging voltage: 5000000
              Charge counter: 3183360
              status: 2
              health: 2
              present: true
              level: 97
              scale: 100
              voltage: 4388
              temperature: 321
              technology: Li-ion
            """;
        var physicalDf = """
            Filesystem     1K-blocks    Used Available Use% Mounted on
            /data/media    114134860 4935004 109199856   5% /storage/emulated
            """;

        var properties = DevicePropertyReader.ReadAdbProperties(command => command switch
        {
            "getprop" => """
                [ro.product.brand]: [qti]
                [ro.product.model]: [sm6150 for arm64]
                [ro.build.version.release]: [9]
                [ro.build.version.sdk]: [28]
                [ro.product.device]: [sm6150]
                [ro.product.cpu.abi]: [arm64-v8a]
                [ro.board.platform]: [sm6150]
                [ro.hardware]: [qcom]
                [ro.boot.hardware]: [qcom]
                [ro.boot.slot_suffix]: [_a]
                [ro.system.build.version.incremental]: []
                [ro.build.version.incremental]: [eng.lnxbui.20190213.004411]
                [ro.vndk.version]: [28]
                [ro.boot.verifiedbootstate]: [orange]
                """,
            "uname -r" => "4.14.83+",
            "wm size" => "Physical size: 1440x2880",
            "wm density" => "Physical density: 560",
            "cat /sys/devices/soc0/serial_number" => "2550535838",
            "cat /proc/uptime" => "9017.51 70122.81",
            "dumpsys battery" => physicalBattery,
            "cat /proc/meminfo" => "MemTotal:        7820660 kB\nMemAvailable:    5484016 kB",
            "df -k /storage/emulated 2>/dev/null || df -k /data 2>/dev/null || df -k /sdcard 2>/dev/null" => physicalDf,
            _ => "",
        }, LocalizeCompact);

        Assert.Equal("qti", properties["brand"]);
        Assert.Equal("sm6150 for arm64", properties["model"]);
        Assert.Equal("9", properties["android"]);
        Assert.Equal("28", properties["sdk"]);
        Assert.Equal("sm6150", properties["codename"]);
        Assert.Equal("arm64-v8a", properties["cpuabi"]);
        Assert.Equal("sm6150", properties["cpucode"]);
        Assert.Equal("4.14.83+", properties["kernel"]);
        Assert.Equal("1440x2880", properties["resolution"]);
        Assert.Equal("560", properties["density"]);
        Assert.Equal("2550535838", properties["boardid"]);
        Assert.Equal("_a", properties["slot"]);
        Assert.Equal("eng.lnxbui.20190213.004411", properties["compile"]);
        Assert.Equal("28", properties["vndk"]);
        Assert.Equal("unlocked", properties["bl"]);
        Assert.Equal("0d2h30m17s", properties["uptime"]);
        Assert.Equal("97", properties["batteryLevel"]);
        Assert.Equal("4.39V 32.1℃", properties["batteryText"]);
        Assert.Equal("30", properties["memLevel"]);
        Assert.Equal("2.4GB/8.0GB", properties["memText"]);
        Assert.Equal("4", properties["diskLevel"]);
        Assert.Equal("4.71GB/108.85GB", properties["diskText"]);
    }

    [Fact]
    public void AdbProperties_Android15Emulator_ReadsHardwareFallbackAndKeyedBattery()
    {
        var emulatorBattery = """
            Current Battery Service state:
              AC powered: true
              USB powered: false
              Wireless powered: false
              Dock powered: false
              Max charging current: 0
             Time when the latest updated value of the Max charging current was sent via battery changed broadcast: 0
              Max charging voltage: 0
              Charge counter: 10
              status: 3
              health: 2
              present: true
              level: 77
              scale: 100
              voltage: 3
             Time when the latest updated value of the voltage was sent via battery changed broadcast: 0
             The last voltage value sent via the battery changed broadcast: 3
              temperature: 360
              technology: Li-ion
              Charging state: 0
              Charging policy: 0
              Capacity level: 3
            """;
        var emulatorDf = """
            Filesystem     1K-blocks     Used Available Use% Mounted on
            /dev/fuse      132023748 19037712 106258868  16% /storage/emulated
            """;

        var properties = DevicePropertyReader.ReadAdbProperties(command => command switch
        {
            "getprop" => """
                [ro.product.brand]: [HUAWEI]
                [ro.product.model]: [ABR-AL80]
                [ro.build.version.release]: [15]
                [ro.build.version.sdk]: [35]
                [ro.product.device]: [Amber]
                [ro.product.cpu.abi]: [x86_64]
                [ro.board.platform]: [HUAWEI]
                [ro.hardware]: [HUAWEI]
                [ro.boot.hardware]: [kona]
                [ro.system.build.version.incremental]: [1137]
                """,
            "uname -r" => "6.1.90-perf+",
            "wm size" => "Physical size: 900x1600",
            "wm density" => "Physical density: 240",
            "cat /sys/devices/soc0/serial_number" => "3477775117",
            "cat /proc/uptime" => "9786.29 33931.12",
            "dumpsys battery" => emulatorBattery,
            "cat /proc/meminfo" => "MemTotal:        3063096 kB\nMemAvailable:    2473928 kB",
            "df -k /storage/emulated 2>/dev/null || df -k /data 2>/dev/null || df -k /sdcard 2>/dev/null" => emulatorDf,
            _ => "",
        }, LocalizeCompact);

        Assert.Equal("HUAWEI", properties["brand"]);
        Assert.Equal("ABR-AL80", properties["model"]);
        Assert.Equal("15", properties["android"]);
        Assert.Equal("35", properties["sdk"]);
        Assert.Equal("Amber", properties["codename"]);
        Assert.Equal("x86_64", properties["cpuabi"]);
        Assert.Equal("kona", properties["cpucode"]);
        Assert.Equal("6.1.90-perf+", properties["kernel"]);
        Assert.Equal("900x1600", properties["resolution"]);
        Assert.Equal("240", properties["density"]);
        Assert.Equal("3477775117", properties["boardid"]);
        Assert.Equal("--", properties["slot"]);
        Assert.Equal("1137", properties["compile"]);
        Assert.Equal("--", properties["vndk"]);
        Assert.Equal("--", properties["bl"]);
        Assert.Equal("0d2h43m6s", properties["uptime"]);
        Assert.Equal("77", properties["batteryLevel"]);
        Assert.Equal("0.00V 36.0℃", properties["batteryText"]);
        Assert.Equal("19", properties["memLevel"]);
        Assert.Equal("0.6GB/3.1GB", properties["memText"]);
        Assert.Equal("14", properties["diskLevel"]);
        Assert.Equal("18.16GB/125.91GB", properties["diskText"]);
    }

    [Theory]
    [InlineData("Physical size: 1440x2880\nOverride size: 720x1600", "720x1600")]
    [InlineData("Physical size: 1440x2880", "1440x2880")]
    [InlineData("error: device offline", "--")]
    public void WmSize_OverrideValueTakesPrecedenceOverPhysicalValue(string output, string expected) =>
        Assert.Equal(expected, DevicePropertyReader.ParseWmSize(output));

    [Theory]
    [InlineData("Physical density: 560\nOverride density: 480", "480")]
    [InlineData("Physical density: 240", "240")]
    [InlineData("", "--")]
    public void WmDensity_OverrideValueTakesPrecedenceOverPhysicalValue(string output, string expected) =>
        Assert.Equal(expected, DevicePropertyReader.ParseWmDensity(output));

    [Theory]
    [InlineData(" 02:51:44 up 7 days,  2:01,  0 users,  load average: 14.22, 14.35, 14.72", "7d2h1m0s")]
    [InlineData(" 02:51:48 up  2:31,  0 users,  load average: 0.30, 0.19, 0.17", "0d2h31m0s")]
    [InlineData("9017.51 70122.81", "0d2h30m17s")]
    public void FormatUptime_HarmonyAndProcOutputs_ProduceLocalizedDuration(string output, string expected) =>
        Assert.Equal(expected, DevicePropertyReader.FormatUptime(output, LocalizeCompact));

    [Fact]
    public void ParseDisk_RealDfLayouts_ReadsNumericRowRegardlessOfMountPath()
    {
        var android9 = """
            Filesystem     1K-blocks    Used Available Use% Mounted on
            /data/media    114134860 4935004 109199856   5% /storage/emulated
            """;
        var android15 = """
            Filesystem     1K-blocks     Used Available Use% Mounted on
            /dev/fuse      132023748 19037712 106258868  16% /storage/emulated
            """;
        var harmonyUsb = """
            Filesystem                                        1K-blocks      Used Available Use% Mounted on
            /dev/block/platform/ee560000.ufs/by-name/userdata 962222080 442314164 519776844  46% /mnt/debugtmp/100/debug_hap/com.atomgit.mobile/data/storage/el1/base
            """;
        var harmonyEmulator = """
            Filesystem     1K-blocks   Used Available Use% Mounted on
            /dev/block/vdd   5979328 819600   4829008  15% /mnt/user/100/nosharefs/appdata/el2/base
            """;

        Assert.Equal((4, "4.71GB/108.85GB"), DevicePropertyReader.ParseDisk(android9));
        Assert.Equal((14, "18.16GB/125.91GB"), DevicePropertyReader.ParseDisk(android15));
        Assert.Equal((46, "421.82GB/917.65GB"), DevicePropertyReader.ParseDisk(harmonyUsb));
        Assert.Equal((14, "0.78GB/5.70GB"), DevicePropertyReader.ParseDisk(harmonyEmulator));
    }

    [Fact]
    public void ParseMem_FullHarmonyMeminfo_IgnoresFreeAndCachedLines()
    {
        var meminfo = """
            MemTotal:       16004744 kB
            MemFree:          439184 kB
            MemAvailable:    5890048 kB
            Buffers:               0 kB
            Cached:          5773756 kB
            """;

        Assert.Equal((63, "10.4GB/16.4GB"), DevicePropertyReader.ParseMem(meminfo));
    }

    [Fact]
    public void ParseHarmonyBootDetails_PhysicalUsb_ReadsSocBoardLockAndSlotFromCmdline()
    {
        var dump = """
            -------------------------------[base]-------------------------------

            BuildId: SGT-AL10 7.0.0.109(SP6C00E105R3P3)
            HardwareModel: HL1FJJM

            /proc/version

            Hongmeng version: HongMeng Kernel 1.13.0

            /proc/cmdline

             ohos.boot.hvb.enable=green storage_cid=383262336638623138663530022401ce efuse_status=0 developer_mode=1 oemmode=user ohos.boot.advsecmode.state=0 ohos.boot.bootslots=2 ohos.boot.currentslot=2 sn=5XM0225C18001159 boardid=23464 ohos.boot.board.boardid=23464 ohos.boot.hvb.device_state=locked ohos.boot.hardware=Kirin9030Pro ohos.boot.chiptype=Kirin9030Pro ohos.boot.gpu_vendor=higpu.v350

            cmd is: uptime -p
            """;

        var details = DevicePropertyReader.ParseHarmonyBootDetails(dump, LocalizeCompact);

        Assert.Equal("Kirin9030Pro", details.SocPlatform);
        Assert.Equal("23464", details.BoardId);
        Assert.Equal("locked", details.LockState);
        Assert.Equal("Slot B", details.Slot);
    }

    [Fact]
    public void ParseHarmonyBootDetails_Emulator_ReadsEmulatedHardwareAndLeavesMissingFieldsUnavailable()
    {
        var dump = """
            -------------------------------[base]-------------------------------

            /proc/cmdline

             console=ttyS0,115200 hardware=hmos.emulator ohos.boot.udid=454D55057494E0583836044990B7F561A49D5896C82D391D8EE4500000000000 const.build.product=HAD devmodel=PCEMU-FD00 vendorcountry=all/cn ohos.boot.qemu.dpi=304

            cmd is: uptime -p
            """;

        var details = DevicePropertyReader.ParseHarmonyBootDetails(dump, LocalizeCompact);

        Assert.Equal("hmos.emulator", details.SocPlatform);
        Assert.Equal("--", details.BoardId);
        Assert.Equal("--", details.LockState);
        Assert.Equal("--", details.Slot);
    }

    [Theory]
    [InlineData("ohos.boot.hvb.device_state=locked", "locked")]
    [InlineData("ohos.boot.hvb.device_state=unlocked", "unlocked")]
    [InlineData("ohos.boot.hvb.enable=green", "locked")]
    [InlineData("ohos.boot.hvb.enable=orange", "unlocked")]
    [InlineData("ohos.boot.hardware=Kirin9030Pro", "--")]
    public void ParseHarmonyBootDetails_LockEvidence_UnlockedEvidenceWins(string tokens, string expected)
    {
        var details = DevicePropertyReader.ParseHarmonyBootDetails(
            "/proc/cmdline\n\n" + tokens + "\n", LocalizeCompact);

        Assert.Equal(expected, details.LockState);
    }

    [Theory]
    [InlineData("ohos.boot.currentslot=1", "Slot A")]
    [InlineData("ohos.boot.currentslot=2", "Slot B")]
    [InlineData("ohos.boot.slot_suffix=_a", "Slot A")]
    [InlineData("ohos.boot.slot_suffix=_b", "Slot B")]
    [InlineData("hardware=Kirin9030Pro", "--")]
    public void ParseHarmonyBootDetails_SlotEvidence_UsesOneBasedSlotOrSuffix(string tokens, string expected)
    {
        var details = DevicePropertyReader.ParseHarmonyBootDetails(
            "/proc/cmdline\n\n" + tokens + "\n", LocalizeCompact);

        Assert.Equal(expected, details.Slot);
    }

    [Fact]
    public void ParseHarmonyBootDetails_MissingCmdline_ResolvesEveryFieldToUnavailable()
    {
        var details = DevicePropertyReader.ParseHarmonyBootDetails(
            "-------------------------------[base]-------------------------------\n\nBuildId: SGT-AL10\n", LocalizeCompact);

        Assert.Equal(("--", "--", "--", "--"),
            (details.SocPlatform, details.BoardId, details.LockState, details.Slot));
    }

    [Fact]
    public void ParseHarmonyDisplay_PhysicalAndEmulatorDumps_PreferMainDisplayFields()
    {
        var physical = """
            [SCREEN INFO]
            VirtualWidth:                 330
            VirtualHeight:                712
            VirtualPixelRatio:            4
            [SCREEN PROPERTY]
            Density:                      4
            DensityInCurResolution:       3.375
            DPI<X, Y>:                    478.971, 463.712
            Bounds<L,T,W,H>:              0, 0, 1320, 2848,
            ---------------- Display ID: 0 ----------------
            [DISPLAY INFO]
            Width:                        1320
            Height:                       2848
            """;
        var emulator = """
            [SCREEN INFO]
            VirtualWidth:                 1642
            VirtualHeight:                1094
            [SCREEN PROPERTY]
            Density:                      1.9
            DPI<X, Y>:                    264.16, 264.16
            Bounds<L,T,W,H>:              0, 0, 3120, 2080,
            ---------------- Display ID: 0 ----------------
            [DISPLAY INFO]
            Width:                        3120
            Height:                       2080
            """;

        Assert.Equal("1320x2848", DevicePropertyReader.ParseHarmonyResolution(physical));
        Assert.Equal("640", DevicePropertyReader.ParseHarmonyDensity(physical));
        Assert.Equal("3120x2080", DevicePropertyReader.ParseHarmonyResolution(emulator));
        Assert.Equal("304", DevicePropertyReader.ParseHarmonyDensity(emulator));
        Assert.Equal("--", DevicePropertyReader.ParseHarmonyResolution("no display"));
        Assert.Equal("--", DevicePropertyReader.ParseHarmonyDensity("no display"));
    }

    [Fact]
    public void ParseHarmonyBattery_BatteryServiceOutput_ConvertsMicrovoltsAndTenths()
    {
        var physical = """
            capacity: 72
            batteryLevel: 3
            voltage: 4124996
            temperature: 300
            """;
        var emulator = """
            capacity: 100
            batteryLevel: 1
            voltage: 4000000
            temperature: 250
            """;

        Assert.Equal((72, "4.12V 30.0℃"), DevicePropertyReader.ParseHarmonyBattery(physical));
        Assert.Equal((100, "4.00V 25.0℃"), DevicePropertyReader.ParseHarmonyBattery(emulator));
        Assert.Equal((-1, "--"), DevicePropertyReader.ParseHarmonyBattery("no battery"));
        Assert.Equal((-1, "--"), DevicePropertyReader.ParseHarmonyBattery("capacity: -1\nvoltage: 0\ntemperature: 250"));
    }
}