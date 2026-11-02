using UotanStudio.Comm.Diagnostics;

namespace UotanStudio.Comm.Protocols.Bootrom;

public sealed partial class RkdlSession
{
    /// <summary>
    /// Waits for a maintenance TEST_UNIT_READY sub code to finish, polling until the CSW reports
    /// success or the timeout elapses, like the official tool's progress prompt for the erase and
    /// format operations.
    /// <para>等待维护类 TEST_UNIT_READY 子码完成：持续轮询直到 CSW 报告成功或超时，与官方工具为擦除与
    /// 格式化操作提供的进度提示一致。</para>
    /// </summary>
    /// <remarks>
    /// The sub-code operations complete asynchronously inside the device, so the first CSW reports
    /// status 1 (not ready) and later polls report the operation's progress in the CSW data residue
    /// (current in the high 16 bits, total in the low 16 bits, both big-endian). A poll whose current
    /// progress exceeds the total is treated as a failure, matching
    /// <see cref="WaitForLoaderReadyAsync"/>.
    /// <para>子码操作在设备内部异步执行，因此第一条 CSW 报告状态 1（未就绪），后续轮询在 CSW 数据余量中报告
    /// 操作进度（当前进度在高 16 位、总进度在低 16 位，均为大端序）。当前进度超过总进度的轮询按失败处理，
    /// 与 <see cref="WaitForLoaderReadyAsync"/> 一致。</para>
    /// </remarks>
    /// <param name="subCode">A <see cref="RkdlTestUnitSubCode"/> value identifying the maintenance operation.
    /// <para>标识维护操作的 <see cref="RkdlTestUnitSubCode"/> 值。</para></param>
    /// <param name="operationName">The operation name used in progress traces. <para>进度跟踪使用的操作名。</para></param>
    /// <param name="timeoutMs">Total wait budget in milliseconds. <para>总等待预算（毫秒）。</para></param>
    /// <param name="progress">Optional progress receiver. <para>可选进度接收器。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns><c>true</c> when the operation completed. <para>操作完成时返回 <c>true</c>。</para></returns>
    private bool WaitForMaintenanceSubCode(byte subCode, string operationName, int timeoutMs = 120000,
        IProgress<(ushort Current, ushort Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = TestDeviceReady(subCode, out var current, out var total);
            if (ready)
            {
                if (total > 0)
                {
                    progress?.Report((total, total));
                }

                CommTrace.State("rkdl", $"TEST_UNIT_READY sub code 0x{subCode:X2} ({operationName}) completed.");
                return true;
            }

            if (current > total)
            {
                throw new RkdlException(RkdlError.DeviceUnready,
                    $"The {operationName} reported progress {current}/{total}, which exceeds the total.");
            }

            if (total > 0)
            {
                progress?.Report((current, total));
            }

            Thread.Sleep(1000);
        }

        throw new RkdlException(RkdlError.DeviceUnready,
            $"The {operationName} did not complete within {timeoutMs} ms.");
    }

    private static void RequireWipeConfirmation(string operation, string confirmationPhrase)
    {
        if (!string.Equals(confirmationPhrase, "WIPE", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                $"The {operation} erases storage on the device and requires the exact confirmation phrase \"WIPE\".");
        }
    }

    /// <summary>
    /// Erases the system disk through the TEST_UNIT_READY sub code
    /// <see cref="RkdlTestUnitSubCode.EraseSystem"/>, which asks the device's own firmware to wipe its
    /// system area.
    /// <para>通过 TEST_UNIT_READY 子码 <see cref="RkdlTestUnitSubCode.EraseSystem"/> 擦除系统盘，
    /// 交由设备自身固件擦除其系统区。</para>
    /// </summary>
    /// <param name="confirmationPhrase">The exact phrase <c>"WIPE"</c>; any other value refuses the operation
    /// before any byte reaches the device. <para>精确短语 <c>"WIPE"</c>；其它取值会在任何字节到达设备之前拒绝操作。</para></param>
    /// <param name="timeoutMs">Completion wait budget in milliseconds. <para>完成等待预算（毫秒）。</para></param>
    /// <param name="progress">Optional progress receiver. <para>可选进度接收器。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmationPhrase"/> is not exactly
    /// <c>"WIPE"</c>. <para><paramref name="confirmationPhrase"/> 不是精确的 <c>"WIPE"</c>。</para></exception>
    /// <exception cref="RkdlException">The transaction failed or the operation did not complete in time.
    /// <para>事务失败或操作未在时限内完成。</para></exception>
    public void EraseSystemDiskBySubCode(string confirmationPhrase, int timeoutMs = 120000,
        IProgress<(ushort Current, ushort Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireWipeConfirmation("TEST_UNIT_READY erase-system operation", confirmationPhrase);
        WaitForMaintenanceSubCode(RkdlTestUnitSubCode.EraseSystem, "erase-system operation", timeoutMs, progress, cancellationToken);
    }

    /// <summary>
    /// Lower-formats the storage through the TEST_UNIT_READY sub code
    /// <see cref="RkdlTestUnitSubCode.LowerFormat"/>, the most destructive maintenance operation the
    /// device firmware offers.
    /// <para>通过 TEST_UNIT_READY 子码 <see cref="RkdlTestUnitSubCode.LowerFormat"/> 对存储执行低级格式化，
    /// 这是设备固件提供的破坏性最强的维护操作。</para>
    /// </summary>
    /// <param name="confirmationPhrase">The exact phrase <c>"WIPE"</c>; any other value refuses the operation
    /// before any byte reaches the device. <para>精确短语 <c>"WIPE"</c>；其它取值会在任何字节到达设备之前拒绝操作。</para></param>
    /// <param name="timeoutMs">Completion wait budget in milliseconds. <para>完成等待预算（毫秒）。</para></param>
    /// <param name="progress">Optional progress receiver. <para>可选进度接收器。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmationPhrase"/> is not exactly
    /// <c>"WIPE"</c>. <para><paramref name="confirmationPhrase"/> 不是精确的 <c>"WIPE"</c>。</para></exception>
    /// <exception cref="RkdlException">The transaction failed or the operation did not complete in time.
    /// <para>事务失败或操作未在时限内完成。</para></exception>
    public void LowerFormatBySubCode(string confirmationPhrase, int timeoutMs = 120000,
        IProgress<(ushort Current, ushort Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireWipeConfirmation("TEST_UNIT_READY lower-format operation", confirmationPhrase);
        WaitForMaintenanceSubCode(RkdlTestUnitSubCode.LowerFormat, "lower-format operation", timeoutMs, progress, cancellationToken);
    }

    /// <summary>
    /// Erases the user-data area through the TEST_UNIT_READY sub code
    /// <see cref="RkdlTestUnitSubCode.EraseUserData"/>.
    /// <para>通过 TEST_UNIT_READY 子码 <see cref="RkdlTestUnitSubCode.EraseUserData"/> 擦除用户数据区。</para>
    /// </summary>
    /// <param name="confirmationPhrase">The exact phrase <c>"WIPE"</c>; any other value refuses the operation
    /// before any byte reaches the device. <para>精确短语 <c>"WIPE"</c>；其它取值会在任何字节到达设备之前拒绝操作。</para></param>
    /// <param name="timeoutMs">Completion wait budget in milliseconds. <para>完成等待预算（毫秒）。</para></param>
    /// <param name="progress">Optional progress receiver. <para>可选进度接收器。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <exception cref="UnauthorizedAccessException"><paramref name="confirmationPhrase"/> is not exactly
    /// <c>"WIPE"</c>. <para><paramref name="confirmationPhrase"/> 不是精确的 <c>"WIPE"</c>。</para></exception>
    /// <exception cref="RkdlException">The transaction failed or the operation did not complete in time.
    /// <para>事务失败或操作未在时限内完成。</para></exception>
    public void EraseUserDataBySubCode(string confirmationPhrase, int timeoutMs = 120000,
        IProgress<(ushort Current, ushort Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireWipeConfirmation("TEST_UNIT_READY erase-user-data operation", confirmationPhrase);
        WaitForMaintenanceSubCode(RkdlTestUnitSubCode.EraseUserData, "erase-user-data operation", timeoutMs, progress, cancellationToken);
    }

    /// <summary>
    /// Queries the user-data sector map through the TEST_UNIT_READY sub code
    /// <see cref="RkdlTestUnitSubCode.GetUserSector"/>, a read-only operation.
    /// <para>通过 TEST_UNIT_READY 子码 <see cref="RkdlTestUnitSubCode.GetUserSector"/> 查询用户数据扇区映射，
    /// 这是只读操作。</para>
    /// </summary>
    /// <returns><c>true</c> when the device answered ready. <para>设备就绪应答时返回 <c>true</c>。</para></returns>
    /// <exception cref="RkdlException">The transaction failed. <para>事务失败。</para></exception>
    public bool GetUserSectorMap()
    {
        ThrowIfDisposed();
        return TestDeviceReady(RkdlTestUnitSubCode.GetUserSector, out _, out _);
    }
}