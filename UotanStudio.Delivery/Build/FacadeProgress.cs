namespace UotanStudio.Services.Delivery;

/// <summary>
/// Public delivery progress contract: the internal <see cref="DeliveryProgress"/> flattened into a record that
/// crosses the assembly boundary so facade callers can drive progress bars and statistics. Tuple fields become
/// standalone scalars, so no ValueTuple is exposed on the public surface.
/// <para>公共交付进度契约：把内部 <see cref="DeliveryProgress"/> 摊平为可跨程序集边界的记录，供门面调用方
/// 驱动进度条与统计。元组字段被展开为独立标量，因此不向公共面暴露 ValueTuple。</para>
/// </summary>
/// <param name="Percent">
/// Percent progress; a negative value means the caller should keep the previously reported percentage.
/// <para>百分比进度；负值表示调用方应沿用既有百分比。</para>
/// </param>
/// <param name="Message">
/// Human-readable localized text that carries the final wording only when <paramref name="Stage"/> is null.
/// <para>已本地化的可读文本，仅当 <paramref name="Stage"/> 为 null 时携带最终文案。</para>
/// </param>
/// <param name="Stage">
/// Localized stage resource key; when non-null the caller translates the text by key and resets stage statistics.
/// <para>阶段本地化资源键；非空时调用方按键翻译文本并重置该阶段的统计。</para>
/// </param>
/// <param name="Detail">
/// Intra-stage detail text or resource key, passed through from the internal progress value.
/// <para>阶段内的明细文本或资源键，按内部进度原样透传。</para>
/// </param>
/// <param name="ProcessedBytes">
/// Bytes processed in this stage; null when the stage reports no byte meter.
/// <para>本阶段已处理字节数；无字节度量时为 null。</para>
/// </param>
/// <param name="TotalBytes">
/// Total bytes for this stage; null when the stage reports no byte meter.
/// <para>本阶段字节总量；无字节度量时为 null。</para>
/// </param>
/// <param name="ProcessedItems">
/// Items processed in this stage; null when the stage reports no item meter.
/// <para>本阶段已处理条目数；无条目度量时为 null。</para>
/// </param>
/// <param name="TotalItems">
/// Total items for this stage; null when the stage reports no item meter.
/// <para>本阶段条目总量；无条目度量时为 null。</para>
/// </param>
/// <param name="Metadata">
/// Additional metadata (for example equivalence report keys), passed through from the internal progress value.
/// <para>附加元数据（例如等价性报告键），按内部进度原样透传。</para>
/// </param>
public sealed record DeliveryBuildProgress(double Percent, string Message, string? Stage, string? Detail,
    long? ProcessedBytes, long? TotalBytes, long? ProcessedItems, long? TotalItems,
    IReadOnlyDictionary<string, string>? Metadata);

/// <summary>
/// Maps the internal progress record onto the public progress contract.
/// <para>内部进度到公共进度契约的映射。</para>
/// </summary>
internal static class DeliveryBuildProgressMapper
{
    extension(DeliveryProgress value)
    {
        /// <summary>
        /// Maps an internal <see cref="DeliveryProgress"/> onto <see cref="DeliveryBuildProgress"/> field by field.
        /// <para>把内部 <see cref="DeliveryProgress"/> 逐字段映射为 <see cref="DeliveryBuildProgress"/>。</para>
        /// </summary>
        internal DeliveryBuildProgress ToBuildProgress()
            => new(value.Percent, value.Message, value.Stage, value.Detail,
                value.Bytes?.Processed, value.Bytes?.Total, value.Items?.Processed, value.Items?.Total, value.Metadata);
    }

    extension(DeliveryBuildProgress value)
    {
        /// <summary>
        /// Restores the internal <see cref="DeliveryProgress"/> from the public contract so hosts that already consume
        /// the internal record keep one stage-statistics code path. Byte and item meters are rebuilt only when both
        /// components are present.
        /// <para>把公共进度契约还原为内部 <see cref="DeliveryProgress"/>，供已直接消费内部记录的宿主界面复用同一套
        /// 阶段统计逻辑。字节与条目度量只在两个分量同时存在时才重组为元组。</para>
        /// </summary>
        internal DeliveryProgress ToDeliveryProgress()
        {
            (long Processed, long Total)? bytes =
                value.ProcessedBytes is { } processedBytes && value.TotalBytes is { } totalBytes
                    ? (processedBytes, totalBytes) : null;
            (long Processed, long Total)? items =
                value.ProcessedItems is { } processedItems && value.TotalItems is { } totalItems
                    ? (processedItems, totalItems) : null;
            return new DeliveryProgress(value.Percent, value.Message, bytes, value.Stage, items, value.Detail, value.Metadata);
        }
    }
}