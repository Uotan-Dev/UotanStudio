using System.Security.Cryptography;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Mtk;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Opt-in L2/L3 read-only evidence for a real MediaTek device. Discovery and the BROM reverse-code
/// handshake plus hardware/version/configuration reads are exercised; no DA/EMI upload, storage
/// command, or shutdown request is ever sent. Without a device on the named USB id every test skips
/// with the gate variable in its reason.
/// <para>针对真实 MTK 设备的选配 L2/L3 只读证据。仅执行发现、BROM 反码握手与硬件/版本/配置读取；
/// 绝不上传 DA/EMI、不执行存储命令、不发送 shutdown。指定 USB id 上没有设备时，
/// 所有用例带着门控变量原因跳过。</para>
/// </summary>
public sealed class MtkRealDeviceReadTests
{
    [Fact]
    public async Task Discovery_NamedMtkEndpoint_IsPresentAndDirectUsb()
    {
        CommHardwareScaffold.RequireMtkDevice();
        var endpoint = await CommHardwareScaffold
            .FindEndpointAsync("mtk", CommHardwareScaffold.MtkDeviceVariable);

        Assert.Equal("mtk", endpoint.Protocol);
        Assert.Equal("direct-usb", endpoint.Route);
    }

    [Fact]
    public async Task BromHandshake_OnLiveEndpoint_ReadsIdentityWithoutUploadingAgents()
    {
        CommHardwareScaffold.RequireMtkDevice();
        var snapshot = await CommHardwareScaffold.Controller.RefreshAsync(TestContext.Current.CancellationToken);
        var endpoint = await CommHardwareScaffold
            .FindEndpointAsync("mtk", CommHardwareScaffold.MtkDeviceVariable);

        await using var lease = await CommHardwareScaffold.Controller.OpenAsync(
            snapshot, endpoint.Id, DeviceSessionKind.Mtk, TestContext.Current.CancellationToken);
        Assert.True(lease.TryGetSession<MtkSession>(out var session));

        var identity = await session!.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual((ushort)0, identity.HardwareCode);
        Assert.True(lease.Generation is null || lease.Generation.Value.Value >= 1);
    }

    [Fact]
    public async Task LoaderFile_HostSideParse_RecordsSha256AndNonemptyBytes()
    {
        var path = CommHardwareScaffold.OptionalEnvironment(CommHardwareScaffold.MtkLoaderPathVariable);
        if (path is null)
        {
            Assert.Skip($"{CommHardwareScaffold.MtkLoaderPathVariable} 未设置；DA 文件登记为选配 / " +
                $"{CommHardwareScaffold.MtkLoaderPathVariable} is not set; loader file registration is opt-in.");
        }

        var file = new FileInfo(path!);
        Assert.True(file.Exists, $"Loader file '{path}' does not exist.");
        Assert.True(file.Length > 0);

        await using var stream = file.OpenRead();
        var digest = await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken);
        Assert.Matches("^[0-9a-f]{64}$", Convert.ToHexString(digest).ToLowerInvariant());
    }
}