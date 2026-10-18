using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Reads both GPT copies of one named XML region under a single operation lease, without repair or primary-only fallback.
    /// <para>在单个操作租约下读取 XML 命名区域的两份 GPT 副本，不进行修复，也不会只回退到主副本。</para></summary>
    /// <remarks>
    /// <para>The XML agent addresses a region by its reported token and a block-aligned byte offset that is already region-relative, and the table is parsed with the block size the agent itself reported. Regions reserved for a separate authenticated protocol are rejected by range validation, and a target that answers with a legacy PMT layout instead of a GPT is reported as a validation failure rather than guessed.</para>
    /// <para>XML 代理按上报的区域标记与已经相对于区域的块对齐字节偏移寻址，分区表按代理自报的块大小解析。 为单独已认证协议保留的区域会被范围校验拒绝；返回旧式 PMT 布局而不是 GPT 的目标按校验失败上报，不做猜测。</para></remarks>
    /// <param name="regionName">Exact XML physical-region token from the discovered geometry, excluding RPMB.
    /// <para>来自已发现几何信息的精确 XML 物理区域标记，不含 RPMB。</para></param>
    /// <param name="timeoutMs">Cooperative deadline for each metadata read, from one millisecond through 24 hours.
    /// <para>每次元数据读取的协作式截止时间，范围从 1 毫秒到 24 小时。</para></param>
    /// <param name="cancellationToken">Cancellation propagated to every storage exchange.
    /// <para>取消信号会传播到每一次存储交换。</para></param>
    /// <returns>A validated snapshot bound to the XML geometry and the selected region.
    /// <para>与 XML 几何信息和所选区域绑定的已校验快照。</para></returns>
    /// <exception cref="InvalidOperationException">XML storage geometry has not been discovered, no XML channel is attached, or the session stage is wrong.
    /// <para>尚未发现 XML 存储几何信息、未附加 XML 通道，或会话阶段不符。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeoutMs"/> is out of bounds or <paramref name="regionName"/> was not reported.
    /// <para><paramref name="timeoutMs"/> 越界，或 <paramref name="regionName"/> 未被上报。</para></exception>
    /// <exception cref="NotSupportedException"><paramref name="regionName"/> names an authenticated region.
    /// <para><paramref name="regionName"/> 指向需认证的区域。</para></exception>
    /// <exception cref="InvalidDataException">A GPT block read was incomplete or the table failed validation.
    /// <para>某个 GPT 块读取不完整，或表未通过校验。</para></exception>
    /// <exception cref="MtkProtocolException">A storage exchange faulted the session.
    /// <para>某次存储交换使会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<MtkXmlGptSnapshot> ReadXmlGptAsync(string regionName, int timeoutMs = 120000,
        CancellationToken cancellationToken = default)
    {
        ValidateXmlSetupTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadXmlGptOwnedAsync(regionName, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<MtkXmlGptSnapshot> ReadXmlGptOwnedAsync(string regionName, int timeoutMs,
        CancellationToken cancellationToken)
    {
        RequireState(MtkSessionState.XmlStorageGeometryKnown);
        cancellationToken.ThrowIfCancellationRequested();
        var storage = XmlStorage ?? throw new InvalidOperationException("XML storage has not been discovered.");
        storage.ValidateRange(regionName, 0, storage.BlockSize);
        RequireXmlOrdinaryRegionSnapshot(regionName);
        var region = storage.Regions.Single(candidate => candidate.Name == regionName);
        var table = await GptReader.ReadAsync(ReadBlocksAsync, storage.BlockSize,
            (ulong)(region.CapacityBytes / storage.BlockSize), cancellationToken).ConfigureAwait(false);
        ThrowIfDisposed();
        return new MtkXmlGptSnapshot(storage, regionName, table);

        async ValueTask ReadBlocksAsync(long offset, Memory<byte> destination, CancellationToken token)
        {
            using var output = new FixedBufferWriteStream(destination);
            await ReadXmlStorageOwnedAsync(regionName, offset, destination.Length, output, timeoutMs, null, token)
                .ConfigureAwait(false);
            if (output.Written != destination.Length)
            {
                throw new InvalidDataException("The XML GPT block read was incomplete.");
            }
        }
    }
}