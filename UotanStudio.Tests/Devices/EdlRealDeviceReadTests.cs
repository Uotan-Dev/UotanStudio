using System.Security.Cryptography;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Edl;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Opt-in L2/L3 read-only evidence for a real Qualcomm EDL device. Discovery, protocol probing,
/// Firehose configure, and a single GPT header sector read are exercised; programmer upload,
/// program, erase, patch, wipe, provisioning, and reset are never called. Without a Sahara- or
/// Firehose-mode device on the named USB id every test skips with the gate variable in its reason.
/// <para>针对真实高通 EDL 设备的选配 L2/L3 只读证据。仅执行发现、协议探测、Firehose 配置与
/// 单个 GPT 头扇区读取；绝不上传 programmer、不写入、不擦除、不 patch、不 wipe、不 provisioning、
/// 不复位。指定 USB  id 上没有 Sahara/Firehose 设备时，所有用例带着门控变量原因跳过。</para>
/// </summary>
public sealed class EdlRealDeviceReadTests
{
    [Fact]
    public async Task Discovery_NamedEdlEndpoint_IsPresentAndDirectUsb()
    {
        CommHardwareScaffold.RequireEdlDevice();
        var endpoint = await CommHardwareScaffold
            .FindEndpointAsync("edl", CommHardwareScaffold.EdlDeviceVariable);

        Assert.Equal("edl", endpoint.Protocol);
        Assert.Equal("direct-usb", endpoint.Route);
    }

    [Fact]
    public async Task Probe_OnLiveEndpoint_VerifiesSaharaOrFirehose()
    {
        CommHardwareScaffold.RequireEdlDevice();
        var snapshot = await CommHardwareScaffold.Controller.RefreshAsync(TestContext.Current.CancellationToken);
        var endpoint = await CommHardwareScaffold
            .FindEndpointAsync("edl", CommHardwareScaffold.EdlDeviceVariable);

        await using var lease = await CommHardwareScaffold.Controller.OpenAsync(
            snapshot, endpoint.Id, DeviceSessionKind.Edl, TestContext.Current.CancellationToken);
        Assert.True(lease.TryGetSession<EdlSession>(out var session));
        var state = await session!.ProbeProtocolAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(EdlProtocolState.Unknown, state);
    }

    [Fact]
    public async Task GptPrimaryHeader_OnFirehoseMode_ReadsOneSectorWithoutMutation()
    {
        CommHardwareScaffold.RequireEdlDevice();
        var snapshot = await CommHardwareScaffold.Controller.RefreshAsync(TestContext.Current.CancellationToken);
        var endpoint = await CommHardwareScaffold
            .FindEndpointAsync("edl", CommHardwareScaffold.EdlDeviceVariable);

        await using var lease = await CommHardwareScaffold.Controller.OpenAsync(
            snapshot, endpoint.Id, DeviceSessionKind.Edl, TestContext.Current.CancellationToken);
        Assert.True(lease.TryGetSession<EdlSession>(out var session));
        if (await session!.ProbeProtocolAsync(TestContext.Current.CancellationToken) != EdlProtocolState.Firehose)
        {
            Assert.Skip("The attached device is in Sahara mode; reading storage requires a device already " +
                "running a Firehose programmer, and this suite never uploads one. / 设备处于 Sahara 模式；" +
                "本套件绝不上传 programmer，因此跳过存储读取。");
        }

        var (response, data) = await session.ReadPartitionAsync(1, 1,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.IsAck, response.RawValue ?? "GPT header read was not acknowledged.");
        Assert.Equal("EFI PART", System.Text.Encoding.ASCII.GetString(data, 0, 8));
    }

    [Fact]
    public async Task ProgrammerDirectory_BundledCandidates_HaveStableSha256AndSize()
    {
        var directory = CommHardwareScaffold.OptionalEnvironment(CommHardwareScaffold.EdlProgrammerDirectoryVariable);
        if (directory is null)
        {
            Assert.Skip($"{CommHardwareScaffold.EdlProgrammerDirectoryVariable} 未设置；programmer 目录哈希登记为选配 / " +
                $"{CommHardwareScaffold.EdlProgrammerDirectoryVariable} is not set; programmer hashing is opt-in.");
        }

        var candidates = Directory.EnumerateFiles(directory!, "*.elf", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(candidates);

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in candidates)
        {
            await using var stream = File.OpenRead(path);
            var digest = await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken);
            var key = $"{Path.GetFileName(path)}:{new FileInfo(path).Length}";
            Assert.True(hashes.TryAdd(key, Convert.ToHexString(digest).ToLowerInvariant()),
                $"Duplicate programmer identity '{key}'.");
            Assert.Matches("^[0-9a-f]{64}$", hashes[key]);
        }
    }
}
