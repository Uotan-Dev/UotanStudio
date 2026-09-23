using System.Collections.Specialized;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Logging;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class DeviceLogBufferTests
{
    [Fact]
    public void VerboseTargets_PreservePhysicalTransportAndAuthorization_DeduplicateKeys()
    {
        var targets = HdcTargetInfo.Parse("phone\t\tUSB\tConnected\tlocalhost\thdc\n" +
            "192.0.2.1:5555 TCP Unauthorized unknown\nphone USB Connected localhost hdc\n");
        Assert.Equal(2, targets.Count);
        Assert.Equal("USB", Assert.Single(targets, t => t.ConnectKey == "phone").Transport);
        Assert.Equal("Unauthorized", Assert.Single(targets, t => t.Transport == "TCP").State);
        Assert.True(DeviceMonitorService.IsSupportedHdcTarget(new HdcTargetInfo("phone", "USB", "Connected", "")));
        Assert.True(DeviceMonitorService.IsSupportedHdcTarget(new HdcTargetInfo("192.0.2.1:5555", "TCP", "Connected", "")));
        Assert.False(DeviceMonitorService.IsSupportedHdcTarget(new HdcTargetInfo("COM3", "UART", "Ready", "")));
        Assert.Empty(HdcTargetInfo.Parse("[Empty]\n"));
        Assert.Throws<IOException>(() => HdcTargetInfo.Parse("[Fail]server unavailable"));
        Assert.Throws<InvalidDataException>(() => HdcTargetInfo.Parse("incomplete"));
    }

    [Fact]
    public void Buffer_EvictsOldestWithinCountAndCharacterBudgets()
    {
        var buffer = new BoundedDeviceLogBuffer(3, 10);
        var removed = new List<AdbLogcatEntry>();
        foreach (var text in new[] { "aaa", "bbb", "ccc", "dddddd" })
            buffer.Add(AdbLogcatEntry.Parse(text), removed.Add);
        Assert.Equal(new[] { "aaa", "bbb" }, removed.Select(e => e.RawText));
        Assert.Equal(new[] { "ccc", "dddddd" }, buffer.Entries.Select(e => e.RawText));
        Assert.Equal(9, buffer.CharacterCount);
        buffer.Take();
        Assert.Equal(6, buffer.CharacterCount);
        buffer.Clear();
        Assert.Equal(0, buffer.CharacterCount);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void ContinuousBurst_RetainsNewestEntries_WithTwoNotificationsPerBatch()
    {
        var buffer = new BoundedDeviceLogBuffer();
        var visible = new LogEntryCollection<AdbLogcatEntry>();
        var mirror = new List<AdbLogcatEntry>();
        var notifications = 0;
        visible.CollectionChanged += (_, args) =>
        {
            notifications++;
            if (args.Action == NotifyCollectionChangedAction.Remove)
                mirror.RemoveRange(args.OldStartingIndex, args.OldItems!.Count);
            else if (args.Action == NotifyCollectionChangedAction.Add)
                mirror.InsertRange(args.NewStartingIndex, args.NewItems!.Cast<AdbLogcatEntry>());
            else { mirror.Clear(); mirror.AddRange(visible); }
        };
        for (var batch = 0; batch < 100; batch++)
        {
            var appended = new List<AdbLogcatEntry>();
            var removed = 0;
            for (var i = 0; i < 250; i++)
            {
                var entry = AdbLogcatEntry.Parse($"{batch}:{i} " + new string('x', 1024));
                buffer.Add(entry, _ => removed++);
                appended.Add(entry);
            }
            var before = notifications;
            visible.Apply(removed, appended);
            Assert.InRange(notifications - before, 1, 2);
            Assert.Equal(buffer.Entries, mirror);
            Assert.InRange(buffer.CharacterCount, 0, 2 * 1024 * 1024);
        }
        visible.Replace(visible.Where(e => e.RawText.StartsWith("99:", StringComparison.Ordinal)));
        Assert.Equal(250, mirror.Count);
        Assert.Equal(visible, mirror);
    }
}