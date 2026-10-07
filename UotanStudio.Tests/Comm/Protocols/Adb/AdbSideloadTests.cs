using System.Text;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Adb;

/// <summary>
/// ADB sideload flash tests: the recovery sideload command sequence (reboot:sideload service
/// plus a streaming sync push to /sideload/package.zip).
/// <para>ADB sideload 刷写测试：recovery sideload 命令序列（reboot:sideload 服务与对
/// /sideload/package.zip 的流式 sync 推送）。</para>
/// </summary>
public class AdbSideloadTests
{
    [Fact]
    public void RebootToSideload_SendsRebootSideloadService()
    {
        var transport = new FakeAdbDeviceTransport();
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        adb.Connect();

        FlashService.RebootToSideload(adb);

        Assert.Contains("reboot:sideload", transport.OpenedServices);
    }

    [Fact]
    public async Task PushSideloadFileAsync_StreamsOtaToSideloadPackageZip()
    {
        var transport = new FakeAdbDeviceTransport();
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        adb.Connect();

        var otaBytes = new byte[32 * 1024];
        Random.Shared.NextBytes(otaBytes);
        using var dir = new TempDirectory();
        var otaPath = dir.WriteBytes("ota-update.zip", otaBytes);

        await FlashService.PushSideloadFileAsync(adb, otaPath, progress: null, TestContext.Current.CancellationToken);

        Assert.Contains("sync:", transport.OpenedServices);

        var send = transport.WrtePayloads.First(p => Encoding.ASCII.GetString(p, 0, 4) == SyncIds.Send);
        var sendTarget = Encoding.UTF8.GetString(send, 8, send.Length - 8);
        Assert.StartsWith("/sideload/package.zip", sendTarget, StringComparison.Ordinal);

        Assert.Contains(transport.WrtePayloads, p => Encoding.ASCII.GetString(p, 0, 4) == SyncIds.Data);
        Assert.Contains(transport.WrtePayloads, p => Encoding.ASCII.GetString(p, 0, 4) == SyncIds.Done);

        var payload = new MemoryStream();
        foreach (var p in transport.WrtePayloads.Where(p => Encoding.ASCII.GetString(p, 0, 4) == SyncIds.Data))
        {
            payload.Write(p, 8, p.Length - 8);
        }

        Assert.Equal(otaBytes, payload.ToArray());
    }

    [Fact]
    public void IsSideloadTarget_DetectsZipAndSideloadPartition()
    {
        Assert.True(FlashService.IsSideloadTarget("sideload", "/tmp/anything.bin"));
        Assert.True(FlashService.IsSideloadTarget("boot", "/tmp/ota-update.zip"));
        Assert.False(FlashService.IsSideloadTarget("boot", "/tmp/boot.img"));
    }
}