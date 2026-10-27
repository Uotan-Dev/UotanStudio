using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Boot ROM identity queries over the boot byte protocol: the bootloader version, Boot ROM version,
/// media device identifier and secure on-chip identifier exchanges this client issues right
/// after the target configuration word.
/// <para>boot 字节协议之上的 Boot ROM 身份查询：本客户端在目标配置字之后立即发起的 bootloader 版本、
/// Boot ROM 版本、媒体设备标识与安全片上标识交互。</para>
/// </summary>
/// <remarks>
/// Every framing is a transcription of :
/// <c>get_blver</c> (lines 664-673) writes command 0xFE and reads one version byte where the value
/// 0xFE itself marks Boot ROM mode, <c>get_bromver</c> (lines 657-663) writes command 0xFF and reads
/// one version byte, and <c>get_meid</c> (lines 932-958) / <c>get_socid</c> (lines 960-987) probe
/// command 0xFE first, then write command 0xE1 or 0xE7, require the one-byte command echo, read the
/// big-endian payload length, the payload itself and a little-endian two-byte completion status. The
/// initialization path reuses the bootloader version it already read instead of probing 0xFE once more
/// for each identifier.
/// During <c>InitializeAsync</c> every query is best-effort and keeps initializing when a target
/// stays silent: a failed or refused query degrades the matching
/// identity field to null, records evidence and never faults the session. The explicit public
/// queries keep the session-wide discipline instead: a refused echo, an oversized declaration or a
/// rejected status faults the session so no further command byte is exchanged.
/// <para>此处每个帧结构都转录自 ：<c>get_blver</c>（第 664-673 行）
/// 写入命令 0xFE 并读取一个版本字节，取值 0xFE 本身即标记 Boot ROM 模式；<c>get_bromver</c>（第 657-663 行）
/// 写入命令 0xFF 并读取一个版本字节；<c>get_meid</c>（第 932-958 行）与 <c>get_socid</c>（第 960-987 行）
/// 先用命令 0xFE 探测，再写入命令 0xE1 或 0xE7，要求单字节命令回显，随后读取大端载荷长度、载荷本体与
/// 小端两字节完成状态。初始化路径复用已读取的 bootloader 版本，不再为每个标识重复探测 0xFE。
/// 在 <c>InitializeAsync</c> 中每个查询都是尽力而为，目标沉默时仍继续初始化：
/// 失败或被拒绝的查询只把对应的身份字段降级为 null、记录证据，绝不让会话进入 fault。显式公开查询则保持
/// 会话级纪律：被拒绝的回显、超限的长度声明或被拒绝的状态会使会话进入 fault，不再交换任何命令字节。</para>
/// </remarks>
public sealed partial class MtkSession
{
    private const byte BootIdentityProbeCommand = 0xFE;
    private const byte BootIdentityBromVersionCommand = 0xFF;
    private const byte BootIdentityGetMeidCommand = 0xE1;
    private const byte BootIdentityGetSocidCommand = 0xE7;
    private const byte BootIdentityProbeUnsupportedMaximum = 2;
    private const int MaximumBootIdentityBytes = 0x100;

    /// <summary>Reads the bootloader version byte through command FE.
    /// <para>通过命令 FE 读取 bootloader 版本字节。</para></summary>
    /// <remarks>Mirrors <c>get_blver</c>: the
    /// single reply byte is the version, and the value 0xFE is the documented Boot ROM mode marker
    /// rather than an error. A refused or truncated exchange faults the session, unlike the non-fatal
    /// behaviour of the same query inside <c>InitializeAsync</c>. 对应 <c>get_blver</c>：单个回复字节即版本， 取值 0xFE 是已记载的 Boot ROM 模式标记而非错误。与
    /// <c>InitializeAsync</c>中同一查询的非致命行为 不同，被拒绝或截断的交互会使会话进入 fault 状态。</remarks>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The bootloader version byte exactly as delivered.
    /// <para>按交付原样返回的 bootloader 版本字节。</para></returns>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">The reply was truncated; the session faults.
    /// <para>回复被截断；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<byte> GetBootLoaderVersionAsync(CancellationToken cancellationToken = default)
    {
        RequirePreloaderCommandStage();
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var version = await ReadBootLoaderVersionOwnedAsync(cancellationToken).ConfigureAwait(false);
                StoreBootIdentity(bootLoaderVersion: version);
                RecordSecurityEvidence("boot-identity", CommandOutcomeKind.Completed,
                    $"Bootloader version 0x{version:X2}.");
                return version;
            }
            catch
            {
                FaultPreloaderCommands();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Reads the Boot ROM version byte through command FF.
    /// <para>通过命令 FF 读取 Boot ROM 版本字节。</para></summary>
    /// <remarks>Mirrors <c>get_bromver</c>. A
    /// refused or truncated exchange faults the session, unlike the non-fatal behaviour of the same
    /// query inside <c>InitializeAsync</c>. 对应 <c>get_bromver</c>。与 <c>InitializeAsync</c>
    /// 中同一查询的非致命行为不同，被拒绝或截断的交互会使会话进入 fault 状态。</remarks>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The Boot ROM version byte exactly as delivered.
    /// <para>按交付原样返回的 Boot ROM 版本字节。</para></returns>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">The reply was truncated; the session faults.
    /// <para>回复被截断；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<byte> GetBromVersionAsync(CancellationToken cancellationToken = default)
    {
        RequirePreloaderCommandStage();
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var version = await ReadBromVersionOwnedAsync(cancellationToken).ConfigureAwait(false);
                StoreBootIdentity(bromVersion: version);
                RecordSecurityEvidence("boot-identity", CommandOutcomeKind.Completed,
                    $"Boot ROM version 0x{version:X2}.");
                return version;
            }
            catch
            {
                FaultPreloaderCommands();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Reads the media device identifier through the E1 command exchange.
    /// <para>通过 E1 命令交互读取媒体设备标识。</para></summary>
    /// <remarks>Mirrors <c>get_meid</c>: the 0xFE
    /// probe reply decides whether the identifier exchange is attempted at all — the exchange is skipped
    /// when the probe reports a bootloader version of two or below — and the payload length declared by
    /// the target is bounded before any buffer is allocated. The completion status is the one
    /// little-endian word of the framing. A null result means the target does not report the identifier;
    /// a refused echo, an oversized declaration or a rejected status faults the session.
    /// <para>对应 <c>get_meid</c>：0xFE 探测回复决定是否发起标识交互——探测报告的 bootloader 版本不大于 2 时
    /// 直接跳过——目标声明的载荷长度在分配任何缓冲之前先做上限校验，完成状态采用该帧结构中的唯一一个小端字。
    /// null 表示目标不上报该标识；被拒绝的回显、超限的长度声明或被拒绝的状态会使会话进入 fault 状态。</para></remarks>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The identifier bytes, or null when the probe reports no identifier support.
    /// <para>标识字节；探测报告不支持该标识时为 null。</para></returns>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">The echo was refused, the declared length exceeds the identity bound or the status was rejected; the session faults.
    /// <para>回显被拒绝、声明的长度超过身份上限或状态被拒绝；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<byte[]?> GetMeidAsync(CancellationToken cancellationToken = default)
    {
        RequirePreloaderCommandStage();
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var meid = await QueryBootIdentityBlobOwnedAsync(BootIdentityGetMeidCommand, "MEID", null,
                    cancellationToken).ConfigureAwait(false);
                if (meid is not null)
                {
                    StoreBootIdentity(meid: meid);
                }

                RecordSecurityEvidence("boot-identity", CommandOutcomeKind.Completed, DescribeIdentityBlob(meid));
                return meid;
            }
            catch
            {
                FaultPreloaderCommands();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Reads the secure on-chip identifier through the E7 command exchange.
    /// <para>通过 E7 命令交互读取安全片上标识。</para></summary>
    /// <remarks>Mirrors <c>get_socid</c> with the
    /// same probe, echo, bounded big-endian length and little-endian status framing as the MEID
    /// exchange. A null result means the target does not report the identifier; a refused echo, an
    /// oversized declaration or a rejected status faults the session. 对应 <c>get_socid</c>，探测、回显、有界大端 长度与小端状态帧结构与 MEID 交互一致。null
    /// 表示目标不上报该标识；被拒绝的回显、超限的长度声明或 被拒绝的状态会使会话进入 fault 状态。</remarks>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The identifier bytes, or null when the probe reports no identifier support.
    /// <para>标识字节；探测报告不支持该标识时为 null。</para></returns>
    /// <exception cref="InvalidOperationException">The session is not in the preloader command stage.
    /// <para>会话不处于 preloader 命令阶段。</para></exception>
    /// <exception cref="MtkProtocolException">The echo was refused, the declared length exceeds the identity bound or the status was rejected; the session faults.
    /// <para>回显被拒绝、声明的长度超过身份上限或状态被拒绝；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task<byte[]?> GetSocidAsync(CancellationToken cancellationToken = default)
    {
        RequirePreloaderCommandStage();
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var socid = await QueryBootIdentityBlobOwnedAsync(BootIdentityGetSocidCommand, "SOCID", null,
                    cancellationToken).ConfigureAwait(false);
                if (socid is not null)
                {
                    StoreBootIdentity(socid: socid);
                }

                RecordSecurityEvidence("boot-identity", CommandOutcomeKind.Completed, DescribeIdentityBlob(socid));
                return socid;
            }
            catch
            {
                FaultPreloaderCommands();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Runs the four identity queries once and reports what the target answered.
    /// <para>一次性运行四个身份查询，并报告目标的应答情况。</para></summary>
    /// <remarks>
    /// Every query is individually best-effort: a refused or silent target only leaves the matching
    /// field null and records evidence, so an identity getter failure never aborts the walk. Only a caller
    /// cancellation propagates.
    /// <para>每个查询都单独尽力而为：目标拒绝或沉默只会让对应字段保持 null 并记录证据，身份查询失败不会中止
    /// 遍历。只有调用方取消会向外传播。</para>
    /// </remarks>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The answered identity fields; unanswered fields stay null.
    /// <para>已应答的身份字段；未应答的字段保持 null。</para></returns>
    internal async Task<(byte? BootLoaderVersion, byte? BromVersion, byte[]? Meid, byte[]? Socid)>
        QueryBootIdentityOwnedAsync(CancellationToken cancellationToken)
    {
        byte? bootLoaderVersion = null;
        byte? bromVersion = null;
        byte[]? meid = null;
        byte[]? socid = null;
        try
        {
            bootLoaderVersion = await ReadBootLoaderVersionOwnedAsync(cancellationToken).ConfigureAwait(false);
            RecordSecurityEvidence("boot-identity", CommandOutcomeKind.Completed,
                $"Bootloader version 0x{bootLoaderVersion:X2}.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordSecurityEvidence("boot-identity", ClassifyUploadOutcome(error),
                $"The bootloader version query did not complete: {error.Message}");
        }

        try
        {
            bromVersion = await ReadBromVersionOwnedAsync(cancellationToken).ConfigureAwait(false);
            RecordSecurityEvidence("boot-identity", CommandOutcomeKind.Completed,
                $"Boot ROM version 0x{bromVersion:X2}.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordSecurityEvidence("boot-identity", ClassifyUploadOutcome(error),
                $"The Boot ROM version query did not complete: {error.Message}");
        }

        try
        {
            meid = await QueryBootIdentityBlobOwnedAsync(BootIdentityGetMeidCommand, "MEID", bootLoaderVersion,
                    cancellationToken)
                .ConfigureAwait(false);
            RecordSecurityEvidence("boot-identity", CommandOutcomeKind.Completed, DescribeIdentityBlob(meid));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordSecurityEvidence("boot-identity", ClassifyUploadOutcome(error),
                $"The MEID query did not complete: {error.Message}");
        }

        try
        {
            socid = await QueryBootIdentityBlobOwnedAsync(BootIdentityGetSocidCommand, "SOCID", bootLoaderVersion,
                    cancellationToken)
                .ConfigureAwait(false);
            RecordSecurityEvidence("boot-identity", CommandOutcomeKind.Completed, DescribeIdentityBlob(socid));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordSecurityEvidence("boot-identity", ClassifyUploadOutcome(error),
                $"The SOCID query did not complete: {error.Message}");
        }

        return (bootLoaderVersion, bromVersion, meid, socid);
    }

    /// <summary>Writes the bootloader version probe and returns the single reply byte.
    /// <para>写入 bootloader 版本探测并返回单个回复字节。</para></summary>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The reply byte; 0xFE marks Boot ROM mode. 回复字节；0xFE 标记 Boot ROM 模式。</returns>
    private async Task<byte> ReadBootLoaderVersionOwnedAsync(CancellationToken cancellationToken)
    {
        await WriteAsync([BootIdentityProbeCommand], _timeoutMs, cancellationToken).ConfigureAwait(false);
        return (await ReadAsync(1, _timeoutMs, cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>Writes the Boot ROM version command and returns the single reply byte.
    /// <para>写入 Boot ROM 版本命令并返回单个回复字节。</para></summary>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The reply byte. 回复字节。</returns>
    private async Task<byte> ReadBromVersionOwnedAsync(CancellationToken cancellationToken)
    {
        await WriteAsync([BootIdentityBromVersionCommand], _timeoutMs, cancellationToken).ConfigureAwait(false);
        return (await ReadAsync(1, _timeoutMs, cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>Runs one identifier exchange behind the documented 0xFE probe.
    /// <para>在已记载的 0xFE 探测之后运行一次标识交互。</para></summary>
    /// <param name="identityCommand">The identifier command byte, 0xE1 or 0xE7.
    /// <para>标识命令字节，0xE1 或 0xE7。</para></param>
    /// <param name="operation">The neutral operation name used in errors.
    /// <para>错误信息中使用的与本地化无关的操作名。</para></param>
    /// <param name="knownBootLoaderVersion">The bootloader version byte already read by the caller, so the
    /// 0xFE probe is not repeated; <see langword="null"/> probes once.
    /// <para>调用方已读取的 bootloader 版本字节，因此不再重复 0xFE 探测；<see langword="null"/> 时探测一次。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The identifier bytes, or null when the probe reports no support.
    /// <para>标识字节；探测报告不支持时为 null。</para></returns>
    private async Task<byte[]?> QueryBootIdentityBlobOwnedAsync(byte identityCommand, string operation,
        byte? knownBootLoaderVersion, CancellationToken cancellationToken)
    {
        var probe = knownBootLoaderVersion
            ?? await ReadBootLoaderVersionOwnedAsync(cancellationToken).ConfigureAwait(false);
        if (probe <= BootIdentityProbeUnsupportedMaximum)
        {
            return null;
        }

        return await ReadBootIdentityBlobOwnedAsync(identityCommand, operation, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Runs one identifier exchange: command echo, bounded big-endian length, payload and
    /// little-endian status. <para>运行一次标识交互：命令回显、有界大端长度、载荷与小端状态。</para></summary>
    /// <param name="identityCommand">The identifier command byte, 0xE1 or 0xE7.
    /// <para>标识命令字节，0xE1 或 0xE7。</para></param>
    /// <param name="operation">The neutral operation name used in errors.
    /// <para>错误信息中使用的与本地化无关的操作名。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>The identifier bytes exactly as delivered. 按交付原样返回的标识字节。</returns>
    private async Task<byte[]> ReadBootIdentityBlobOwnedAsync(byte identityCommand, string operation,
        CancellationToken cancellationToken)
    {
        await WriteAsync([identityCommand], _timeoutMs, cancellationToken).ConfigureAwait(false);
        var echo = await ReadAsync(1, _timeoutMs, cancellationToken).ConfigureAwait(false);
        if (echo[0] != identityCommand)
        {
            throw new MtkProtocolException($"The {operation} command echo did not match.");
        }

        var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(
            await ReadAsync(4, _timeoutMs, cancellationToken).ConfigureAwait(false));
        if (declaredLength > MaximumBootIdentityBytes)
        {
            throw new MtkProtocolException(
                $"The declared {operation} length {declaredLength} exceeds the identity bound " +
                $"{MaximumBootIdentityBytes}.");
        }

        var payload = await ReadAsync(checked((int)declaredLength), _timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        var status = BinaryPrimitives.ReadUInt16LittleEndian(
            await ReadAsync(2, _timeoutMs, cancellationToken).ConfigureAwait(false));
        if (status != 0)
        {
            throw new MtkProtocolException($"The target rejected the {operation} query (status 0x{status:X4}).",
                status);
        }

        return payload;
    }

    /// <summary>Builds the neutral evidence line for one identifier query outcome.
    /// <para>为一次标识查询结果构造与本地化无关的证据行。</para></summary>
    /// <param name="payload">The answered bytes, or null. 已应答的字节，可为 null。</param>
    /// <returns>The evidence detail text. 证据详情文本。</returns>
    private static string DescribeIdentityBlob(byte[]? payload) => payload is null
        ? "The target does not report the identifier."
        : $"Identifier {Convert.ToHexString(payload)} ({payload.Length} byte(s)).";

    /// <summary>Merges answered identity fields into the stored device identity.
    /// <para>把已应答的身份字段合并进已存储的设备身份。</para></summary>
    /// <param name="bootLoaderVersion">The newly answered bootloader version, or null.
    /// <para>新应答的 bootloader 版本，可为 null。</para></param>
    /// <param name="bromVersion">The newly answered Boot ROM version, or null.
    /// <para>新应答的 Boot ROM 版本，可为 null。</para></param>
    /// <param name="meid">The newly answered identifier, or null.
    /// <para>新应答的标识，可为 null。</para></param>
    /// <param name="socid">The newly answered on-chip identifier, or null.
    /// <para>新应答的片上标识，可为 null。</para></param>
    private void StoreBootIdentity(byte? bootLoaderVersion = null, byte? bromVersion = null,
        byte[]? meid = null, byte[]? socid = null)
    {
        if (DeviceInfo is not { } current)
        {
            return;
        }

        DeviceInfo = current with
        {
            BlVersion = bootLoaderVersion ?? current.BlVersion,
            BromVersion = bromVersion ?? current.BromVersion,
            Meid = meid ?? current.Meid,
            Socid = socid ?? current.Socid
        };
    }
}