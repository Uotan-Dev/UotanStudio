using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Sla;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>An explicitly selected bootloader security resource, not a credential source or bypass.
/// <para>显式选定的 bootloader 安全资源，不是凭据来源，也不是绕过手段。</para></summary>
public enum MtkBootResourceKind
{
    /// <summary>A vendor-issued root certificate sent with boot command E0.
    /// <para>随启动命令 E0 发送的厂商签发根证书。</para></summary>
    RootCertificate,
    /// <summary>A vendor-issued authentication resource sent with boot command E2.
    /// <para>随启动命令 E2 发送的厂商签发认证资源。</para></summary>
    Authentication
}

/// <summary>
/// One MTK boot-protocol session over an already selected transport, driving bootloader,
/// download-agent and XFlash/XML stages under a single exclusive operation lease.
/// <para>在已选定传输之上运行的单个 MTK 启动协议会话，在唯一的排他操作租约下驱动
/// bootloader、download agent 与 XFlash/XML 各阶段。</para>
/// </summary>
public sealed partial class MtkSession
{
    private const int NoAuthenticationNeededStatus = 0x1D0C;

    /// <summary>
    /// Uploads an explicitly authorized security resource using the COM byte-stream wire
    /// profile. A successful exchange leaves the bootloader stage unchanged and does not
    /// prove SLA completion or permission to execute a particular download agent.
    /// <para>使用 COM 字节流线路配置上传显式授权的安全资源。交换成功不会改变
    /// bootloader 阶段，也不证明 SLA 已完成或获准执行某个 download agent。</para>
    /// </summary>
    /// <remarks>
    /// The caller must select the byte-stream profile, not a USB implementation requiring
    /// packet-size-dependent writes or explicit zero-length packets. No resource is fetched,
    /// padded, patched or retried. A device-reported checksum of zero, or one that differs from the
    /// computed value, is kept as warning evidence and does not fail the exchange, matching the
    /// reference upload. A status of <c>0x1D0C</c> on the authentication command means the target
    /// needs no authentication; the exchange completes without uploading when that is the initial status,
    /// or after the upload when the completion frame reports it.
    /// Default bootstrap and agent upload do not invoke this method. Bootstrap invokes it
    /// only for explicitly selected kinds with a configured byte-stream resource provider.
    /// <para>调用方必须选择字节流配置，而不是需要按包长写入或显式零长度包的 USB 实现。不会抓取、填充、修补或重试任何资源。
    /// 设备上报的校验和为零或与计算值不同时，仅作为 warning 证据保留，不会使交换失败，与参考上传一致。
    /// 认证命令返回 <c>0x1D0C</c> 表示目标无需认证：若该状态出现在初始应答则不上传资源即完成，
    /// 若出现在上传后的完成帧也同样按完成处理。默认引导与代理上传不会调用本方法；
    /// 仅当显式选定资源种类并配置了字节流资源提供程序时，引导流程才会调用。</para>
    /// </remarks>
    /// <param name="kind">Certificate or authentication resource command.
    /// <para>证书或认证资源命令。</para></param>
    /// <param name="resource">Readable caller-owned resource stream at the selected bytes.
    /// <para>定位在选定字节处的可读、调用方拥有的资源流。</para></param>
    /// <param name="length">Positive even byte count, at most 64 MiB; trailing bytes are not sent.
    /// <para>正偶数的字节数，最多 64 MiB；尾部多余字节不会被发送。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting, reading and communication.
    /// <para>用于等待、读取与通信的取消令牌。</para></param>
    /// <returns>A task that completes after the bootloader echoes the checksum for the full resource, reports a
    /// differing checksum as warning evidence, or reports <c>0x1D0C</c> (no authentication needed) for the
    /// authentication resource.
    /// <para>在 bootloader 回显完整资源校验和、把不同校验和记录为 warning 证据，或对认证资源上报
    /// <c>0x1D0C</c>（无需认证）后完成的异步任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> is null.
    /// <para><paramref name="resource"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is undefined or <paramref name="length"/>
    /// is not positive or exceeds 64 MiB.
    /// <para><paramref name="kind"/> 未定义，或 <paramref name="length"/> 不是正数或超过 64 MiB。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="length"/> is odd, or the resource is unreadable
    /// or shorter than the selection.
    /// <para><paramref name="length"/> 为奇数，或资源流不可读、短于选定范围。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not in the bootloader stage.
    /// <para>会话未处于 bootloader 阶段。</para></exception>
    /// <exception cref="MtkProtocolException">The bootloader rejected the request or returned a malformed frame;
    /// the session faults. A differing checksum is warning evidence only.
    /// <para>bootloader 拒绝请求或返回格式错误的帧；会话进入 fault 状态。校验和不同仅作为 warning 证据。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task UploadBootResourceByteStreamAsync(MtkBootResourceKind kind, Stream resource, int length,
        CancellationToken cancellationToken = default)
    {
        if (kind is not (MtkBootResourceKind.RootCertificate or MtkBootResourceKind.Authentication))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        ValidateBootResource(resource, length);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.Bootloader);
            cancellationToken.ThrowIfCancellationRequested();
            var buffer = new byte[1024];
            var terminalSlaRecorded = false;
            try
            {
                await EchoAsync([kind == MtkBootResourceKind.RootCertificate ? (byte)0xE0 : (byte)0xE2],
                    cancellationToken).ConfigureAwait(false);
                await EchoUInt32Async((uint)length, cancellationToken).ConfigureAwait(false);
                var initial = BinaryPrimitives.ReadUInt16BigEndian(
                    await ReadAsync(2, _timeoutMs, cancellationToken).ConfigureAwait(false));
                if (kind == MtkBootResourceKind.Authentication && initial == NoAuthenticationNeededStatus)
                {
                    RecordSlaEvidence(MtkSlaFlowKind.BootResourceAuthorization, MtkSlaEvidenceStage.ChallengeWaived,
                        null, CommandOutcomeKind.Completed,
                        $"no-auth-needed; the bootloader reported 0x{initial:X4} for the authentication resource.");
                    return;
                }

                if (initial > 0xFF || (kind == MtkBootResourceKind.Authentication && initial == 0xFF))
                {
                    RecordSlaEvidence(MtkSlaFlowKind.BootResourceAuthorization, MtkSlaEvidenceStage.Rejected,
                        null, CommandOutcomeKind.Rejected,
                        $"SlaRejected; the bootloader rejected the {kind} resource request (status 0x{initial:X4}).");
                    terminalSlaRecorded = true;
                    throw new MtkProtocolException("The bootloader rejected the security resource request.", initial);
                }
                RecordSlaEvidence(MtkSlaFlowKind.BootResourceAuthorization, MtkSlaEvidenceStage.DeviceRequired,
                    null, CommandOutcomeKind.Completed,
                    $"boot-resource-requested; the bootloader accepted the {kind} resource request.");
                ushort checksum = 0;
                var remaining = length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(buffer.Length, remaining);
                    await resource.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    for (var offset = 0; offset < count; offset += 2)
                    {
                        checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2));
                    }
                    await WriteAsync(buffer, count, _timeoutMs, cancellationToken).ConfigureAwait(false);
                    remaining -= count;
                }
                var response = await ReadAsync(4, _timeoutMs, cancellationToken).ConfigureAwait(false);
                var completedStatus = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2));
                if (kind == MtkBootResourceKind.Authentication && completedStatus == NoAuthenticationNeededStatus)
                {
                    RecordSlaEvidence(MtkSlaFlowKind.BootResourceAuthorization, MtkSlaEvidenceStage.ChallengeWaived,
                        null, CommandOutcomeKind.Completed,
                        $"no-auth-needed; the bootloader reported 0x{completedStatus:X4} after the authentication upload.");
                    return;
                }

                CheckStatus(completedStatus, "security resource upload");
                var reportedChecksum = BinaryPrimitives.ReadUInt16BigEndian(response);
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                RecordSlaEvidence(MtkSlaFlowKind.BootResourceAuthorization, MtkSlaEvidenceStage.ResponseAccepted,
                    null, CommandOutcomeKind.Completed,
                    reportedChecksum == 0
                        ? $"boot-resource-accepted; the bootloader reported a zero checksum for the {kind} resource."
                        : reportedChecksum == checksum
                            ? $"boot-resource-accepted; the bootloader echoed the {kind} resource checksum."
                            : $"warning: the bootloader reported checksum 0x{reportedChecksum:X4} instead of the computed " +
                              $"0x{checksum:X4} for the {kind} resource; continuing as the reference does.");
            }
            catch (Exception error)
            {
                if (!terminalSlaRecorded)
                {
                    var (stage, classification) = MtkSlaEvidence.ClassifyFailure(error, deadlineExpired: false);
                    RecordSlaEvidence(MtkSlaFlowKind.BootResourceAuthorization, stage, null,
                        stage switch
                        {
                            MtkSlaEvidenceStage.Timeout => CommandOutcomeKind.Timeout,
                            MtkSlaEvidenceStage.ProviderCancelled => CommandOutcomeKind.Cancelled,
                            _ => CommandOutcomeKind.Rejected
                        },
                        $"{classification}; {error.Message}");
                }
                _state = MtkSessionState.Faulted;
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    internal static void ValidateBootResource(Stream resource, int length)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaximumAgentLength);
        if ((length & 1) != 0 || !resource.CanRead
            || (resource.CanSeek && resource.Length - resource.Position < length))
        {
            throw new ArgumentException("The resource must contain the complete, even-length selection.", nameof(resource));
        }
    }
}