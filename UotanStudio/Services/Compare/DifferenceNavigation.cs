namespace UotanStudio.Services.Compare;

/// <summary>
/// Finds the start of the adjacent contiguous difference without wrapping.
/// <para>查找相邻连续差异区段的起点，不循环跳转。</para>
/// </summary>
internal static class DifferenceNavigation
{
    internal static int Find(int count, int current, bool forward, Func<int, bool> isDifferent)
    {
        var index = current < 0 ? (forward ? 0 : count - 1) : Math.Min(current, count - 1);
        if (current >= 0)
        {
            while (index >= 0 && index < count && isDifferent(index))
            {
                index += forward ? 1 : -1;
            }
        }
        while (index >= 0 && index < count && !isDifferent(index))
        {
            index += forward ? 1 : -1;
        }
        if (index < 0 || index >= count)
        {
            return -1;
        }
        while (index > 0 && isDifferent(index - 1))
        {
            index--;
        }
        return index;
    }
}