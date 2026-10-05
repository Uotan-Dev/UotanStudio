namespace UotanStudio.Firmware.Core;

/// <summary>
/// Scalar alignment helpers for fixed-width binary layout offsets. The stream-padding equivalent
/// lives in <see cref="Streams.ZeroFill"/>; these compute an aligned value rather than writing it.
/// <para>定宽二进制布局偏移的标量对齐辅助方法。流式填充版本见 <see cref="Streams.ZeroFill"/>；此处计算对齐值而非写入。</para>
/// </summary>
internal static class Alignment
{
    /// <summary>
    /// Rounds <paramref name="value"/> up to the next multiple of <paramref name="alignment"/>,
    /// which must be a power of two. Overflow wraps.
    /// <para>将 <paramref name="value"/> 向上取整到 <paramref name="alignment"/> 的下一个倍数（须为 2 的幂）。溢出时回绕。</para>
    /// </summary>
    internal static uint Up(uint value, uint alignment) => (value + alignment - 1) & ~(alignment - 1);

    /// <summary>
    /// Rounds <paramref name="value"/> up to the next multiple of <paramref name="alignment"/>,
    /// throwing <see cref="OverflowException"/> when the rounding would exceed <see cref="uint.MaxValue"/>.
    /// <para>将 <paramref name="value"/> 向上取整到 <paramref name="alignment"/> 的下一个倍数；当取整将超出 <see cref="uint.MaxValue"/> 时抛出 <see cref="OverflowException"/>。</para>
    /// </summary>
    internal static uint UpChecked(uint value, uint alignment) => checked((value + alignment - 1) / alignment * alignment);
}