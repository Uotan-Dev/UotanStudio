namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// A monotonic counter identifying one transport instantiation of a physical device.
/// <para>标识物理设备一次传输实例化的单调计数器。</para>
/// Every successful native open advances the generation for the device identity, so a lease,
/// plan, or cached observation can prove which transport instantiation it was taken on and be
/// retired once that instantiation is gone. Generations are per identity and never comparable
/// across devices.
/// <para>每次成功的原生打开都会推进该设备身份的代际，因此租约、计划或缓存观测都能证明自己
/// 取自哪一次传输实例化，并在该实例化结束后被废弃。代际按身份独立计数，绝不在设备之间比较。</para>
/// </summary>
public readonly record struct TransportGeneration : IComparable<TransportGeneration>
{
    /// <summary>
    /// Initializes a generation with its raw counter value.
    /// <para>以原始计数值初始化代际。</para>
    /// </summary>
    /// <param name="value">
    /// The 1-based counter value; 0 is reserved for "no generation observed".
    /// <para>从 1 开始的计数值；0 保留表示“未观测到代际”。</para>
    /// </param>
    public TransportGeneration(ulong value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1UL);
        Value = value;
    }

    /// <summary>
    /// Gets the raw counter value.
    /// <para>获取原始计数值。</para>
    /// </summary>
    /// <value>The 1-based counter value. <para>从 1 开始的计数值。</para></value>
    public ulong Value { get; }

    /// <summary>
    /// Compares this generation with another of the same device identity.
    /// <para>在同一物理身份的代际之间比较大小。</para>
    /// </summary>
    /// <param name="other">
    /// The generation to compare with. <para>待比较的代际。</para>
    /// </param>
    /// <returns>An ordering over the counter values. <para>计数值上的排序结果。</para></returns>
    public int CompareTo(TransportGeneration other) => Value.CompareTo(other.Value);
}