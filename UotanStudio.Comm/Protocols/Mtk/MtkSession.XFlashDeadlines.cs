namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Gets or sets the cooperative deadline for each XFlash storage read or write after acquiring the operation gate.
    /// The budget includes caller stream access, status frames and completion; it is captured once per range exchange.
    /// <para>获取或设置取得操作门后每次 XFlash 存储读写的协作式截止时间。
    /// 时间预算包括调用方流访问、状态帧与完成确认，在每次范围交换时捕获一次。</para></summary>
    /// <value>A budget from 1 ms to 24 hours, defaulting to 24 hours. Changes affect subsequent exchanges only.
    /// Erase operations use their explicit timeout argument instead.
    /// <para>1 毫秒至 24 小时的预算，默认 24 小时；修改仅影响后续交换。
    /// 擦除操作改用其显式超时参数。</para></value>
    /// <exception cref="ArgumentOutOfRangeException">The assigned budget is outside the supported range.
    /// <para>赋值的预算超出支持范围。</para></exception>
    public int XFlashStorageTransferTimeoutMs
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 86400000);
            field = value;
        }
    } = 86400000;

    private async Task RunXFlashStorageTransferAsync(Func<CancellationToken, Task> exchange, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(XFlashStorageTransferTimeoutMs);
        try
        {
            await exchange(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            FaultXFlashStorage();
            throw new TimeoutException("XFlash storage transfer completion was not confirmed before the deadline; reconnect before continuing.", error);
        }
    }
}