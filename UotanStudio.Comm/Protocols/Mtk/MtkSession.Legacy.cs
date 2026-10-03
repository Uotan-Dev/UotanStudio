using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Profile;
using UotanStudio.Comm.Protocols.Mtk.Storage;
using UotanStudio.Firmware.VendorFirmware.MediaTek;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private MtkLegacyBootVersions? _legacyBootVersions;
    private MtkLegacyOptions? _legacyOptions;

    /// <summary>Gets the validated Legacy storage geometry, cleared on protocol failure.
    /// <para>获取经过校验的 Legacy 存储几何信息，协议失败时清除。</para></summary>
    /// <value>The storage inventory, or null before successful initialization.
    /// <para>存储清单；初始化成功前为 null。</para></value>
    public MtkLegacyStorageInfo? LegacyStorage { get; private set; }

    /// <summary>Gets the verified completion response of the active Legacy storage session.
    /// <para>获取活动 Legacy 存储会话经过校验的完成响应。</para></summary>
    /// <value>The original status fields and response bytes, or null before startup, after failure or shutdown.
    /// <para>原始状态字段与响应字节；启动前、失败后或关闭后为 null。</para></value>
    public MtkLegacyCompletionInfo? LegacyCompletion { get; private set; }

    /// <summary>Gets the checksum reported for a supplied Legacy DRAM resource.
    /// This is diagnostic data, not an independently verified checksum algorithm.
    /// <para>获取为所提供 Legacy DRAM 资源上报的校验和。这是诊断数据，不代表独立验证了校验算法。</para></summary>
    /// <value>The reported 16-bit value, or null when no DRAM resource was sent.
    /// <para>上报的 16 位值；未发送 DRAM 资源时为 null。</para></value>
    public ushort? LegacyDramReportedChecksum { get; private set; }

    /// <summary>Queries boot versions before uploading a Legacy DA without changing device state.
    /// <para>在上传 Legacy DA 前查询引导版本，不更改设备状态。</para></summary>
    /// <param name="cancellationToken">Cancellation for the serialized query.
    /// <para>串行化查询的取消令牌。</para></param>
    /// <returns>The unmodified FE and FF command responses.
    /// <para>未经修改的 FE 和 FF 命令响应。</para></returns>
    public async Task<MtkLegacyBootVersions> QueryLegacyBootVersionsAsync(CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.Bootloader);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await WriteAsync([0xFE], _timeoutMs, cancellationToken).ConfigureAwait(false);
                var bootloader = (await ReadAsync(1, _timeoutMs, cancellationToken).ConfigureAwait(false))[0];
                await WriteAsync([0xFF], _timeoutMs, cancellationToken).ConfigureAwait(false);
                var bootRom = (await ReadAsync(1, _timeoutMs, cancellationToken).ConfigureAwait(false))[0];
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                return _legacyBootVersions = new(bootRom, bootloader);
            }
            catch
            {
                FaultLegacy();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Initializes a started non-IoT Legacy DA1, uploads the selected DA2 and validates eMMC storage.
    /// <para>初始化已启动的非 IoT Legacy DA1，上传选定的 DA2 并校验 eMMC 存储。</para></summary>
    /// <remarks>Boot versions must be queried before starting DA1. The caller owns the DA2 source.
    /// No loader patching, authorization bypass, storage writes, USB speed change or protocol fallback is performed.
    /// DRAM setup uses only the supplied resource when requested by the device.
    /// <para>必须在启动 DA1 前查询引导版本。调用方持有 DA2 源。不执行加载器补丁、授权绕过、
    /// 存储写入、USB 提速或协议回退。设备请求 DRAM 设置时，仅使用所提供的资源。</para></remarks>
    /// <param name="stageTwo">The readable stream positioned at the selected complete DA2 region.
    /// <para>定位到选定完整 DA2 区域的可读流。</para></param>
    /// <param name="loadAddress">The aligned 32-bit DA2 load address.
    /// <para>对齐的 32 位 DA2 加载地址。</para></param>
    /// <param name="length">The positive region length, at most 64 MiB.
    /// <para>正数区域长度，最多 64 MiB。</para></param>
    /// <param name="options">The explicitly selected non-IoT wire profile and runtime configuration.
    /// <para>显式选定的非 IoT 线协议配置和运行时设置。</para></param>
    /// <param name="emi">The selected DRAM resource, required only if the device requests it.
    /// <para>选定的 DRAM 资源，仅当设备请求时必需。</para></param>
    /// <param name="cancellationToken">Cancellation for the complete startup.
    /// <para>完整启动过程的取消令牌。</para></param>
    /// <returns>The validated storage geometry after the final agent status.
    /// <para>最终代理状态确认后经过校验的存储几何信息。</para></returns>
    public Task<MtkLegacyStorageInfo> InitializeLegacyAgentAsync(Stream stageTwo, uint loadAddress, int length,
        MtkLegacyOptions options, MtkEmiParameters? emi = null, CancellationToken cancellationToken = default) =>
        InitializeLegacyAgentAsync(stageTwo, loadAddress, length, MtkXFlashStorageKind.Emmc, options, emi, cancellationToken);

    /// <summary>Initializes a non-IoT Legacy agent for explicitly selected eMMC, NOR or NAND storage.
    /// <para>为显式选择的 eMMC、NOR 或 NAND 存储初始化非 IoT Legacy 代理。</para></summary>
    /// <param name="stageTwo">The caller-owned readable complete DA2 stream.
    /// <para>调用方持有的完整可读 DA2 流。</para></param>
    /// <param name="loadAddress">The aligned 32-bit load address.
    /// <para>对齐的 32 位加载地址。</para></param>
    /// <param name="length">The DA2 byte length, at most 64 MiB.
    /// <para>DA2 字节长度，最多 64 MiB。</para></param>
    /// <param name="storageKind">The required storage family; no automatic fallback occurs.
    /// <para>要求的存储类型，不自动回退。</para></param>
    /// <param name="options">The explicit non-IoT wire profile.
    /// <para>显式的非 IoT 线协议配置。</para></param>
    /// <param name="emi">The DRAM resource used only when requested.
    /// <para>仅当设备请求时使用的 DRAM 资源。</para></param>
    /// <param name="cancellationToken">Cancellation for the complete startup.
    /// <para>完整启动过程的取消令牌。</para></param>
    /// <returns>The selected geometry after successful final agent status.
    /// <para>最终代理状态成功后的选定几何信息。</para></returns>
    public async Task<MtkLegacyStorageInfo> InitializeLegacyAgentAsync(Stream stageTwo, uint loadAddress, int length,
        MtkXFlashStorageKind storageKind, MtkLegacyOptions options, MtkEmiParameters? emi = null,
        CancellationToken cancellationToken = default)
    {
        if (storageKind is not (MtkXFlashStorageKind.Emmc or MtkXFlashStorageKind.Nor or MtkXFlashStorageKind.Nand))
        {
            throw new NotSupportedException("The Legacy startup supports explicit eMMC, NOR or NAND selection.");
        }
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        MtkXFlashChannel.ValidateSource(stageTwo, length);
        ValidateLegacyEmi(emi);
        if ((loadAddress & 3) != 0 || (ulong)loadAddress + (uint)length > (ulong)uint.MaxValue + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(loadAddress));
        }
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.AgentStarted);
            var versions = _legacyBootVersions ?? throw new InvalidOperationException("Query Legacy boot versions before starting DA1.");
            var runtime = options.BuildRuntime(DeviceInfo!.HardwareCode, versions);
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.StartupTimeoutMs);
            var token = deadline.Token;
            try
            {
                await RequireLegacyByteAsync(0xC0, "DA1 synchronization", token).ConfigureAwait(false);
                _ = await ReadAsync(4, _timeoutMs, token).ConfigureAwait(false);
                await ReadLegacyNandIdsAsync(token).ConfigureAwait(false);
                _ = await ReadAsync(20, _timeoutMs, token).ConfigureAwait(false);
                await WriteAsync([0x5A], _timeoutMs, token).ConfigureAwait(false);
                var response = await ReadAsync(3, _timeoutMs, token).ConfigureAwait(false);
                var observed = Convert.ToHexString(response);
                if (options.StageOneResponseHex is { } expected
                    && !response.AsSpan().SequenceEqual(Convert.FromHexString(expected)))
                {
                    throw new MtkProtocolException(
                        $"The Legacy DA1 response does not match the selected loader profile; the agent sent {observed} and the profile asserts {expected}.");
                }
                _state = MtkSessionState.LegacyStageOneReady;
                await WriteAsync(runtime, _timeoutMs, token).ConfigureAwait(false);
                var status = await ReadLegacyWordAsync(token).ConfigureAwait(false);
                if (status == 0xBC3)
                {
                    await InitializeLegacyDramOwnedAsync(emi, token).ConfigureAwait(false);
                }
                else if (status != 0)
                {
                    throw new MtkProtocolException("Legacy runtime setup failed.", status);
                }
                else if (DeviceInfo.HardwareCode == 0x6592)
                {
                    _ = await ReadAsync(20, _timeoutMs, token).ConfigureAwait(false);
                }
                _state = MtkSessionState.LegacyRuntimeReady;
                await UploadLegacyStageTwoOwnedAsync(stageTwo, loadAddress, length, options.AgentPacketLength, token).ConfigureAwait(false);
                var storage = await ReadLegacyStorageOwnedAsync(options, storageKind, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _legacyOptions = options;
                _evidence.Add(new ProtocolEvidence
                {
                    Protocol = "mtk",
                    Phase = "legacy-da-profile",
                    Outcome = CommandOutcomeKind.Completed,
                    Detail = MtkLegacyDaProfile.From(options, storageKind, observed).Describe()
                });
                LegacyStorage = storage;
                _state = MtkSessionState.LegacyStorageReady;
                PublishStorageCapability(MtkStorageDiscoverySource.Legacy,
                    (generation, evidence) => MtkStorageCapabilitySnapshot.FromLegacy(storage, generation, evidence));
                return storage;
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultLegacy();
                throw new TimeoutException("The Legacy startup deadline expired; reconnect without replaying the failed exchange.", error);
            }
            catch
            {
                FaultLegacy();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task UploadLegacyStageTwoOwnedAsync(Stream source, uint address, int length, int packetLength, CancellationToken token)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(header, address);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)packetLength);
        await WriteAsync(header, _timeoutMs, token).ConfigureAwait(false);
        await RequireLegacyByteAsync(0x5A, "DA2 header", token).ConfigureAwait(false);
        var buffer = new byte[Math.Min(packetLength, length)];
        for (var transferred = 0; transferred < length;)
        {
            var count = Math.Min(buffer.Length, length - transferred);
            await source.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            await WriteAsync(buffer, count, _timeoutMs, token).ConfigureAwait(false);
            await RequireLegacyByteAsync(0x5A, "DA2 packet", token).ConfigureAwait(false);
            transferred += count;
        }
        await WriteAsync([0x5A], _timeoutMs, token).ConfigureAwait(false);
        await RequireLegacyByteAsync(0x5A, "DA2 completion", token).ConfigureAwait(false);
    }

    private async Task<MtkLegacyStorageInfo> ReadLegacyStorageOwnedAsync(MtkLegacyOptions options,
        MtkXFlashStorageKind storageKind, CancellationToken token)
    {
        var nor = await ReadAsync(28, _timeoutMs, token).ConfigureAwait(false);
        var nand = await ReadAsync(options.NandSize64Bit ? 17 : 13, _timeoutMs, token).ConfigureAwait(false);
        var count = BinaryPrimitives.ReadUInt16BigEndian(nand.AsSpan(nand.Length - 2));
        if (count > 256)
        {
            throw new MtkProtocolException("The Legacy NAND ID count exceeds the supported limit.");
        }
        var nandIds = await ReadAsync(count * 2, _timeoutMs, token).ConfigureAwait(false);
        var nandDetails = await ReadAsync(9, _timeoutMs, token).ConfigureAwait(false);
        var emmc = await ReadAsync(92, _timeoutMs, token).ConfigureAwait(false);
        _ = await ReadAsync(28, _timeoutMs, token).ConfigureAwait(false);
        var configuration = await ReadAsync(38, _timeoutMs, token).ConfigureAwait(false);
        var sramStatus = BinaryPrimitives.ReadUInt32BigEndian(configuration);
        var dramStatus = BinaryPrimitives.ReadUInt32BigEndian(configuration.AsSpan(8));
        if (sramStatus != 0 || dramStatus != 0)
        {
            throw new MtkProtocolException("Legacy RAM configuration failed.", sramStatus != 0 ? sramStatus : dramStatus);
        }
        if (DeviceInfo!.HardwareCode is 0x8127 or 0x8163)
        {
            var status = await ReadLegacyWordAsync(token).ConfigureAwait(false);
            if (status != 0)
            {
                throw new MtkProtocolException("Legacy storage initialization failed.", status);
            }
        }
        var completion = await ReadLegacyCompletionOwnedAsync(options, token).ConfigureAwait(false);
        var storage = storageKind switch
        {
            MtkXFlashStorageKind.Nor => MtkLegacyStorageInfo.ParseNor(nor),
            MtkXFlashStorageKind.Nand => MtkLegacyStorageInfo.ParseNand(nand, nandIds, nandDetails, options.NandSize64Bit),
            _ => MtkLegacyStorageInfo.Parse(emmc)
        };
        LegacyCompletion = completion;
        return storage;
    }

    private async Task<MtkLegacyCompletionInfo> ReadLegacyCompletionOwnedAsync(MtkLegacyOptions options, CancellationToken token)
    {
        var marker = (await ReadAsync(1, options.StartupTimeoutMs, token).ConfigureAwait(false))[0];
        var waited = marker == 0xA5 && options.CompletionProfile == MtkLegacyCompletionProfile.SdkPostProcessWait;
        if (waited)
        {
            marker = (await ReadAsync(1, options.StartupTimeoutMs, token).ConfigureAwait(false))[0];
        }
        if (marker != 0x5A)
        {
            throw new MtkProtocolException("The Legacy completion marker does not match the selected startup profile.", marker);
        }
        var fields = await ReadAsync(9, _timeoutMs, token).ConfigureAwait(false);
        var status = BinaryPrimitives.ReadUInt32BigEndian(fields);
        if (status != 0 || fields[8] != 0xC1)
        {
            throw new MtkProtocolException("Legacy initialization did not finish with a successful download status and SOC_OK.", status);
        }
        var raw = new byte[waited ? 11 : 10];
        if (waited)
        {
            raw[0] = 0xA5;
        }
        raw[^10] = 0x5A;
        fields.CopyTo(raw, raw.Length - fields.Length);
        return new(waited, status, BinaryPrimitives.ReadUInt32BigEndian(fields.AsSpan(4)), Convert.ToHexString(raw));
    }

    private async Task ReadLegacyNandIdsAsync(CancellationToken token)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(await ReadAsync(2, _timeoutMs, token).ConfigureAwait(false));
        if (count > 256)
        {
            throw new MtkProtocolException("The Legacy NAND ID count exceeds the supported limit.");
        }
        _ = await ReadAsync(count * 2, _timeoutMs, token).ConfigureAwait(false);
    }

    private async Task RequireLegacyByteAsync(byte expected, string operation, CancellationToken token)
    {
        var value = (await ReadAsync(1, _timeoutMs, token).ConfigureAwait(false))[0];
        if (value != expected)
        {
            throw new MtkProtocolException($"Legacy {operation} returned 0x{value:X2}; expected 0x{expected:X2}.", value);
        }
    }

    private async Task<uint> ReadLegacyWordAsync(CancellationToken token) =>
        BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(4, _timeoutMs, token).ConfigureAwait(false));

    private void FaultLegacy()
    {
        LegacyStorage = null;
        LegacyCompletion = null;
        LegacyDramReportedChecksum = null;
        _legacyOptions = null;
        ClearStorageCapability(MtkStorageDiscoverySource.Legacy);
        _state = MtkSessionState.Faulted;
    }
}